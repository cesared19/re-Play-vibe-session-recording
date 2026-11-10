using System;
using System.IO;
using UnityEngine;

namespace XRSessionCapture
{
    [Serializable]
    public class ImageCompareResult
    {
        public string name;
        public string baseline;
        public string actual;
        public string diff;
        public bool missing;
        public bool sizeMismatch;
        public int differentPixels;
        public int totalPixels;
        public float differentPercent;
        public bool passed;
    }

    public static class ImageCompare
    {
        static void SafeDestroy(UnityEngine.Object o)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(o);
            else
                UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>
        /// Compares two PNGs. A pixel differs when any channel differs by more than <paramref name="channelThreshold"/> (0–1).
        /// The images match when at most <paramref name="maxDifferentPercent"/> of the pixels differ. A diff image is written
        /// to <paramref name="diffPath"/> (differing pixels in red over a dimmed copy of the actual image).
        /// </summary>
        public static ImageCompareResult Compare(string baselinePath, string actualPath, string diffPath, float channelThreshold, float maxDifferentPercent)
        {
            var result = new ImageCompareResult
            {
                name = Path.GetFileNameWithoutExtension(baselinePath),
                baseline = baselinePath,
                actual = actualPath,
            };
            if (!File.Exists(baselinePath) || !File.Exists(actualPath))
            {
                result.missing = true;
                return result;
            }

            var a = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                a.LoadImage(File.ReadAllBytes(baselinePath));
                b.LoadImage(File.ReadAllBytes(actualPath));
                if (a.width != b.width || a.height != b.height)
                {
                    result.sizeMismatch = true;
                    return result;
                }
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();
                var threshold = Mathf.RoundToInt(Mathf.Clamp01(channelThreshold) * 255f);
                var diff = new Color32[pa.Length];
                var count = 0;
                for (var i = 0; i < pa.Length; i++)
                {
                    var x = pa[i];
                    var y = pb[i];
                    var d = Math.Max(Math.Abs(x.r - y.r), Math.Max(Math.Abs(x.g - y.g), Math.Abs(x.b - y.b)));
                    if (d > threshold)
                    {
                        count++;
                        diff[i] = new Color32(255, 0, 0, 255);
                    }
                    else
                    {
                        var gray = (byte)((y.r * 77 + y.g * 150 + y.b * 29) >> 10);
                        diff[i] = new Color32(gray, gray, gray, 255);
                    }
                }
                result.totalPixels = pa.Length;
                result.differentPixels = count;
                result.differentPercent = pa.Length == 0 ? 0 : count * 100f / pa.Length;
                result.passed = result.differentPercent <= maxDifferentPercent;

                if (!string.IsNullOrEmpty(diffPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath));
                    var d = new Texture2D(a.width, a.height, TextureFormat.RGBA32, false);
                    d.SetPixels32(diff);
                    File.WriteAllBytes(diffPath, d.EncodeToPNG());
                    SafeDestroy(d);
                    result.diff = diffPath;
                }
                return result;
            }
            finally
            {
                SafeDestroy(a);
                SafeDestroy(b);
            }
        }
    }
}
