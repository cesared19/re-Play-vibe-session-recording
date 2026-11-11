// Only in the Editor (the window and tests): the headset never talks to SparkyTC.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace XRSessionCapture.SparkyTC
{
    /// <summary>A replay ready to publish, or skipped and why.</summary>
    public class PublishItem
    {
        public string captureDirectory;
        public string replayDirectory;
        public ReplayReport report;
        public SparkyTCLink link;
        /// <summary>Why it isn't published (no link, no report); null when it's ready.</summary>
        public string skipReason;

        /// <summary>"capture/replay", stable for the same replay on any PC.</summary>
        public string Key => Path.GetFileName(captureDirectory) + "/" + Path.GetFileName(replayDirectory);
    }

    public class PublishOutcome
    {
        public SparkyTCClient.TestRun testRun;
        public string testRunUrl;
        public int recorded;
        /// <summary>Test cases that already had their result from an earlier publish of the same replays.</summary>
        public int alreadyPublished;
        public List<PublishItem> skipped = new List<PublishItem>();
    }

    /// <summary>
    /// Publishes replays to SparkyTC as one automated test run for a build: a result per replay (Pass when the replay
    /// passed, Fail otherwise) with its findings, duration and report, and as evidence every screenshot of the
    /// recording and the replay with its diff, plus the replay's folder as a .zip. Runs on the PC only.
    /// </summary>
    public static class SparkyTCPublisher
    {
        /// <summary>The replays in these folders, each with its capture's link; unlinked ones are marked as skipped.</summary>
        public static List<PublishItem> Plan(IEnumerable<string> replayDirectories)
        {
            var items = new List<PublishItem>();
            foreach (var replayDirectory in replayDirectories.Select(Path.GetFullPath).Distinct())
            {
                // <capture>/replays/<replay>
                var captureDirectory = Path.GetDirectoryName(Path.GetDirectoryName(replayDirectory));
                var item = new PublishItem { captureDirectory = captureDirectory, replayDirectory = replayDirectory };
                try
                {
                    item.report = ReplayReport.Load(replayDirectory);
                }
                catch (Exception e)
                {
                    item.skipReason = "its report can't be read: " + e.Message;
                }
                if (item.report == null && item.skipReason == null)
                    item.skipReason = "it has no report (the replay didn't finish)";
                item.link = SparkyTCLink.Load(captureDirectory);
                if (item.link == null && item.skipReason == null)
                    item.skipReason = "its capture isn't linked to a SparkyTC test case";
                items.Add(item);
            }
            return items;
        }

        /// <summary>
        /// Publishes the ready items as one automated run for <paramref name="buildNumber"/>. Checks the token and the
        /// build first, so nothing is published if either is wrong. Publishing the same replays again returns the same
        /// run and only fills in what's missing.
        /// </summary>
        public static async Task<PublishOutcome> PublishAsync(SparkyTCClient client, IReadOnlyList<PublishItem> items, string buildNumber, string title, string webUrl, IProgress<string> progress = null)
        {
            var outcome = new PublishOutcome { skipped = items.Where(i => i.skipReason != null).ToList() };
            // one result per test case: the newest replay of it (a run completes once every test case has one)
            var ready = new List<PublishItem>();
            foreach (var group in items.Where(i => i.skipReason == null).GroupBy(i => i.link.testCaseNumber))
            {
                var ordered = group.OrderBy(i => i.report.startedUtc, StringComparer.Ordinal).ToList();
                ready.Add(ordered[ordered.Count - 1]);
                foreach (var older in ordered.Take(ordered.Count - 1))
                {
                    older.skipReason = "a newer replay of the same test case is in this batch";
                    outcome.skipped.Add(older);
                }
            }
            ready = ready.OrderBy(i => i.link.testCaseNumber).ToList();
            if (ready.Count == 0)
                throw new SparkyTCException("None of these replays can be published: link their captures to SparkyTC test cases first.");
            buildNumber = buildNumber?.Trim();
            if (string.IsNullOrEmpty(buildNumber))
                throw new SparkyTCException("Choose the build these replays tested.");

            progress?.Report("Checking the token and the build…");
            var project = await client.GetProjectAsync().ConfigureAwait(false);
            if (!project.CanWrite)
                throw new SparkyTCException("This token can only read. Publishing needs a Read and write token (Settings → API tokens in SparkyTC).");
            var build = project.builds?.FirstOrDefault(b => string.Equals(b.buildNumber, buildNumber, StringComparison.OrdinalIgnoreCase));
            if (build == null)
                throw new SparkyTCException($"Build {buildNumber} isn't registered in SparkyTC ({project.title}). An admin adds it in the project's Builds tab; nothing was published.");

            var numbers = ready.Select(i => i.link.testCaseNumber).ToList();
            var keys = ready.Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var description = "Published by re-Play from " + ready.Count + " replay" + (ready.Count == 1 ? "" : "s") + ":\n" + string.Join("\n", keys.Select(k => "- " + k));

            progress?.Report("Creating the test run…");
            var run = await client.CreateTestRunAsync(
                string.IsNullOrWhiteSpace(title) ? DefaultTitle(build.buildNumber) : title.Trim(),
                build.buildNumber, numbers, description, SourceRef(keys)).ConfigureAwait(false);
            outcome.testRun = run;
            outcome.testRunUrl = string.IsNullOrEmpty(webUrl) ? null : webUrl.TrimEnd('/') + run.webPath;

            // published before: only the test cases still without a result get one
            var pending = new HashSet<long>((run.testCases ?? Array.Empty<SparkyTCClient.TestRunCase>())
                .Where(c => c.status == "NOT_RUN").Select(c => c.testCaseNumber));
            for (var index = 0; index < ready.Count; index++)
            {
                var item = ready[index];
                var number = item.link.testCaseNumber;
                if (!pending.Contains(number))
                {
                    outcome.alreadyPublished++;
                    continue;
                }
                progress?.Report($"Recording {item.Key} ({index + 1}/{ready.Count})…");
                var result = await client.RecordResultAsync(run.testRunNumber, number,
                    item.report.passed ? "PASS" : "FAIL", Notes(item.report), DurationSeconds(item.report), ReportJson(item.report)).ConfigureAwait(false);
                outcome.recorded++;

                progress?.Report($"Uploading the evidence of {item.Key}…");
                foreach (var batch in Evidence(item))
                    await client.UploadEvidenceAsync(run.testRunNumber, result.resultID, batch).ConfigureAwait(false);
            }
            return outcome;
        }

        public static string DefaultTitle(string buildNumber) =>
            $"re-Play {buildNumber} · {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";

        /// <summary>The same replays always give the same reference, so publishing them again doesn't create another run.</summary>
        public static string SourceRef(IEnumerable<string> sortedKeys)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", sortedKeys)));
                return "re-play:" + BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static string Notes(ReplayReport report)
        {
            var total = report.screenshots.Count;
            var matching = total - report.FailedScreenshots;
            var sb = new StringBuilder();
            sb.Append(report.passed ? "Replay passed" : "Replay failed");
            if (total > 0)
                sb.Append($": {matching}/{total} screenshots match the recording");
            sb.Append($". {report.framesReplayed} frames replayed on {report.replayDevice} ({report.replayTiming}).");
            foreach (var finding in report.findings)
                sb.Append("\n- ").Append(finding);
            var notes = sb.ToString();
            return notes.Length <= 10_000 ? notes : notes.Substring(0, 9_990) + "…";
        }

        static long? DurationSeconds(ReplayReport report)
        {
            if (!DateTime.TryParse(report.startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
                || !DateTime.TryParse(report.finishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end))
                return null;
            var seconds = (long)Math.Round((end - start).TotalSeconds);
            return seconds >= 0 && seconds <= 24 * 3600 ? seconds : (long?)null;
        }

        /// <summary>The report as SparkyTC stores it: findings, screenshots and recording vs replay metrics.</summary>
        public static Json ReportJson(ReplayReport report)
        {
            return new Json()
                .Add("passed", report.passed)
                .Add("findings", report.findings.Take(500).Select(f => f.Length <= 1000 ? f : f.Substring(0, 999) + "…"))
                .Add("screenshots", report.screenshots.Take(500).Select(s => new Json()
                    .Add("name", s.name)
                    .Add("differentPercent", s.missing || s.sizeMismatch ? (double?)null : Math.Round(Math.Min(100, Math.Max(0, (double)s.differentPercent)), 4))
                    .Add("passed", s.passed)))
                .Add("metrics", new Json()
                    .Add("recorded", Metrics(report.recordedMetrics))
                    .Add("replay", Metrics(report.replayMetrics)));
        }

        static Json Metrics(MetricsSummary m)
        {
            // JsonUtility fills a missing summary with an empty one
            if (m == null || m.frames == 0)
                return null;
            // re-Play writes -1 for what the device didn't report
            double? V(float value) => value < 0 || float.IsNaN(value) ? (double?)null : Math.Round(value, 3);
            return new Json()
                .Add("averageFps", V(m.averageFps))
                .Add("frameTimeP50Ms", V(m.frameTimeP50Ms))
                .Add("frameTimeP95Ms", V(m.frameTimeP95Ms))
                .Add("frameTimeP99Ms", V(m.frameTimeP99Ms))
                .Add("overBudgetPercent", V(m.overBudgetPercent))
                .Add("droppedFrames", m.droppedFrames < 0 ? (long?)null : m.droppedFrames)
                .Add("cpuAvgMs", V(m.cpuAvgMs))
                .Add("gpuAvgMs", V(m.gpuAvgMs))
                .Add("usedMemoryPeakMB", V(m.usedMemoryPeakMB))
                .Add("batteryTemperatureMaxC", V(m.batteryTemperatureMaxC));
        }

        /// <summary>
        /// The evidence, in a few uploads: each screenshot's recording, replay and diff images, named
        /// "&lt;screenshot&gt;.recording.png" and so on (SparkyTC links them from the report), then the .zip.
        /// </summary>
        static IEnumerable<List<SparkyTCClient.Evidence>> Evidence(PublishItem item)
        {
            foreach (var shot in item.report.screenshots)
            {
                var files = new List<SparkyTCClient.Evidence>();
                AddImage(files, shot.name, "recording", shot.baseline);
                AddImage(files, shot.name, "replay", shot.actual);
                AddImage(files, shot.name, "diff", shot.diff);
                if (files.Count > 0)
                    yield return files;
            }
            yield return new List<SparkyTCClient.Evidence>
            {
                new SparkyTCClient.Evidence
                {
                    fileName = $"replay-{Path.GetFileName(item.captureDirectory)}-{Path.GetFileName(item.replayDirectory)}.zip",
                    contentType = "application/zip",
                    data = ReplayZip(item),
                },
            };
        }

        static void AddImage(List<SparkyTCClient.Evidence> files, string name, string kind, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            files.Add(new SparkyTCClient.Evidence { fileName = $"{name}.{kind}.png", contentType = "image/png", data = File.ReadAllBytes(path) });
        }

        /// <summary>
        /// The replay's folder and the recording's screenshots, laid out like the capture folder so report.html shows
        /// its images when opened from the extracted .zip.
        /// </summary>
        public static byte[] ReplayZip(PublishItem item)
        {
            using (var buffer = new MemoryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    var replayRoot = $"{SessionCaptureManager.ReplaysFolderName}/{Path.GetFileName(item.replayDirectory)}";
                    AddFolder(zip, item.replayDirectory, replayRoot);
                    AddFolder(zip, Path.Combine(item.captureDirectory, SessionCaptureManager.ShotsFolderName), SessionCaptureManager.ShotsFolderName);
                    foreach (var name in new[] { MetricsLog.SummaryFileName, SparkyTCLink.FileName })
                    {
                        var path = Path.Combine(item.captureDirectory, name);
                        if (File.Exists(path))
                            zip.CreateEntryFromFile(path, name, System.IO.Compression.CompressionLevel.Optimal);
                    }
                }
                return buffer.ToArray();
            }
        }

        static void AddFolder(ZipArchive zip, string folder, string entryRoot)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(folder.Length).TrimStart('/', '\\').Replace('\\', '/');
                zip.CreateEntryFromFile(file, entryRoot + "/" + relative, System.IO.Compression.CompressionLevel.Optimal);
            }
        }

        /// <summary>
        /// For unattended runs (the regression tests, CI): publishes when SPARKYTC_URL (or SPARKYTC_API_URL),
        /// SPARKYTC_TOKEN and SPARKYTC_BUILD are set, and returns null when they aren't. SPARKYTC_RUN_TITLE is optional.
        /// </summary>
        public static async Task<PublishOutcome> PublishFromEnvironmentAsync(IEnumerable<string> replayDirectories)
        {
            var url = Environment.GetEnvironmentVariable("SPARKYTC_URL")?.Trim();
            var apiUrl = Environment.GetEnvironmentVariable("SPARKYTC_API_URL")?.Trim();
            var token = Environment.GetEnvironmentVariable("SPARKYTC_TOKEN")?.Trim();
            var build = Environment.GetEnvironmentVariable("SPARKYTC_BUILD")?.Trim();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(build) || (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(apiUrl)))
                return null;
            var client = new SparkyTCClient(string.IsNullOrEmpty(apiUrl) ? url : apiUrl, token);
            var progress = new Progress<string>(message => Debug.Log("[XRSessionCapture] " + message));
            return await PublishAsync(client, Plan(replayDirectories), build, Environment.GetEnvironmentVariable("SPARKYTC_RUN_TITLE"), url, progress).ConfigureAwait(false);
        }
    }
}
#endif
