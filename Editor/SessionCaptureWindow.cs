using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using XRSessionCapture.SparkyTC;
using Debug = UnityEngine.Debug;
using XRSessionCapture.Editor.SparkyTC;

namespace XRSessionCapture.Editor
{
    /// <summary>Window &gt; XR Session Capture: browse captures, pull them from the headset, replay them in the Editor or on the headset.</summary>
    public sealed class SessionCaptureWindow : EditorWindow
    {
        const string PackageIdPref = "XRSessionCapture.PackageId";
        const string AdbPathPref = "XRSessionCapture.AdbPath";

        sealed class CaptureEntry
        {
            public string directory;
            public string id;
            public SessionInfo info;
            public int frames;
            public double duration;
            public int screenshots;
            public List<string> notes = new List<string>();
            public bool truncated;
            public string error;
            public List<(string directory, ReplayReport report)> replays = new List<(string, ReplayReport)>();
            public bool expanded;
            public SparkyTCLink link;
        }

        readonly List<CaptureEntry> m_Captures = new List<CaptureEntry>();
        Vector2 m_Scroll;
        string m_PackageId;
        string m_AdbPath;
        string m_Status;

        // SparkyTC: the project the token is for, checked with "Connect"
        bool m_ShowSparkyTC;
        string m_SparkyTCStatus;
        MessageType m_SparkyTCStatusType = MessageType.None;
        SparkyTCClient.Project m_SparkyTCProject;
        bool m_Connecting;

        [MenuItem("Window/XR Session Capture")]
        public static void Open()
        {
            var window = GetWindow<SessionCaptureWindow>("XR Session Capture");
            window.minSize = new Vector2(420, 300);
            window.Refresh();
        }

        [MenuItem("GameObject/XR/Session Capture", false, 10)]
        static void CreateManager(MenuCommand command)
        {
            if (UnityEngine.Object.FindObjectOfType<SessionCaptureManager>() != null)
            {
                Selection.activeObject = UnityEngine.Object.FindObjectOfType<SessionCaptureManager>();
                EditorUtility.DisplayDialog("XR Session Capture", "This scene already has a Session Capture manager.", "OK");
                return;
            }
            var go = new GameObject("XR Session Capture");
            go.AddComponent<SessionCaptureManager>();
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create XR Session Capture");
            Selection.activeObject = go;
        }

        void OnEnable()
        {
            m_PackageId = EditorPrefs.GetString(PackageIdPref, PlayerSettings.applicationIdentifier);
            m_AdbPath = EditorPrefs.GetString(AdbPathPref, DefaultAdbPath());
            m_ShowSparkyTC = SparkyTCSettings.IsConfigured;
            Refresh();
        }

        void OnFocus() => Refresh();

        void Refresh()
        {
            m_Captures.Clear();
            var root = SessionCaptureManager.RootDirectory;
            if (!Directory.Exists(root))
                return;
            foreach (var directory in CaptureDirectories(root))
            {
                var file = Path.Combine(directory, SessionFile.CaptureFileName);
                var entry = new CaptureEntry
                {
                    directory = directory,
                    // relative to SessionCaptures, e.g. "regression/20261110-153012_Main"
                    id = directory.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/'),
                    link = SparkyTCLink.Load(directory),
                };
                try
                {
                    var capture = SessionCapture.Load(file);
                    entry.info = capture.info;
                    entry.frames = capture.frames.Count;
                    entry.duration = capture.Duration;
                    entry.truncated = capture.truncated;
                    entry.screenshots = capture.markers.Count(m => m.kind == MarkerKind.Screenshot);
                    entry.notes = capture.markers.Where(m => m.kind == MarkerKind.Note).Select(m => $"{m.time:0.0}s  {m.name}").ToList();
                }
                catch (Exception e)
                {
                    entry.error = e.Message;
                }
                var replays = Path.Combine(directory, SessionCaptureManager.ReplaysFolderName);
                if (Directory.Exists(replays))
                {
                    foreach (var r in Directory.GetDirectories(replays).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                        entry.replays.Add((r, SafeLoadReport(r)));
                }
                m_Captures.Add(entry);
            }
        }

        /// <summary>Capture folders in SessionCaptures, and one level down in folders that group them (e.g. regression/), newest first.</summary>
        static IEnumerable<string> CaptureDirectories(string root)
        {
            static bool IsCapture(string d) => File.Exists(Path.Combine(d, SessionFile.CaptureFileName));
            var found = new List<string>();
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (IsCapture(directory))
                    found.Add(directory);
                else
                    found.AddRange(Directory.GetDirectories(directory).Where(IsCapture));
            }
            return found.OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
        }

