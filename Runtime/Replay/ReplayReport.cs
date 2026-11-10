using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace XRSessionCapture
{
    /// <summary>Result of replaying a capture: screenshot comparisons and metrics of the recording versus the replay.</summary>
    [Serializable]
    public class ReplayReport
    {
        public const string JsonFileName = "report.json";
        public const string HtmlFileName = "report.html";

        public string captureId;
        public string capturePath;
        public string replayDirectory;
        public string startedUtc;
        public string finishedUtc;
        public string replayDevice;
        public string replayTiming;
        public bool captureWasTruncated;
        public int framesReplayed;
        public bool passed;
        public List<ImageCompareResult> screenshots = new List<ImageCompareResult>();
        public MetricsSummary recordedMetrics;
        public MetricsSummary replayMetrics;
        /// <summary>Human-readable notes: failed screenshots and performance regressions.</summary>
        public List<string> findings = new List<string>();

        public int FailedScreenshots
        {
            get
            {
                var n = 0;
                foreach (var s in screenshots)
                    if (!s.passed)
                        n++;
                return n;
            }
        }

        public void Save()
        {
            File.WriteAllText(Path.Combine(replayDirectory, JsonFileName), JsonUtility.ToJson(this, true));
            File.WriteAllText(Path.Combine(replayDirectory, HtmlFileName), ToHtml(), new UTF8Encoding(false));
        }

        public static ReplayReport Load(string directory)
        {
            var path = Path.Combine(directory, JsonFileName);
            return File.Exists(path) ? JsonUtility.FromJson<ReplayReport>(File.ReadAllText(path)) : null;
        }

        string Relative(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            var from = new Uri(replayDirectory.TrimEnd('/', '\\') + "/");
            return Uri.UnescapeDataString(from.MakeRelativeUri(new Uri(path)).ToString());
        }

        static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

        static string F(float v, string unit = "") => v < 0 || float.IsNaN(v) ? "—" : v.ToString("0.##", CultureInfo.InvariantCulture) + unit;

        string ToHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>Replay ").Append(E(captureId)).Append("</title><style>");
            sb.Append(":root{color-scheme:light dark;--ok:#15803d;--bad:#b91c1c;--line:#8884}body{font:14px/1.45 system-ui,sans-serif;margin:0;padding:16px;max-width:1200px;margin:auto}");
            sb.Append("h1{font-size:20px}h2{font-size:16px;margin-top:28px}.badge{display:inline-block;padding:2px 10px;border-radius:999px;color:#fff;font-weight:600}");
            sb.Append(".ok{background:var(--ok)}.bad{background:var(--bad)}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left}");
            sb.Append("th{font-weight:600}.num{text-align:right;font-variant-numeric:tabular-nums}.shots{display:grid;gap:8px;grid-template-columns:repeat(auto-fit,minmax(240px,1fr))}");
            sb.Append("figure{margin:0}img{width:100%;height:auto;border:1px solid var(--line)}figcaption{font-size:12px;opacity:.8}.wrap{overflow-x:auto}</style></head><body>");

            sb.Append("<h1>Replay of ").Append(E(captureId)).Append(" <span class=\"badge ").Append(passed ? "ok\">Passed" : "bad\">Failed").Append("</span></h1>");
            sb.Append("<p>").Append(E(startedUtc)).Append(" · ").Append(E(replayDevice)).Append(" · ").Append(E(replayTiming)).Append(" · ").Append(framesReplayed).Append(" frames");
            if (captureWasTruncated)
                sb.Append(" · <strong>the capture was cut short while recording</strong>");
            sb.Append("</p>");

            if (findings.Count > 0)
            {
                sb.Append("<h2>Findings</h2><ul>");
                foreach (var f in findings)
                    sb.Append("<li>").Append(E(f)).Append("</li>");
                sb.Append("</ul>");
            }

            sb.Append("<h2>Performance</h2><div class=\"wrap\"><table><tr><th>Metric</th><th class=\"num\">Recording</th><th class=\"num\">Replay</th></tr>");
            Row(sb, "Average FPS", recordedMetrics?.averageFps ?? -1, replayMetrics?.averageFps ?? -1, "");
            Row(sb, "Frame time p50", recordedMetrics?.frameTimeP50Ms ?? -1, replayMetrics?.frameTimeP50Ms ?? -1, " ms");
            Row(sb, "Frame time p95", recordedMetrics?.frameTimeP95Ms ?? -1, replayMetrics?.frameTimeP95Ms ?? -1, " ms");
            Row(sb, "Frame time p99", recordedMetrics?.frameTimeP99Ms ?? -1, replayMetrics?.frameTimeP99Ms ?? -1, " ms");
            Row(sb, "Frames over budget", recordedMetrics?.overBudgetPercent ?? -1, replayMetrics?.overBudgetPercent ?? -1, " %");
            Row(sb, "Dropped frames (XR runtime)", recordedMetrics?.droppedFrames ?? -1, replayMetrics?.droppedFrames ?? -1, "");
            Row(sb, "CPU avg", recordedMetrics?.cpuAvgMs ?? -1, replayMetrics?.cpuAvgMs ?? -1, " ms");
            Row(sb, "GPU avg", recordedMetrics?.gpuAvgMs ?? -1, replayMetrics?.gpuAvgMs ?? -1, " ms");
            Row(sb, "Memory peak", recordedMetrics?.usedMemoryPeakMB ?? -1, replayMetrics?.usedMemoryPeakMB ?? -1, " MB");
            Row(sb, "Battery temperature max", recordedMetrics?.batteryTemperatureMaxC ?? -1, replayMetrics?.batteryTemperatureMaxC ?? -1, " °C");
            sb.Append("</table></div>");

            sb.Append("<h2>Screenshots (").Append(screenshots.Count - FailedScreenshots).Append("/").Append(screenshots.Count).Append(" match)</h2>");
            foreach (var s in screenshots)
            {
                sb.Append("<h3>").Append(E(s.name)).Append(" <span class=\"badge ").Append(s.passed ? "ok\">Match" : "bad\">Different").Append("</span></h3><p>");
                if (s.missing)
                    sb.Append("Screenshot missing from the recording or the replay.");
                else if (s.sizeMismatch)
                    sb.Append("The images have different sizes.");
                else
                    sb.Append(s.differentPercent.ToString("0.###", CultureInfo.InvariantCulture)).Append("% of pixels differ (").Append(s.differentPixels).Append(" of ").Append(s.totalPixels).Append(").");
                sb.Append("</p><div class=\"shots\">");
                Figure(sb, "Recording", s.baseline);
                Figure(sb, "Replay", s.actual);
                Figure(sb, "Differences in red", s.diff);
                sb.Append("</div>");
            }
            sb.Append("</body></html>");
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string name, float recorded, float replay, string unit)
        {
            sb.Append("<tr><td>").Append(E(name)).Append("</td><td class=\"num\">").Append(F(recorded, unit)).Append("</td><td class=\"num\">").Append(F(replay, unit)).Append("</td></tr>");
        }

        void Figure(StringBuilder sb, string caption, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            sb.Append("<figure><img loading=\"lazy\" src=\"").Append(E(Relative(path))).Append("\" alt=\"").Append(E(caption)).Append("\"><figcaption>").Append(E(caption)).Append("</figcaption></figure>");
        }
    }
}
