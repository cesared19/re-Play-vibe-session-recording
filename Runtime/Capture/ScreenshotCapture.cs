using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace XRSessionCapture
{
    /// <summary>
    /// Renders a mono view from the headset camera into a PNG. A hidden helper camera with a fixed size and field of view
    /// is used, so screenshots taken on the headset, in the Editor and on different runs are comparable pixel by pixel.
    /// </summary>
    public sealed class ScreenshotCapture : IDisposable
    {
        readonly int m_Width;
        readonly int m_Height;
        readonly float m_FieldOfView;
        Camera m_Camera;
        RenderTexture m_Target;
        int m_Pending;

        /// <summary>Screenshots still being read back or written to disk.</summary>
        public int Pending => m_Pending;

        public ScreenshotCapture(int width, int height, float fieldOfView)
        {
            m_Width = Mathf.Max(16, width);
            m_Height = Mathf.Max(16, height);
            m_FieldOfView = Mathf.Clamp(fieldOfView, 10f, 170f);
        }

        /// <summary>Renders <paramref name="source"/>'s view now and writes it to <paramref name="path"/> asynchronously.</summary>
        public void Capture(Camera source, string path, Action<string> onSaved = null)
        {
            if (source == null)
            {
                Debug.LogWarning("[XRSessionCapture] No camera to take the screenshot from (tag the headset camera MainCamera or assign it).");
                return;
            }
            EnsureResources();
            m_Camera.CopyFrom(source);
            m_Camera.stereoTargetEye = StereoTargetEyeMask.None;
            m_Camera.targetTexture = m_Target;
            m_Camera.fieldOfView = m_FieldOfView;
            m_Camera.aspect = m_Width / (float)m_Height;
            m_Camera.enabled = false;
            m_Camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            m_Camera.Render();

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            m_Pending++;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                AsyncGPUReadback.Request(m_Target, 0, TextureFormat.RGBA32, request =>
                {
                    if (request.hasError)
                    {
                        m_Pending--;
                        Debug.LogWarning($"[XRSessionCapture] GPU readback failed for {path}.");
                        return;
                    }
                    SaveAsync(request.GetData<byte>().ToArray(), path, onSaved);
                });
            }
            else
            {
                var previous = RenderTexture.active;
                RenderTexture.active = m_Target;
                var texture = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, m_Width, m_Height), 0, 0, false);
                RenderTexture.active = previous;
                var bytes = texture.GetRawTextureData();
                UnityEngine.Object.Destroy(texture);
                SaveAsync(bytes, path, onSaved);
            }
        }

        void SaveAsync(byte[] rgba, string path, Action<string> onSaved)
        {
            var width = m_Width;
            var height = m_Height;
            var context = System.Threading.SynchronizationContext.Current;
            Task.Run(() =>
            {
                try
                {
                    // The clear color may be transparent; screenshots are always opaque.
                    for (var i = 3; i < rgba.Length; i += 4)
                        rgba[i] = 255;
                    var png = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
                    File.WriteAllBytes(path, png);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[XRSessionCapture] Could not save {path}: {e.Message}");
                }
            }).ContinueWith(_ =>
            {
                if (context != null)
                    context.Post(__ => Done(path, onSaved), null);
                else
                    Done(path, onSaved);
            });
        }

        void Done(string path, Action<string> onSaved)
        {
            m_Pending--;
            onSaved?.Invoke(path);
        }

        void EnsureResources()
        {
            if (m_Camera == null)
            {
                var go = new GameObject("XRSessionCapture Screenshot Camera") { hideFlags = HideFlags.HideAndDontSave };
                m_Camera = go.AddComponent<Camera>();
                m_Camera.enabled = false;
            }
            if (m_Target == null)
            {
                m_Target = new RenderTexture(m_Width, m_Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "XRSessionCapture Screenshot",
                    antiAliasing = 1,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                m_Target.Create();
            }
        }

        public void Dispose()
        {
            if (m_Camera != null)
                UnityEngine.Object.Destroy(m_Camera.gameObject);
            if (m_Target != null)
            {
                m_Target.Release();
                UnityEngine.Object.Destroy(m_Target);
            }
            m_Camera = null;
            m_Target = null;
        }
    }
}
