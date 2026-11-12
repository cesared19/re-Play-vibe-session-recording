using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using XRSessionCapture.SparkyTC;

namespace XRSessionCapture.Editor.SparkyTC
{
    /// <summary>
    /// Publishes chosen replays (from the Editor, the test runner or pulled from the headset) to SparkyTC as one
    /// automated test run for a build.
    /// </summary>
    public class PublishWindow : EditorWindow
    {
        sealed class Row
        {
            public PublishItem item;
            public bool selected;
        }

        readonly List<Row> m_Rows = new List<Row>();
        SparkyTCClient.Project m_Project;
        string[] m_Builds = Array.Empty<string>();
        int m_Build;
        string m_Title;
        string m_Status;
        MessageType m_StatusType = MessageType.None;
        string m_RunUrl;
        bool m_Busy;
        Vector2 m_Scroll;

        public static void Open()
        {
            var window = GetWindow<PublishWindow>(true, "Publish replays to SparkyTC");
            window.minSize = new Vector2(520, 420);
            window.Reload();
        }

        void OnEnable() => Reload();

        void Reload()
        {
            m_Rows.Clear();
            var root = SessionCaptureManager.RootDirectory;
            if (Directory.Exists(root))
            {
                var replayFolders = new List<string>();
                foreach (var capture in CaptureDirectories(root))
                {
                    var replays = Path.Combine(capture, SessionCaptureManager.ReplaysFolderName);
                    if (Directory.Exists(replays))
                        replayFolders.AddRange(Directory.GetDirectories(replays));
                }
                var items = SparkyTCPublisher.Plan(replayFolders);
                // the newest replay of each linked capture is chosen to start with
                var newestPerCapture = items.Where(i => i.skipReason == null)
                    .GroupBy(i => i.captureDirectory)
                    .Select(g => g.OrderBy(i => Path.GetFileName(i.replayDirectory), StringComparer.Ordinal).Last());
                // new HashSet(...) instead of ToHashSet(): Unity 2020.3 compiles against .NET Standard 2.0, which lacks it
                var newestSet = new HashSet<PublishItem>(newestPerCapture);
                foreach (var item in items.OrderByDescending(i => Path.GetFileName(i.replayDirectory), StringComparer.Ordinal))
                    m_Rows.Add(new Row { item = item, selected = newestSet.Contains(item) });
            }
            LoadProject();
        }

        static IEnumerable<string> CaptureDirectories(string root)
        {
            static bool IsCapture(string d) => File.Exists(Path.Combine(d, SessionFile.CaptureFileName));
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (IsCapture(directory))
                    yield return directory;
                else
                    foreach (var child in Directory.GetDirectories(directory).Where(IsCapture))
                        yield return child;
            }
        }

        async void LoadProject()
        {
            if (!SparkyTCSettings.IsConfigured)
            {
                SetStatus("Set the SparkyTC URL and a Read and write token in Window → XR Session Capture first.", MessageType.Warning);
                return;
            }
            try
            {
                m_Project = await SparkyTCSettings.CreateClient().GetProjectAsync();
                m_Builds = (m_Project.builds ?? Array.Empty<SparkyTCClient.Build>()).Select(b => b.buildNumber).ToArray();
                m_Build = Math.Max(0, Array.IndexOf(m_Builds, m_Project.currentBuild?.buildNumber));
                if (m_Builds.Length == 0)
                    SetStatus($"{m_Project.title} has no builds. An admin registers them in SparkyTC's Builds tab.", MessageType.Warning);
                else if (!m_Project.CanWrite)
                    SetStatus("This token can only read. Publishing needs a Read and write token.", MessageType.Warning);
                else
                    SetStatus(null, MessageType.None);
            }
            catch (SparkyTCException e)
            {
                SetStatus(e.Message, MessageType.Warning);
            }
            Repaint();
        }

