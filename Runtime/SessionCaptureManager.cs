using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace XRSessionCapture
{
    public enum CaptureState
    {
        Idle,
        Recording,
        Replaying,
        /// <summary>Replay finished; waiting for screenshots to be written and comparing them.</summary>
        Comparing,
    }

    /// <summary>
    /// Records XR sessions (headset and controller poses, buttons, screenshots, metrics), replays them through virtual
    /// Input System devices and compares the result. Add it once to your first scene (or use the menu
    /// GameObject &gt; XR &gt; Session Capture).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("XR/Session Capture")]
    public sealed class SessionCaptureManager : MonoBehaviour
    {
        public const string FolderName = "SessionCaptures";
        public const string ReplaysFolderName = "replays";
        public const string ShotsFolderName = "shots";
        public const string DiffFolderName = "diff";
        const string LaunchArgReplay = "xrscReplay";
        const string LaunchArgQuit = "xrscQuit";

        [Header("General")]
        [Tooltip("Headset camera. Empty: Camera.main.")]
        public Camera headCamera;
        public bool dontDestroyOnLoad = true;
        public bool showOverlay = true;
        public OverlayAnchor overlayAnchor = OverlayAnchor.LeftWrist;
        [Range(0.3f, 3f)] public float overlayScale = 1f;

        [Header("Recording")]
        [Tooltip("Start recording as soon as this component starts. Recording from scene load makes replays line up best.")]
        public bool recordOnStart;
        [Tooltip("Seed UnityEngine.Random when recording and replaying, so random gameplay repeats.")]
        public bool useFixedRandomSeed = true;
        public int randomSeed = 12345;
        [Tooltip("Take a screenshot every N seconds while recording (0 = only when asked).")]
        [Min(0)] public float autoScreenshotInterval;
        public Vector2Int screenshotSize = new Vector2Int(1280, 720);
        [Range(30, 120)] public float screenshotFieldOfView = 90f;

        [Header("Replay")]
        public ReplayOptions replayOptions = new ReplayOptions();
        [Tooltip("Capture to replay when this component starts: a capture folder name (inside SessionCaptures), a folder path or a .xrsc file.")]
        public string replayOnStart;
        [Tooltip("A pixel differs when a color channel differs by more than this (0–1).")]
        [Range(0f, 1f)] public float pixelThreshold = 0.06f;
        [Tooltip("A screenshot matches when at most this percentage of its pixels differ.")]
        [Range(0f, 100f)] public float maxDifferentPixelsPercent = 0.5f;
        [Tooltip("Report a regression when the replay's p95 frame time is this much higher than the recording's (only meaningful on the same device).")]
        [Min(0)] public float performanceRegressionPercent = 20f;
        public bool failOnPerformanceRegression;
        [Tooltip("Quit the app when a replay finishes (exit code 0 if it passed, 1 if not). For automated runs.")]
        public bool quitWhenReplayFinishes;

        [Header("Controls (defaults: hold left Menu + X / Y / trigger, or F8 / F9 / F10)")]
        public InputAction toggleOverlayAction;
        public InputAction toggleRecordingAction;
        public InputAction screenshotAction;

        public static SessionCaptureManager Instance { get; private set; }

        /// <summary>Capture to replay when the next SessionCaptureManager starts (tests set this before loading the scene).</summary>
        public static string PendingReplayPath { get; set; }

#if UNITY_EDITOR
        /// <summary>SessionState key the Editor window uses to ask for a replay when entering Play Mode.</summary>
        public const string EditorPendingReplayKey = "XRSessionCapture.PendingReplay";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void CreateForEditorReplay()
        {
            var pending = UnityEditor.SessionState.GetString(EditorPendingReplayKey, string.Empty);
            if (string.IsNullOrEmpty(pending))
                return;
            UnityEditor.SessionState.EraseString(EditorPendingReplayKey);
            PendingReplayPath = pending;
            // The scene may not have a manager: add one so the replay runs anyway.
            if (FindObjectOfType<SessionCaptureManager>() == null)
                new GameObject("XR Session Capture").AddComponent<SessionCaptureManager>();
        }
#endif

        public event Action<string> RecordingStarted;
        public event Action<string> RecordingStopped;
        public event Action<ReplayReport> ReplayFinished;

        public CaptureState State { get; private set; }
        public MetricsFrame LatestMetrics { get; private set; }
        public int DroppedFramesTotal { get; private set; }
        public int RecordedFrames => m_RecordedFrame;
        public float RecordingSeconds => State == CaptureState.Recording ? (float)(Time.unscaledTimeAsDouble - m_RecordStart) : 0f;
        public float ReplayProgress => m_Player != null ? m_Player.Progress : 0f;
        public ReplayReport LastReport { get; private set; }
        public bool? LastReportPassed => LastReport?.passed;

        /// <summary>Folder of the capture being recorded, or of the replay output while replaying.</summary>
        public string CurrentDirectory { get; private set; }

        public Camera HeadCamera => headCamera != null ? headCamera : Camera.main;

        /// <summary>Where captures are stored: persistentDataPath/SessionCaptures on the headset, &lt;project&gt;/SessionCaptures in the Editor.</summary>
        public static string RootDirectory
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", FolderName));
#else
                return Path.Combine(Application.persistentDataPath, FolderName);
#endif
            }
        }

        MetricsSampler m_Sampler;
        MetricsLog m_MetricsLog;
        MetricsOverlay m_Overlay;
        ScreenshotCapture m_Screenshots;

        // Recording
        SessionWriter m_Writer;
        SessionInfo m_Info;
        double m_RecordStart;
        int m_RecordedFrame;
        float m_NextAutoScreenshot;
        string m_PendingScreenshot;
        readonly List<string> m_PendingNotes = new List<string>();

        // Replay
        SessionPlayer m_Player;
        string m_CaptureDirectory;
        int m_LastHandledReplayFrame;
        int m_ReplayedFrames;
        readonly List<Marker> m_ReplayScreenshots = new List<Marker>();
        int m_NextReplayScreenshot;
        ReplayReport m_Report;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[XRSessionCapture] There is already a SessionCaptureManager; removing the duplicate on " + name);
                Destroy(this);
                return;
            }
            Instance = this;
            if (dontDestroyOnLoad)
            {
                transform.SetParent(null);
                DontDestroyOnLoad(gameObject);
            }

            ReplayLayouts.Register();
            m_Sampler = new MetricsSampler();
            m_Overlay = gameObject.AddComponent<MetricsOverlay>();
            m_Overlay.anchor = overlayAnchor;
            m_Overlay.scale = overlayScale;
            m_Overlay.Initialize(this);
            m_Overlay.Visible = showOverlay;

            SetUpActions();
        }

        void Start()
        {
            var replay = !string.IsNullOrEmpty(PendingReplayPath) ? PendingReplayPath : LaunchArgument(LaunchArgReplay);
            PendingReplayPath = null;
            if (string.IsNullOrEmpty(replay))
                replay = replayOnStart;
            if (LaunchArgument(LaunchArgQuit) != null)
                quitWhenReplayFinishes = true;

            if (!string.IsNullOrEmpty(replay))
                StartReplay(replay);
            else if (recordOnStart)
                StartRecording();
        }

        void OnDestroy()
        {
            if (Instance != this)
                return;
            StopRecording();
            StopReplay();
            DisposeActions();
            m_Sampler?.Dispose();
            m_Screenshots?.Dispose();
            Instance = null;
        }

        void OnApplicationPause(bool paused)
        {
            // Taking the headset off pauses the app on Android (and it may be killed while paused): put what was recorded on disk.
            if (paused && State == CaptureState.Recording)
            {
                m_Writer?.Flush();
                m_MetricsLog?.Flush();
            }
        }

        // ---------- Controls ----------

        void SetUpActions()
        {
            toggleOverlayAction = DefaultIfEmpty(toggleOverlayAction, "Toggle overlay", "{PrimaryButton}", "f8");
            toggleRecordingAction = DefaultIfEmpty(toggleRecordingAction, "Toggle recording", "{SecondaryButton}", "f9");
            screenshotAction = DefaultIfEmpty(screenshotAction, "Screenshot", "{TriggerButton}", "f10");
            toggleOverlayAction.performed += OnToggleOverlay;
            toggleRecordingAction.performed += OnToggleRecording;
            screenshotAction.performed += OnScreenshot;
            toggleOverlayAction.Enable();
            toggleRecordingAction.Enable();
            screenshotAction.Enable();
        }

        static InputAction DefaultIfEmpty(InputAction action, string name, string leftButton, string key)
        {
            if (action != null && action.bindings.Count > 0)
                return action;
            action = new InputAction(name, InputActionType.Button);
            action.AddCompositeBinding("ButtonWithOneModifier")
                .With("Modifier", "<XRController>{LeftHand}/{MenuButton}")
                .With("Button", "<XRController>{LeftHand}/" + leftButton);
            action.AddBinding("<Keyboard>/" + key);
            return action;
        }

        void DisposeActions()
        {
            if (toggleOverlayAction != null) { toggleOverlayAction.performed -= OnToggleOverlay; toggleOverlayAction.Disable(); }
            if (toggleRecordingAction != null) { toggleRecordingAction.performed -= OnToggleRecording; toggleRecordingAction.Disable(); }
            if (screenshotAction != null) { screenshotAction.performed -= OnScreenshot; screenshotAction.Disable(); }
        }

        // Replayed controllers press the same buttons that were pressed while recording; ignore them.
        static bool FromReplay(InputAction.CallbackContext context) => context.control != null && (context.control.device is ReplayController || context.control.device is ReplayHMD);

        void OnToggleOverlay(InputAction.CallbackContext context)
        {
            if (FromReplay(context))
                return;
            showOverlay = !showOverlay;
            m_Overlay.Visible = showOverlay;
        }

        void OnToggleRecording(InputAction.CallbackContext context)
        {
            if (FromReplay(context))
                return;
            if (State == CaptureState.Recording)
                StopRecording();
            else if (State == CaptureState.Replaying)
                StopReplay();
            else if (State == CaptureState.Idle)
                StartRecording();
        }

        void OnScreenshot(InputAction.CallbackContext context)
        {
            if (FromReplay(context))
                return;
            TakeScreenshot();
        }

        // ---------- Recording ----------

        /// <summary>Starts a new capture. Returns its folder, or null if something else is running.</summary>
        public string StartRecording(string notes = null)
        {
            if (State != CaptureState.Idle)
                return null;
            var scene = SceneManager.GetActiveScene().name;
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Sanitize(string.IsNullOrEmpty(scene) ? "scene" : scene);
            var directory = Path.Combine(RootDirectory, id);
            Directory.CreateDirectory(directory);

            m_Info = new SessionInfo
            {
                id = id,
                createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                deviceModel = SystemInfo.deviceModel,
                deviceName = SystemInfo.deviceName,
                operatingSystem = SystemInfo.operatingSystem,
                xrDevice = UnityEngine.XR.XRSettings.loadedDeviceName,
                applicationId = Application.identifier,
                applicationVersion = Application.version,
                unityVersion = Application.unityVersion,
                sceneName = scene,
                displayRefreshRate = LatestMetrics.refreshRate,
                screenshotWidth = screenshotSize.x,
                screenshotHeight = screenshotSize.y,
                usedFixedSeed = useFixedRandomSeed,
                randomSeed = randomSeed,
                notes = notes,
            };
            if (useFixedRandomSeed)
                UnityEngine.Random.InitState(randomSeed);

            m_Writer = new SessionWriter(Path.Combine(directory, SessionFile.CaptureFileName), m_Info);
            m_MetricsLog = new MetricsLog(directory);
            EnsureScreenshots();
            m_RecordStart = Time.unscaledTimeAsDouble;
            m_RecordedFrame = 0;
            DroppedFramesTotal = 0;
            m_NextAutoScreenshot = autoScreenshotInterval > 0 ? autoScreenshotInterval : float.PositiveInfinity;
            m_PendingScreenshot = null;
            m_PendingNotes.Clear();
            CurrentDirectory = directory;
            State = CaptureState.Recording;
            Debug.Log($"[XRSessionCapture] Recording to {directory}");
            RecordingStarted?.Invoke(directory);
            return directory;
        }

        public void StopRecording()
        {
            if (State != CaptureState.Recording)
                return;
            m_Writer?.Dispose();
            FinishRecording();
        }

        void FinishRecording()
        {
            m_Writer = null;
            var summary = m_MetricsLog?.Close();
            m_MetricsLog = null;
            State = CaptureState.Idle;
            var directory = CurrentDirectory;
            Debug.Log($"[XRSessionCapture] Recorded {m_RecordedFrame} frames ({RecordingSecondsAtStop():0.0} s) to {directory}" + (summary != null ? $", {summary.averageFps:0.0} fps average" : string.Empty));
            RecordingStopped?.Invoke(directory);
        }

        double RecordingSecondsAtStop() => Time.unscaledTimeAsDouble - m_RecordStart;

        /// <summary>Takes a screenshot at the end of this frame. While recording, it becomes a checkpoint that replays compare against.</summary>
        public void TakeScreenshot(string name = null)
        {
            if (State == CaptureState.Recording)
            {
                m_PendingScreenshot = string.IsNullOrEmpty(name) ? "shot" : name;
                return;
            }
            if (State == CaptureState.Idle)
            {
                // Outside a recording: a plain screenshot in SessionCaptures/screenshots.
                EnsureScreenshots();
                var path = Path.Combine(RootDirectory, "screenshots", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "_" + Sanitize(name ?? "shot") + ".png");
                CaptureWithoutOverlay(path);
            }
        }

        /// <summary>Adds a note to the capture at the current frame (shown in the Editor window).</summary>
        public void AddNote(string text)
        {
            if (State == CaptureState.Recording && !string.IsNullOrEmpty(text))
                m_PendingNotes.Add(text);
        }

        void RecordFrame()
        {
            var time = Time.unscaledTimeAsDouble - m_RecordStart;
            var sample = new FrameSample
            {
                frame = m_RecordedFrame,
                time = time,
                deltaTime = Time.unscaledDeltaTime,
                head = XRInputReader.ReadHead(),
                left = XRInputReader.ReadController(true),
                right = XRInputReader.ReadController(false),
            };
            m_Writer.WriteFrame(sample);

            foreach (var note in m_PendingNotes)
                m_Writer.WriteMarker(new Marker { frame = m_RecordedFrame, time = time, kind = MarkerKind.Note, name = note });
            m_PendingNotes.Clear();

            if (m_PendingScreenshot == null && time >= m_NextAutoScreenshot)
            {
                m_PendingScreenshot = "auto";
                m_NextAutoScreenshot += autoScreenshotInterval;
            }
            if (m_PendingScreenshot != null)
            {
                var marker = new Marker { frame = m_RecordedFrame, time = time, kind = MarkerKind.Screenshot, name = m_PendingScreenshot };
                m_Writer.WriteMarker(marker);
                CaptureWithoutOverlay(ScreenshotPath(CurrentDirectory, marker));
                m_PendingScreenshot = null;
            }
            m_RecordedFrame++;
        }

        public static string ScreenshotPath(string directory, Marker marker)
        {
            return Path.Combine(directory, ShotsFolderName, marker.frame.ToString("D6", CultureInfo.InvariantCulture) + "_" + Sanitize(marker.name) + ".png");
        }

        void EnsureScreenshots()
        {
            if (m_Screenshots == null)
                m_Screenshots = new ScreenshotCapture(screenshotSize.x, screenshotSize.y, screenshotFieldOfView);
        }

        void CaptureWithoutOverlay(string path)
        {
            m_Overlay.SetRenderersHidden(true);
            try
            {
                m_Screenshots.Capture(HeadCamera, path);
            }
            finally
            {
                m_Overlay.SetRenderersHidden(false);
            }
        }

        // ---------- Replay ----------

        /// <summary>
        /// Replays a capture: a folder name inside <see cref="RootDirectory"/>, a folder path or a .xrsc path.
        /// Returns false if it cannot start (something else is running, or the capture cannot be read).
        /// </summary>
        public bool StartReplay(string capture)
        {
            if (State != CaptureState.Idle)
                return false;
            var path = ResolveCapturePath(capture);
            if (path == null)
            {
                Debug.LogError($"[XRSessionCapture] Capture not found: {capture}");
                return false;
            }

            SessionCapture data;
            try
            {
                data = SessionCapture.Load(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[XRSessionCapture] Could not read {path}: {e.Message}");
                return false;
            }
            if (data.frames.Count == 0)
            {
                Debug.LogError($"[XRSessionCapture] {path} has no frames.");
                return false;
            }

            m_CaptureDirectory = Path.GetDirectoryName(path);
            var replayDirectory = Path.Combine(m_CaptureDirectory, ReplaysFolderName, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(replayDirectory);
            CurrentDirectory = replayDirectory;

            if (data.info != null && data.info.usedFixedSeed)
                UnityEngine.Random.InitState(data.info.randomSeed);

            // Use the recording's screenshot settings so the images are comparable.
            if (data.info != null && data.info.screenshotWidth > 0 && data.info.screenshotHeight > 0
                && (data.info.screenshotWidth != screenshotSize.x || data.info.screenshotHeight != screenshotSize.y))
            {
                screenshotSize = new Vector2Int(data.info.screenshotWidth, data.info.screenshotHeight);
                m_Screenshots?.Dispose();
                m_Screenshots = null;
            }
            EnsureScreenshots();

            m_ReplayScreenshots.Clear();
            foreach (var marker in data.markers)
                if (marker.kind == MarkerKind.Screenshot)
                    m_ReplayScreenshots.Add(marker);
            m_ReplayScreenshots.Sort((a, b) => a.frame.CompareTo(b.frame));
            m_NextReplayScreenshot = 0;
            m_LastHandledReplayFrame = -1;
            m_ReplayedFrames = 0;
            DroppedFramesTotal = 0;

            m_Report = new ReplayReport
            {
                captureId = data.info?.id ?? Path.GetFileName(m_CaptureDirectory),
                capturePath = path,
                replayDirectory = replayDirectory,
                startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                replayDevice = Application.isEditor ? "Unity Editor" : SystemInfo.deviceModel,
                replayTiming = replayOptions.timing.ToString(),
                captureWasTruncated = data.truncated,
            };

            m_MetricsLog = new MetricsLog(replayDirectory);
            m_Player = new SessionPlayer(data, replayOptions);
            m_Player.Start();
            State = CaptureState.Replaying;
            Debug.Log($"[XRSessionCapture] Replaying {path} ({data.frames.Count} frames, {data.Duration:0.0} s) into {replayDirectory}");
            return true;
        }

        /// <summary>Stops a replay early. The report is still written, with what was replayed.</summary>
        public void StopReplay()
        {
            if (State != CaptureState.Replaying || m_Player == null)
                return;
            m_Player.Stop();
            BeginComparing();
        }

        public static string ResolveCapturePath(string capture)
        {
            if (string.IsNullOrEmpty(capture))
                return null;
            var candidates = new[] { capture, Path.Combine(RootDirectory, capture) };
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return Path.GetFullPath(c);
                var inFolder = Path.Combine(c, SessionFile.CaptureFileName);
                if (File.Exists(inFolder))
                    return Path.GetFullPath(inFolder);
            }
            return null;
        }

        void ReplayFrame()
        {
            var current = m_Player.CurrentRecordedFrame;
            if (current == m_LastHandledReplayFrame)
                return;
            m_LastHandledReplayFrame = current;
            m_ReplayedFrames++;
            // RealTime timing can skip recorded frames; take every screenshot whose frame has been reached.
            while (m_NextReplayScreenshot < m_ReplayScreenshots.Count && m_ReplayScreenshots[m_NextReplayScreenshot].frame <= current)
            {
                CaptureWithoutOverlay(ScreenshotPath(CurrentDirectory, m_ReplayScreenshots[m_NextReplayScreenshot]));
                m_NextReplayScreenshot++;
            }
        }

        void BeginComparing()
        {
            m_Player = null;
            m_Report.framesReplayed = m_ReplayedFrames;
            m_Report.replayMetrics = m_MetricsLog?.Close();
            m_MetricsLog = null;
            State = CaptureState.Comparing;
        }

        void FinishComparing()
        {
            var report = m_Report;
            report.recordedMetrics = LoadSummary(m_CaptureDirectory);

            foreach (var marker in m_ReplayScreenshots)
            {
                var baseline = ScreenshotPath(m_CaptureDirectory, marker);
                var actual = ScreenshotPath(report.replayDirectory, marker);
                var diff = Path.Combine(report.replayDirectory, DiffFolderName, Path.GetFileName(baseline));
                var result = ImageCompare.Compare(baseline, actual, diff, pixelThreshold, maxDifferentPixelsPercent);
                report.screenshots.Add(result);
                if (result.missing)
                    report.findings.Add($"Screenshot {result.name} is missing (the replay stopped early, or the recording's screenshot was not saved).");
                else if (result.sizeMismatch)
                    report.findings.Add($"Screenshot {result.name} has a different size than in the recording.");
                else if (!result.passed)
                    report.findings.Add($"Screenshot {result.name} differs: {result.differentPercent:0.###}% of pixels (limit {maxDifferentPixelsPercent}%).");
            }

            var regression = false;
            var rec = report.recordedMetrics;
            var rep = report.replayMetrics;
            if (rec != null && rep != null && rec.frameTimeP95Ms > 0 && rep.frameTimeP95Ms > 0)
            {
                var change = (rep.frameTimeP95Ms - rec.frameTimeP95Ms) / rec.frameTimeP95Ms * 100f;
                if (change > performanceRegressionPercent)
                {
                    regression = true;
                    report.findings.Add($"p95 frame time went from {rec.frameTimeP95Ms:0.0} ms to {rep.frameTimeP95Ms:0.0} ms (+{change:0}%)." + (Application.isEditor ? " The replay ran in the Editor, so this is not comparable with the headset." : string.Empty));
                }
            }
            if (report.captureWasTruncated)
                report.findings.Add("The capture was cut short while recording (the app closed before Stop); it was replayed up to that point.");

            report.passed = report.FailedScreenshots == 0 && !(failOnPerformanceRegression && regression);
            report.finishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            try
            {
                report.Save();
            }
            catch (Exception e)
            {
                Debug.LogError($"[XRSessionCapture] Could not write the report: {e.Message}");
            }

            LastReport = report;
            m_Report = null;
            State = CaptureState.Idle;
            var log = new StringBuilder();
            log.Append("[XRSessionCapture] Replay ").Append(report.passed ? "PASSED" : "FAILED").Append(": ")
                .Append(report.screenshots.Count - report.FailedScreenshots).Append('/').Append(report.screenshots.Count).Append(" screenshots match. Report: ")
                .Append(Path.Combine(report.replayDirectory, ReplayReport.HtmlFileName));
            foreach (var f in report.findings)
                log.Append("\n  - ").Append(f);
            if (report.passed)
                Debug.Log(log.ToString());
            else
                Debug.LogWarning(log.ToString());

            ReplayFinished?.Invoke(report);
            if (quitWhenReplayFinishes)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.ExitPlaymode();
#else
                Application.Quit(report.passed ? 0 : 1);
#endif
            }
        }

        static MetricsSummary LoadSummary(string directory)
        {
            var path = Path.Combine(directory, MetricsLog.SummaryFileName);
            try
            {
                return File.Exists(path) ? JsonUtility.FromJson<MetricsSummary>(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------- Frame loop ----------

        void Update()
        {
            if (State == CaptureState.Replaying && m_Player != null && m_Player.IsFinished)
            {
                m_Player.Stop();
                BeginComparing();
            }
            if (State == CaptureState.Comparing && (m_Screenshots == null || m_Screenshots.Pending == 0))
                FinishComparing();

            if (m_Overlay != null)
            {
                m_Overlay.anchor = overlayAnchor;
                m_Overlay.scale = overlayScale;
            }
        }

        void LateUpdate()
        {
            var frame = State == CaptureState.Recording ? m_RecordedFrame : State == CaptureState.Replaying && m_Player != null ? m_Player.CurrentRecordedFrame : Time.frameCount;
            var time = State == CaptureState.Recording ? Time.unscaledTimeAsDouble - m_RecordStart : Time.unscaledTimeAsDouble;
            var metrics = m_Sampler.Sample(frame, time);
            LatestMetrics = metrics;
            DroppedFramesTotal += metrics.droppedFrames;

            if (State == CaptureState.Recording)
            {
                m_MetricsLog.Add(metrics);
                RecordFrame();
            }
            else if (State == CaptureState.Replaying && m_Player != null && m_Player.IsPlaying && !m_Player.IsFinished && m_Player.CurrentIndex >= 0)
            {
                m_MetricsLog.Add(metrics);
                ReplayFrame();
            }
        }

        // ---------- Helpers ----------

        /// <summary>World pose of a controller (recorded pose while replaying), using the camera's parent as tracking space.</summary>
        public Pose? ControllerWorldPose(bool left)
        {
            ControllerSample sample;
            if (State == CaptureState.Replaying && m_Player != null && m_Player.CurrentIndex >= 0)
                sample = left ? m_Player.Current.left : m_Player.Current.right;
            else
                sample = XRInputReader.ReadController(left);
            if (!sample.connected || (!sample.isTracked && sample.trackingState == 0))
                return null;
            var camera = HeadCamera;
            var space = camera != null ? camera.transform.parent : null;
            var position = sample.device.position;
            var rotation = sample.device.rotation;
            if (space != null)
            {
                position = space.TransformPoint(position);
                rotation = space.rotation * rotation;
            }
            return new Pose(position, rotation);
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unnamed";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return sb.ToString();
        }

        static string LaunchArgument(string name)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    if (intent == null || !intent.Call<bool>("hasExtra", name))
                        return null;
                    return intent.Call<string>("getStringExtra", name) ?? string.Empty;
                }
            }
            catch (Exception)
            {
                return null;
            }
#else
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-" + name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal) ? args[i + 1] : string.Empty;
            }
            return null;
#endif
        }
    }
}
