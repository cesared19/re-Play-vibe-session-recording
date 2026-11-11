using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XRSessionCapture;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Replays every capture in SessionCaptures/regression and fails if a screenshot differs from the recording.
/// Run it from Window > General > Test Runner (PlayMode), or from the command line:
///   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults results.xml
/// Captures are recorded on the headset, pulled with Window > XR Session Capture and moved into the regression folder.
///
/// To publish the replays to SparkyTC when the run ends (one automated test run for the build), set these environment
/// variables before starting Unity: SPARKYTC_URL, SPARKYTC_TOKEN (Read and write) and SPARKYTC_BUILD, and optionally
/// SPARKYTC_API_URL and SPARKYTC_RUN_TITLE. Captures must be linked to test cases (Window > XR Session Capture).
/// </summary>
public class ReplayRegressionTests
{
    const float TimeoutSeconds = 600f;

    // replays made in this run, published to SparkyTC at the end
    static readonly List<string> s_Replays = new List<string>();

    static IEnumerable<string> Captures()
    {
        var folder = Path.Combine(SessionCaptureManager.RootDirectory, "regression");
        if (!Directory.Exists(folder))
            yield break;
        foreach (var directory in Directory.GetDirectories(folder))
            if (File.Exists(Path.Combine(directory, SessionFile.CaptureFileName)))
                yield return Path.Combine("regression", Path.GetFileName(directory));
    }

    [UnityTest]
    public IEnumerator ReplayMatchesRecording([ValueSource(nameof(Captures))] string capture)
    {
        var path = SessionCaptureManager.ResolveCapturePath(capture);
        Assert.IsNotNull(path, "Capture not found: " + capture);
        var info = SessionCapture.Load(path).info;

        // A manager in the scene picks this up in Start() and replays instead of recording.
        SessionCaptureManager.PendingReplayPath = path;
#if UNITY_EDITOR
        var scenePath = FindScenePath(info.sceneName);
        Assert.IsNotNull(scenePath, $"Scene '{info.sceneName}' not found in the project.");
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scenePath, new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
#else
        yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(info.sceneName);
#endif
        yield return null;

        var manager = SessionCaptureManager.Instance;
        if (manager == null)
        {
            manager = new GameObject("XR Session Capture").AddComponent<SessionCaptureManager>();
            yield return null; // Start() consumes PendingReplayPath.
        }
        if (manager.State != CaptureState.Replaying && manager.State != CaptureState.Comparing)
            Assert.IsTrue(manager.StartReplay(path), "The replay did not start.");
        SessionCaptureManager.PendingReplayPath = null;

        ReplayReport report = null;
        manager.ReplayFinished += r => report = r;
        var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (report == null && Time.realtimeSinceStartup < deadline)
            yield return null;

        Assert.IsNotNull(report, "The replay did not finish in time.");
        s_Replays.Add(report.replayDirectory);
        Assert.IsTrue(report.passed, "Replay failed:\n" + string.Join("\n", report.findings) + "\nReport: " + Path.Combine(report.replayDirectory, ReplayReport.HtmlFileName));
        Object.Destroy(manager.gameObject);
        yield return null;
    }

    [OneTimeTearDown]
    public void PublishToSparkyTC()
    {
#if UNITY_EDITOR
        if (s_Replays.Count == 0)
            return;
        try
        {
            // the publisher doesn't need the main thread, so waiting here is safe
            var outcome = XRSessionCapture.SparkyTC.SparkyTCPublisher.PublishFromEnvironmentAsync(s_Replays).GetAwaiter().GetResult();
            if (outcome == null)
                return; // SPARKYTC_* not set: nothing to publish
            Debug.Log($"[XRSessionCapture] Published to SparkyTC: test run {outcome.testRun.testRunNumber}, {outcome.recorded} new result(s). {outcome.testRunUrl}");
            foreach (var skipped in outcome.skipped)
                Debug.LogWarning($"[XRSessionCapture] Not published: {skipped.Key} ({skipped.skipReason})");
        }
        catch (XRSessionCapture.SparkyTC.SparkyTCException e)
        {
            Assert.Fail("Couldn't publish the replays to SparkyTC: " + e.Message);
        }
        finally
        {
            s_Replays.Clear();
        }
#endif
    }

#if UNITY_EDITOR
    static string FindScenePath(string sceneName)
    {
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Scene " + sceneName))
        {
            var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(p) == sceneName)
                return p;
        }
        return null;
    }
#endif
}