        void SetStatus(string message, MessageType type)
        {
            m_Status = message;
            m_StatusType = type;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField(m_Project != null ? $"{m_Project.title} ({m_Project.projectShortCode})" : "SparkyTC", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("One automated test run with a result per replay: Pass when it matched the recording, Fail otherwise, with its report, screenshots and the replay as .zip.", EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(m_Busy))
            {
                using (new EditorGUI.DisabledScope(m_Builds.Length == 0))
                    m_Build = EditorGUILayout.Popup(new GUIContent("Build", "The build these replays tested. Builds are registered in SparkyTC."), m_Build, m_Builds);
                m_Title = EditorGUILayout.TextField(new GUIContent("Run title", "Empty: \"re-Play <build> · <date>\"."), m_Title);

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Replays ({m_Rows.Count(r => r.selected)} chosen)", EditorStyles.boldLabel);
                    if (GUILayout.Button("Reload", GUILayout.Width(70)))
                        Reload();
                }
                if (m_Rows.Count == 0)
                    EditorGUILayout.HelpBox("No replays yet. Replay captures in the Editor or on the headset (then pull them) first.", MessageType.Info);

                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                foreach (var row in m_Rows)
                {
                    var item = row.item;
                    using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUI.DisabledScope(item.skipReason != null))
                            row.selected = EditorGUILayout.Toggle(row.selected && item.skipReason == null, GUILayout.Width(18));
                        using (new EditorGUILayout.VerticalScope())
                        {
                            var verdict = item.report == null ? "" : item.report.passed ? "  ✓ passed" : "  ✗ failed";
                            EditorGUILayout.LabelField(item.Key + verdict, EditorStyles.miniBoldLabel);
                            EditorGUILayout.LabelField(item.skipReason != null
                                ? "Can't be published: " + item.skipReason
                                : $"→ {item.link.DisplayCode} {item.link.title} · {item.report.replayDevice}", EditorStyles.miniLabel);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();

                var chosen = m_Rows.Where(r => r.selected && r.item.skipReason == null).Select(r => r.item).ToList();
                using (new EditorGUI.DisabledScope(chosen.Count == 0 || m_Project == null || !m_Project.CanWrite || m_Builds.Length == 0))
                {
                    if (GUILayout.Button(m_Busy ? "Publishing…" : $"Publish {chosen.Count} replay{(chosen.Count == 1 ? "" : "s")} to SparkyTC", GUILayout.Height(28)))
                        Publish(chosen);
                }
            }

            if (!string.IsNullOrEmpty(m_Status))
                EditorGUILayout.HelpBox(m_Status, m_StatusType);
            if (!string.IsNullOrEmpty(m_RunUrl) && GUILayout.Button("Open the test run in SparkyTC"))
                Application.OpenURL(m_RunUrl);
        }

        async void Publish(List<PublishItem> chosen)
        {
            m_Busy = true;
            m_RunUrl = null;
            var progress = new Progress<string>(message =>
            {
                SetStatus(message, MessageType.None);
                Repaint();
            });
            try
            {
                // planned again, so a choice made before doesn't carry over
                var items = SparkyTCPublisher.Plan(chosen.Select(i => i.replayDirectory));
                var outcome = await SparkyTCPublisher.PublishAsync(SparkyTCSettings.CreateClient(), items, m_Builds[m_Build], m_Title, SparkyTCSettings.Url, progress);
                m_RunUrl = outcome.testRunUrl;
                var run = outcome.testRun;
                var message = outcome.testRun.created
                    ? $"Published test run {run.testRunNumber} for build {run.build?.buildNumber}: {outcome.recorded} result{(outcome.recorded == 1 ? "" : "s")}."
                    : outcome.recorded == 0
                        ? $"These replays were already published as test run {run.testRunNumber}; nothing new was sent."
                        : $"Test run {run.testRunNumber} was published before; {outcome.recorded} missing result{(outcome.recorded == 1 ? " was" : "s were")} added.";
                if (outcome.skipped.Count > 0)
                    message += "\nSkipped: " + string.Join("; ", outcome.skipped.Select(i => $"{i.Key} ({i.skipReason})"));
                SetStatus(message, MessageType.Info);
            }
            catch (SparkyTCException e)
            {
                SetStatus(e.Message, MessageType.Warning);
            }
            catch (Exception e)
            {
                SetStatus("Publishing stopped: " + e.Message, MessageType.Error);
                Debug.LogException(e);
            }
            finally
            {
                m_Busy = false;
                Repaint();
            }
        }
    }
}
