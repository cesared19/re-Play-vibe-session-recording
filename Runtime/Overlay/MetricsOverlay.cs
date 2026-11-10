using System.Globalization;
using System.Text;
using UnityEngine;

namespace XRSessionCapture
{
    public enum OverlayAnchor
    {
        /// <summary>On the back of the left wrist, facing you when you look at your watch.</summary>
        LeftWrist,
        /// <summary>Floats in front of you and follows your head with a delay.</summary>
        FollowHead,
    }

    /// <summary>
    /// In-headset panel with live metrics and a frame-time graph. It draws with its own unlit shaders (in Resources),
    /// so it works with the built-in pipeline and URP and in single-pass instanced or multiview stereo.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MetricsOverlay : MonoBehaviour
    {
        const int GraphWidth = 180;
        const int GraphHeight = 56;
        const float PanelWidth = 0.26f;
        const float PanelHeight = 0.13f;

        public OverlayAnchor anchor = OverlayAnchor.LeftWrist;
        [Tooltip("Distance in front of the eyes for FollowHead.")]
        public float followDistance = 0.7f;
        public float scale = 1f;

        SessionCaptureManager m_Manager;
        Transform m_Root;
        TextMesh m_Text;
        Renderer[] m_Renderers;
        Texture2D m_Graph;
        Color32[] m_GraphPixels;
        readonly float[] m_History = new float[GraphWidth];
        int m_HistoryIndex;
        float m_NextTextUpdate;
        bool m_Hidden;
        Font m_Font;
        Material m_TextMaterial;
        Material m_PanelMaterial;
        Material m_GraphMaterial;
        readonly StringBuilder m_Sb = new StringBuilder(512);

        public bool Visible
        {
            get => m_Root != null && m_Root.gameObject.activeSelf;
            set
            {
                if (m_Root != null)
                    m_Root.gameObject.SetActive(value);
            }
        }

        internal void Initialize(SessionCaptureManager manager)
        {
            m_Manager = manager;
            Build();
        }

        /// <summary>Hides the renderers for one render (screenshots must not include the overlay).</summary>
        internal void SetRenderersHidden(bool hidden)
        {
            if (m_Renderers == null || m_Hidden == hidden)
                return;
            m_Hidden = hidden;
            foreach (var r in m_Renderers)
                if (r != null)
                    r.enabled = !hidden;
        }

        void Build()
        {
            var unlit = Shader.Find("Hidden/XRSessionCapture/Unlit");
            var textShader = Shader.Find("Hidden/XRSessionCapture/Text");
            if (unlit == null || textShader == null)
            {
                Debug.LogError("[XRSessionCapture] Overlay shaders not found. Reimport the package (Runtime/Resources/XRSessionCapture).");
                enabled = false;
                return;
            }

            m_Root = new GameObject("XRSessionCapture Overlay").transform;
            m_Root.SetParent(transform, false);

            m_PanelMaterial = new Material(unlit) { color = new Color(0.05f, 0.05f, 0.07f, 0.82f), renderQueue = 4000 };
            CreateQuad("Panel", m_PanelMaterial, new Vector3(0, 0, 0.002f), new Vector2(PanelWidth, PanelHeight));

            m_Graph = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            m_GraphPixels = new Color32[GraphWidth * GraphHeight];
            m_GraphMaterial = new Material(unlit) { mainTexture = m_Graph, renderQueue = 4001 };
            CreateQuad("Graph", m_GraphMaterial, new Vector3(0, -PanelHeight * 0.5f + 0.022f, 0.001f), new Vector2(PanelWidth - 0.01f, 0.036f));

            m_Font = Resources.GetBuiltinResource<Font>(BuiltinFontName());
            m_TextMaterial = new Material(textShader) { mainTexture = m_Font.material.mainTexture, renderQueue = 4002 };
            Font.textureRebuilt += OnFontTextureRebuilt;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(m_Root, false);
            textGo.transform.localPosition = new Vector3(-PanelWidth * 0.5f + 0.006f, PanelHeight * 0.5f - 0.005f, 0);
            m_Text = textGo.AddComponent<TextMesh>();
            m_Text.font = m_Font;
            m_Text.fontSize = 48;
            // TextMesh line height is about characterSize * fontSize / 10 world units: ~1 cm here.
            m_Text.characterSize = 0.0021f;
            m_Text.anchor = TextAnchor.UpperLeft;
            m_Text.alignment = TextAlignment.Left;
            m_Text.richText = true;
            m_Text.color = Color.white;
            var textRenderer = textGo.GetComponent<MeshRenderer>();
            textRenderer.sharedMaterial = m_TextMaterial;

            m_Renderers = m_Root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in m_Renderers)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        static string BuiltinFontName()
        {
#if UNITY_2022_2_OR_NEWER
            return "LegacyRuntime.ttf";
#else
            return "Arial.ttf";
#endif
        }

        void OnFontTextureRebuilt(Font font)
        {
            if (font == m_Font && m_TextMaterial != null)
                m_TextMaterial.mainTexture = font.material.mainTexture;
        }

        void CreateQuad(string name, Material material, Vector3 localPosition, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_Root, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = new Vector3(size.x, size.y, 1);
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        void LateUpdate()
        {
            if (m_Root == null || m_Manager == null || !Visible)
                return;
            Place();
            var m = m_Manager.LatestMetrics;
            PushGraph(m.frameTimeMs, m.refreshRate);
            if (Time.unscaledTime >= m_NextTextUpdate)
            {
                m_NextTextUpdate = Time.unscaledTime + 0.25f;
                m_Text.text = BuildText(m);
            }
        }

        void Place()
        {
            var camera = m_Manager.HeadCamera;
            if (camera == null)
                return;
            var head = camera.transform;
            var s = Mathf.Max(0.1f, scale);
            m_Root.localScale = new Vector3(s, s, s);

            if (anchor == OverlayAnchor.LeftWrist)
            {
                var left = m_Manager.ControllerWorldPose(true);
                if (left.HasValue)
                {
                    var pose = left.Value;
                    // A little above the back of the wrist, facing the eyes.
                    var position = pose.position + pose.rotation * new Vector3(0, 0.05f, -0.07f);
                    m_Root.SetPositionAndRotation(position, Quaternion.LookRotation(position - head.position, head.up));
                    return;
                }
            }

            // FollowHead (also the fallback when the left controller is not tracked).
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = head.up;
            forward.Normalize();
            var target = head.position + forward * followDistance + Vector3.down * 0.15f;
            var t = 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime);
            var newPosition = Vector3.Lerp(m_Root.position, target, t);
            if ((m_Root.position - target).sqrMagnitude > 4f)
                newPosition = target;
            m_Root.SetPositionAndRotation(newPosition, Quaternion.LookRotation(newPosition - head.position, Vector3.up));
        }

        void PushGraph(float frameMs, float refreshRate)
        {
            m_History[m_HistoryIndex] = frameMs;
            m_HistoryIndex = (m_HistoryIndex + 1) % GraphWidth;

            var budget = refreshRate > 0 ? 1000f / refreshRate : 11.1f;
            var maxMs = budget * 2f;
            var background = new Color32(0, 0, 0, 0);
            for (var i = 0; i < m_GraphPixels.Length; i++)
                m_GraphPixels[i] = background;

            var budgetRow = Mathf.Clamp(Mathf.RoundToInt(budget / maxMs * (GraphHeight - 1)), 0, GraphHeight - 1);
            for (var x = 0; x < GraphWidth; x++)
                m_GraphPixels[budgetRow * GraphWidth + x] = new Color32(255, 255, 255, 90);

            for (var x = 0; x < GraphWidth; x++)
            {
                var value = m_History[(m_HistoryIndex + x) % GraphWidth];
                if (value <= 0)
                    continue;
                var height = Mathf.Clamp(Mathf.RoundToInt(value / maxMs * (GraphHeight - 1)), 1, GraphHeight - 1);
                var color = value <= budget * 1.05f ? new Color32(74, 222, 128, 230) : value <= budget * 1.5f ? new Color32(250, 204, 21, 230) : new Color32(248, 113, 113, 240);
                for (var y = 0; y <= height; y++)
                    m_GraphPixels[y * GraphWidth + x] = color;
            }
            m_Graph.SetPixels32(m_GraphPixels);
            m_Graph.Apply(false);
        }

        string BuildText(in MetricsFrame m)
        {
            var sb = m_Sb;
            sb.Clear();
            var budget = m.refreshRate > 0 ? 1000f / m.refreshRate : 11.1f;
            var fpsColor = m.frameTimeMs <= budget * 1.05f ? "#4ade80" : m.frameTimeMs <= budget * 1.5f ? "#facc15" : "#f87171";

            switch (m_Manager.State)
            {
                case CaptureState.Recording:
                    sb.Append("<color=#f87171>● REC</color> ").Append(Clock(m_Manager.RecordingSeconds)).Append("   frame ").Append(m_Manager.RecordedFrames).Append('\n');
                    break;
                case CaptureState.Replaying:
                    sb.Append("<color=#60a5fa>▶ REPLAY</color> ").Append(Mathf.RoundToInt(m_Manager.ReplayProgress * 100)).Append("%\n");
                    break;
                case CaptureState.Comparing:
                    sb.Append("<color=#60a5fa>Comparing screenshots…</color>\n");
                    break;
                default:
                    sb.Append("<color=#a1a1aa>Idle</color>").Append(m_Manager.LastReportPassed.HasValue ? (m_Manager.LastReportPassed.Value ? "   last replay: <color=#4ade80>passed</color>" : "   last replay: <color=#f87171>failed</color>") : string.Empty).Append('\n');
                    break;
            }

            sb.Append("<color=").Append(fpsColor).Append('>').Append(N(m.Fps, "0")).Append(" fps</color>  ").Append(N(m.frameTimeMs, "0.0")).Append(" ms  (budget ").Append(N(budget, "0.0")).Append(")\n");
            sb.Append("CPU ").Append(Ms(m.cpuFrameMs)).Append("  GPU ").Append(Ms(m.gpuFrameMs)).Append("  main ").Append(Ms(m.mainThreadMs)).Append('\n');
            sb.Append("XR app GPU ").Append(Ms(m.xrAppGpuMs)).Append("  dropped ").Append(m_Manager.DroppedFramesTotal).Append('\n');
            sb.Append("Mem ").Append(float.IsNaN(m.usedMemoryMB) ? "—" : N(m.usedMemoryMB, "0") + " MB").Append("  GC ").Append(float.IsNaN(m.gcAllocKB) ? "—" : N(m.gcAllocKB, "0.0") + " KB/f").Append('\n');
            sb.Append("Battery ").Append(Pct(m.batteryPercent)).Append("  ").Append(float.IsNaN(m.batteryTemperatureC) ? "" : N(m.batteryTemperatureC, "0.0") + " °C  ").Append(Thermal(m.thermalStatus)).Append('\n');
            sb.Append("Controllers L ").Append(Pct(m.leftControllerBatteryPercent)).Append("  R ").Append(Pct(m.rightControllerBatteryPercent));
            return sb.ToString();
        }

        static string N(float v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
        static string Ms(float v) => float.IsNaN(v) ? "—" : N(v, "0.0");
        static string Pct(float v) => float.IsNaN(v) ? "—" : N(v, "0") + "%";

        static string Thermal(int status)
        {
            switch (status)
            {
                case 0: return "<color=#4ade80>cool</color>";
                case 1: return "<color=#a3e635>light</color>";
                case 2: return "<color=#facc15>moderate</color>";
                case 3: return "<color=#fb923c>severe</color>";
                case 4:
                case 5:
                case 6: return "<color=#f87171>critical</color>";
                default: return string.Empty;
            }
        }

        static string Clock(float seconds)
        {
            var s = Mathf.FloorToInt(seconds);
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
            if (m_Graph != null) Destroy(m_Graph);
            if (m_TextMaterial != null) Destroy(m_TextMaterial);
            if (m_PanelMaterial != null) Destroy(m_PanelMaterial);
            if (m_GraphMaterial != null) Destroy(m_GraphMaterial);
        }
    }
}