        static ReplayReport SafeLoadReport(string directory)
        {
            try
            {
                return ReplayReport.Load(directory);
            }
            catch
            {
                return null;
            }
        }

        void OnGUI()
        {
            var root = SessionCaptureManager.RootDirectory;
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(root, EditorStyles.miniLabel);
                if (GUILayout.Button("Open folder", GUILayout.Width(90)))
                {
                    Directory.CreateDirectory(root);
                    EditorUtility.RevealInFinder(root);
                }
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    Refresh();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Headset (adb)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_PackageId = EditorGUILayout.TextField(new GUIContent("App package", "Android package of the app on the headset (Player Settings > Package Name)."), m_PackageId);
            m_AdbPath = EditorGUILayout.TextField(new GUIContent("adb", "Path to adb. Empty or 'adb' uses the one on PATH."), m_AdbPath);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PackageIdPref, m_PackageId);
                EditorPrefs.SetString(AdbPathPref, m_AdbPath);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Pull captures from headset", "Copies SessionCaptures from the app's folder on the headset into this project.")))
                    PullFromHeadset();
                if (GUILayout.Button("List devices"))
                    RunAdb("devices -l", out _);
            }
            if (!string.IsNullOrEmpty(m_Status))
                EditorGUILayout.HelpBox(m_Status, MessageType.None);

