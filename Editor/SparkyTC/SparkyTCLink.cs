using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace XRSessionCapture.Editor.SparkyTC
{
    /// <summary>
    /// The SparkyTC test case a capture automates, saved as sparkytc.json in the capture's folder. It travels with the
    /// folder (for example into SessionCaptures/regression) and never changes session.xrsc.
    /// </summary>
    [Serializable]
    public class SparkyTCLink
    {
        public const string FileName = "sparkytc.json";

        public string projectShortCode;
        public long testCaseNumber;
        public string title;
        public string sectionPath;
        /// <summary>The test case in the web app, relative to the SparkyTC URL.</summary>
        public string webPath;
        public string linkedUtc;

        public string DisplayCode => string.IsNullOrEmpty(projectShortCode) ? "#" + testCaseNumber : projectShortCode + "-" + testCaseNumber;

        public static SparkyTCLink Load(string captureDirectory)
        {
            var path = Path.Combine(captureDirectory, FileName);
            if (!File.Exists(path))
                return null;
            try
            {
                var link = JsonUtility.FromJson<SparkyTCLink>(File.ReadAllText(path));
                return link != null && link.testCaseNumber > 0 ? link : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[XRSessionCapture] Ignoring {path}: {e.Message}");
                return null;
            }
        }

        public static SparkyTCLink From(string projectShortCode, SparkyTCClient.TestCase testCase) => new SparkyTCLink
        {
            projectShortCode = projectShortCode,
            testCaseNumber = testCase.testCaseNumber,
            title = testCase.title,
            sectionPath = testCase.sectionPath,
            webPath = testCase.webPath,
            linkedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        public void Save(string captureDirectory)
        {
            File.WriteAllText(Path.Combine(captureDirectory, FileName), JsonUtility.ToJson(this, true));
        }

        public static void Delete(string captureDirectory)
        {
            var path = Path.Combine(captureDirectory, FileName);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