            EditorGUILayout.Space(4);
            DrawSparkyTCSettings();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"Captures ({m_Captures.Count})", EditorStyles.boldLabel);
            if (m_Captures.Count == 0)
                EditorGUILayout.HelpBox("No captures yet. Add GameObject > XR > Session Capture to your first scene, build to the headset and record with left Menu + Y (F9 in the Editor). Then pull the captures from the headset here.", MessageType.Info);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var entry in m_Captures.ToArray())
                DrawCapture(entry);
            EditorGUILayout.EndScrollView();
        }

        void DrawCapture(CaptureEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var last = entry.replays.Select(r => r.report).FirstOrDefault(r => r != null);
                var badge = last == null ? "" : last.passed ? "  ✓ last replay passed" : "  ✗ last replay failed";
                entry.expanded = EditorGUILayout.Foldout(entry.expanded, entry.id + badge, true);
                if (entry.error != null)
                {
                    EditorGUILayout.HelpBox("Cannot read: " + entry.error, MessageType.Error);
                    return;
                }
                var info = entry.info;
                EditorGUILayout.LabelField($"{info?.sceneName} · {info?.deviceModel} · {entry.duration:0.0} s · {entry.frames} frames · {entry.screenshots} screenshots" + (entry.truncated ? " · cut short" : ""), EditorStyles.miniLabel);
                DrawSparkyTCLink(entry);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode && SessionCaptureManager.Instance == null))
                    {
                        if (GUILayout.Button(new GUIContent("Replay in Editor", "Enters Play Mode in the open scene and replays this capture. Open the scene it was recorded in first.")))
                            ReplayInEditor(entry);
                    }
                    if (GUILayout.Button(new GUIContent("Replay on headset", "Pushes the capture to the headset and starts the app with it.")))
                        ReplayOnHeadset(entry);
                    if (GUILayout.Button("Folder", GUILayout.Width(60)))
                        EditorUtility.RevealInFinder(Path.Combine(entry.directory, SessionFile.CaptureFileName));
                    if (GUILayout.Button("Delete", GUILayout.Width(60))
                        && EditorUtility.DisplayDialog("Delete capture", $"Delete {entry.id} and its replays?", "Delete", "Cancel"))
                    {
                        Directory.Delete(entry.directory, true);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }

                if (!entry.expanded)
                    return;

                if (info != null)
                {
                    EditorGUILayout.LabelField("Recorded", $"{info.createdUtc} on {info.deviceModel} ({info.xrDevice}), {info.applicationId} {info.applicationVersion}, Unity {info.unityVersion}", EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(info.notes))
                        EditorGUILayout.LabelField("Notes", info.notes, EditorStyles.wordWrappedMiniLabel);
                }
                foreach (var note in entry.notes)
                    EditorGUILayout.LabelField("•", note, EditorStyles.miniLabel);

                var metrics = Path.Combine(entry.directory, MetricsLog.SummaryFileName);
                if (File.Exists(metrics))
                {
                    var s = JsonUtility.FromJson<MetricsSummary>(File.ReadAllText(metrics));
                    EditorGUILayout.LabelField("Performance", string.Format(CultureInfo.InvariantCulture,
                        "{0:0.0} fps avg · p95 {1:0.0} ms · {2:0.#}% over budget · {3} dropped", s.averageFps, s.frameTimeP95Ms, s.overBudgetPercent, s.droppedFrames), EditorStyles.miniLabel);
                }

                if (entry.replays.Count > 0)
                {
                    EditorGUILayout.LabelField("Replays", EditorStyles.miniBoldLabel);
                    foreach (var (directory, report) in entry.replays)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var label = Path.GetFileName(directory) + (report == null ? "  (no report)" : $"  {(report.passed ? "passed" : "FAILED")} · {report.screenshots.Count - report.FailedScreenshots}/{report.screenshots.Count} screenshots · {report.replayDevice}");
                            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
                            var html = Path.Combine(directory, ReplayReport.HtmlFileName);
                            using (new EditorGUI.DisabledScope(!File.Exists(html)))
                            {
                                if (GUILayout.Button("Report", EditorStyles.miniButton, GUILayout.Width(60)))
                                    Application.OpenURL(new Uri(html).AbsoluteUri);
                            }
                        }
                    }
                }
            }
        }

        static void ReplayInEditor(CaptureEntry entry)
        {
            var path = Path.Combine(entry.directory, SessionFile.CaptureFileName);
            if (EditorApplication.isPlaying)
            {
                var manager = SessionCaptureManager.Instance;
                if (manager != null && !manager.StartReplay(path))
                    EditorUtility.DisplayDialog("XR Session Capture", "Stop the current recording or replay first.", "OK");
                return;
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (entry.info != null && !string.IsNullOrEmpty(entry.info.sceneName) && entry.info.sceneName != scene
                && !EditorUtility.DisplayDialog("XR Session Capture", $"This capture was recorded in scene '{entry.info.sceneName}', but '{scene}' is open. Replay anyway?", "Replay", "Cancel"))
                return;
            SessionState.SetString(SessionCaptureManager.EditorPendingReplayKey, path);
            EditorApplication.EnterPlaymode();
        }

        void PullFromHeadset()
        {
            var root = SessionCaptureManager.RootDirectory;
            Directory.CreateDirectory(root);
            var remote = $"/sdcard/Android/data/{m_PackageId}/files/{SessionCaptureManager.FolderName}/.";
            if (RunAdb($"pull \"{remote}\" \"{root}\"", out _))
                m_Status = "Captures copied to " + root;
            Refresh();
        }

        void ReplayOnHeadset(CaptureEntry entry)
        {
            var remoteRoot = $"/sdcard/Android/data/{m_PackageId}/files/{SessionCaptureManager.FolderName}";
            var remote = remoteRoot + "/" + entry.id;
            RunAdb($"shell mkdir -p \"{remote}/{SessionCaptureManager.ShotsFolderName}\"", out _);
            if (!RunAdb($"push \"{Path.Combine(entry.directory, SessionFile.CaptureFileName)}\" \"{remote}/\"", out _))
                return;
            var summary = Path.Combine(entry.directory, MetricsLog.SummaryFileName);
            if (File.Exists(summary))
                RunAdb($"push \"{summary}\" \"{remote}/\"", out _);
            var shots = Path.Combine(entry.directory, SessionCaptureManager.ShotsFolderName);
            if (Directory.Exists(shots))
                RunAdb($"push \"{shots}/.\" \"{remote}/{SessionCaptureManager.ShotsFolderName}/\"", out _);

            // Launch activity: ask the package manager, fall back to Unity's default activity.
            var activity = m_PackageId + "/com.unity3d.player.UnityPlayerActivity";
            if (RunAdb($"shell cmd package resolve-activity --brief {m_PackageId}", out var resolved, logErrors: false))
            {
                var line = resolved.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("/"));
                if (!string.IsNullOrEmpty(line))
                    activity = line;
            }
            if (RunAdb($"shell am start -S -n {activity} -e xrscReplay {entry.id}", out _))
                m_Status = $"Replaying {entry.id} on the headset. When it finishes, use \"Pull captures from headset\" to get the report.";
        }

        bool RunAdb(string arguments, out string output, bool logErrors = true)
        {
            var adb = string.IsNullOrWhiteSpace(m_AdbPath) ? "adb" : m_AdbPath;
            output = string.Empty;
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(adb, arguments)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000))
                    {
                        process.Kill();
                        m_Status = $"adb {arguments} did not finish in 2 minutes.";
                        return false;
                    }
                    output = stdout.Result;
                    var errors = stderr.Result;
                    var ok = process.ExitCode == 0;
                    m_Status = (ok ? "" : "adb failed: ") + (output + errors).Trim();
                    if (!ok && logErrors)
                        Debug.LogWarning($"[XRSessionCapture] adb {arguments}\n{output}{errors}");
                    return ok;
                }
            }
            catch (Exception e)
            {
                m_Status = $"Could not run adb ({adb}): {e.Message}. Set the adb path above (Android SDK platform-tools).";
                return false;
            }
        }

        // ---------- SparkyTC ----------

        void DrawSparkyTCSettings()
        {
            m_ShowSparkyTC = EditorGUILayout.Foldout(m_ShowSparkyTC, "SparkyTC" + (m_SparkyTCProject != null ? $" · {m_SparkyTCProject.title} ({m_SparkyTCProject.projectShortCode})" : string.Empty), true, EditorStyles.foldoutHeader);
            if (!m_ShowSparkyTC)
                return;

            EditorGUILayout.LabelField("Link captures to test cases, and publish replays as automated test runs. Kept on this PC only.", EditorStyles.wordWrappedMiniLabel);
            EditorGUI.BeginChangeCheck();
            var url = EditorGUILayout.TextField(new GUIContent("SparkyTC URL", "The address you open SparkyTC at, e.g. http://192.168.1.20:3000."), SparkyTCSettings.Url);
            var apiUrl = EditorGUILayout.TextField(new GUIContent("API URL (optional)", "Only if the integration API answers somewhere else, e.g. http://localhost:8080 through an SSH tunnel to the server."), SparkyTCSettings.ApiUrl);
            var token = EditorGUILayout.PasswordField(new GUIContent("Project API token", "stc_... An admin creates it in SparkyTC: the project's Settings → API tokens. Read is enough to link; publishing needs Read and write."), SparkyTCSettings.Token);
            if (EditorGUI.EndChangeCheck())
            {
                SparkyTCSettings.Url = url;
                SparkyTCSettings.ApiUrl = apiUrl;
                SparkyTCSettings.Token = token;
                m_SparkyTCProject = null;
                m_SparkyTCStatus = null;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Connecting || !SparkyTCSettings.IsConfigured))
                {
                    if (GUILayout.Button(m_Connecting ? "Connecting…" : "Connect", GUILayout.Width(110)))
                        ConnectToSparkyTC();
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(SparkyTCSettings.Url)))
                {
                    if (GUILayout.Button("Open SparkyTC", GUILayout.Width(110)))
                        Application.OpenURL(SparkyTCSettings.Url);
                }
                using (new EditorGUI.DisabledScope(!SparkyTCSettings.IsConfigured))
                {
                    if (GUILayout.Button(new GUIContent("Publish replays…", "Publish replays of linked captures to SparkyTC as an automated test run for a build.")))
                        PublishWindow.Open();
                }
            }
            if (!string.IsNullOrEmpty(m_SparkyTCStatus))
                EditorGUILayout.HelpBox(m_SparkyTCStatus, m_SparkyTCStatusType);
        }

        async void ConnectToSparkyTC()
        {
            m_Connecting = true;
            m_SparkyTCStatus = null;
            Repaint();
            try
            {
                var project = await SparkyTCSettings.CreateClient().GetProjectAsync();
                m_SparkyTCProject = project;
                m_SparkyTCStatus = $"Connected to {project.title} ({project.projectShortCode}). " +
                    (project.CanWrite ? "This token can also publish results." : "This token reads only: enough to link captures; publishing needs Read and write.");
                m_SparkyTCStatusType = MessageType.Info;
            }
            catch (SparkyTCException e)
            {
                m_SparkyTCProject = null;
                m_SparkyTCStatus = e.Message;
                m_SparkyTCStatusType = MessageType.Warning;
            }
            catch (Exception e)
            {
                m_SparkyTCProject = null;
                m_SparkyTCStatus = "Couldn't talk to SparkyTC: " + e.Message;
                m_SparkyTCStatusType = MessageType.Warning;
            }
            finally
            {
                m_Connecting = false;
                Repaint();
            }
        }

        void DrawSparkyTCLink(CaptureEntry entry)
        {
            var link = entry.link;
            if (link == null && !SparkyTCSettings.IsConfigured)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (link != null)
                {
                    EditorGUILayout.LabelField(new GUIContent($"SparkyTC: {link.DisplayCode} {link.title}", link.sectionPath), EditorStyles.miniLabel);
                    var web = SparkyTCSettings.WebUrl(link.webPath);
                    using (new EditorGUI.DisabledScope(web == null))
                    {
                        if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(50)))
                            Application.OpenURL(web);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("SparkyTC: not linked", EditorStyles.miniLabel);
                }

                using (new EditorGUI.DisabledScope(!SparkyTCSettings.IsConfigured))
                {
                    var rect = GUILayoutUtility.GetRect(new GUIContent(link == null ? "Link…" : "Change…"), EditorStyles.miniButton, GUILayout.Width(70));
                    if (GUI.Button(rect, link == null ? "Link…" : "Change…", EditorStyles.miniButton))
                        LinkCapture(entry, rect);
                }
                if (link != null && GUILayout.Button("Unlink", EditorStyles.miniButton, GUILayout.Width(55))
                    && EditorUtility.DisplayDialog("Unlink capture", $"Unlink {entry.id} from {link.DisplayCode}? Its replays won't be published to SparkyTC.", "Unlink", "Cancel"))
                {
                    SparkyTCLink.Delete(entry.directory);
                    entry.link = null;
                }
            }
        }

        void LinkCapture(CaptureEntry entry, Rect buttonRect)
        {
            PopupWindow.Show(buttonRect, new TestCasePicker((project, testCase) =>
            {
                m_SparkyTCProject = project;
                var link = SparkyTCLink.From(project.projectShortCode, testCase);
                link.Save(entry.directory);
                entry.link = link;
                Repaint();
            }));
        }

        static string DefaultAdbPath()
        {
            var sdk = EditorPrefs.GetString("AndroidSdkRoot");
            if (!string.IsNullOrEmpty(sdk))
            {
                var adb = Path.Combine(sdk, "platform-tools", Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb");
                if (File.Exists(adb))
                    return adb;
            }
            return "adb";
        }
    }
}
