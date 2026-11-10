using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;

namespace XRSessionCapture
{
    /// <summary>Metrics for one frame. NaN means "not available on this device or configuration".</summary>
    public struct MetricsFrame
    {
        public int frame;
        public double time;
        /// <summary>Wall-clock time of the frame (Time.unscaledDeltaTime).</summary>
        public float frameTimeMs;
        /// <summary>CPU time of the frame from FrameTimingManager (needs "Frame Timing Stats" in Player Settings).</summary>
        public float cpuFrameMs;
        /// <summary>GPU time of the frame from FrameTimingManager (needs "Frame Timing Stats" in Player Settings).</summary>
        public float gpuFrameMs;
        /// <summary>Main thread time from the profiler counter.</summary>
        public float mainThreadMs;
        /// <summary>GPU time of the app as reported by the XR runtime.</summary>
        public float xrAppGpuMs;
        /// <summary>GPU time of the compositor as reported by the XR runtime.</summary>
        public float xrCompositorGpuMs;
        /// <summary>Frames the XR runtime dropped since the previous sample.</summary>
        public int droppedFrames;
        public float refreshRate;
        public float usedMemoryMB;
        public float gcAllocKB;
        public float batteryPercent;
        public float batteryTemperatureC;
        /// <summary>Android thermal status (0 none … 6 shutdown), -1 if unknown.</summary>
        public int thermalStatus;
        public float leftControllerBatteryPercent;
        public float rightControllerBatteryPercent;

        public float Fps => frameTimeMs > 0 ? 1000f / frameTimeMs : 0f;
    }

    /// <summary>Samples performance counters once per frame. Call <see cref="Sample"/> from LateUpdate and <see cref="Dispose"/> when done.</summary>
    public sealed class MetricsSampler : IDisposable
    {
        const float SlowPollSeconds = 2f;

        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly List<XRDisplaySubsystem> m_Displays = new List<XRDisplaySubsystem>();
        readonly List<UnityEngine.XR.InputDevice> m_XRDevices = new List<UnityEngine.XR.InputDevice>();
        ProfilerRecorder m_MainThread;
        ProfilerRecorder m_UsedMemory;
        ProfilerRecorder m_GcAlloc;
        int m_LastDropped = -1;
        float m_NextSlowPoll;
        float m_BatteryTemperature = float.NaN;
        int m_ThermalStatus = -1;
        float m_LeftBattery = float.NaN;
        float m_RightBattery = float.NaN;

        public MetricsFrame Latest { get; private set; }

        public MetricsSampler()
        {
            m_MainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            m_UsedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory", 1);
            m_GcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
        }

        public MetricsFrame Sample(int frame, double time)
        {
            var m = new MetricsFrame
            {
                frame = frame,
                time = time,
                frameTimeMs = Time.unscaledDeltaTime * 1000f,
                cpuFrameMs = float.NaN,
                gpuFrameMs = float.NaN,
                mainThreadMs = m_MainThread.Valid && m_MainThread.Count > 0 ? m_MainThread.LastValue / 1e6f : float.NaN,
                xrAppGpuMs = float.NaN,
                xrCompositorGpuMs = float.NaN,
                refreshRate = Screen.currentResolution.refreshRate,
                usedMemoryMB = m_UsedMemory.Valid && m_UsedMemory.Count > 0 ? m_UsedMemory.LastValue / (1024f * 1024f) : float.NaN,
                gcAllocKB = m_GcAlloc.Valid && m_GcAlloc.Count > 0 ? m_GcAlloc.LastValue / 1024f : float.NaN,
                batteryPercent = SystemInfo.batteryLevel >= 0 ? SystemInfo.batteryLevel * 100f : float.NaN,
            };

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
            {
                if (m_Timings[0].cpuFrameTime > 0)
                    m.cpuFrameMs = (float)m_Timings[0].cpuFrameTime;
                if (m_Timings[0].gpuFrameTime > 0)
                    m.gpuFrameMs = (float)m_Timings[0].gpuFrameTime;
            }

            var display = GetDisplay();
            if (display != null)
            {
                if (display.TryGetAppGPUTimeLastFrame(out var appGpu))
                    m.xrAppGpuMs = appGpu * 1000f;
                if (display.TryGetCompositorGPUTimeLastFrame(out var compositorGpu))
                    m.xrCompositorGpuMs = compositorGpu * 1000f;
                if (display.TryGetDisplayRefreshRate(out var refresh) && refresh > 0)
                    m.refreshRate = refresh;
                if (display.TryGetDroppedFrameCount(out var dropped))
                {
                    m.droppedFrames = m_LastDropped >= 0 ? Math.Max(0, dropped - m_LastDropped) : 0;
                    m_LastDropped = dropped;
                }
            }

            if (Time.unscaledTime >= m_NextSlowPoll)
            {
                m_NextSlowPoll = Time.unscaledTime + SlowPollSeconds;
                PollSlowStats();
            }
            m.batteryTemperatureC = m_BatteryTemperature;
            m.thermalStatus = m_ThermalStatus;
            m.leftControllerBatteryPercent = m_LeftBattery;
            m.rightControllerBatteryPercent = m_RightBattery;

            Latest = m;
            return m;
        }

        XRDisplaySubsystem GetDisplay()
        {
            SubsystemManager.GetSubsystems(m_Displays);
            foreach (var d in m_Displays)
                if (d.running)
                    return d;
            return null;
        }

        void PollSlowStats()
        {
            m_LeftBattery = ControllerBattery(InputDeviceCharacteristics.Left);
            m_RightBattery = ControllerBattery(InputDeviceCharacteristics.Right);
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidStats.Poll(out m_BatteryTemperature, out m_ThermalStatus);
#endif
        }

        float ControllerBattery(InputDeviceCharacteristics side)
        {
            InputDevices.GetDevicesWithCharacteristics(side | InputDeviceCharacteristics.Controller, m_XRDevices);
            foreach (var d in m_XRDevices)
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.batteryLevel, out var level))
                    return level * 100f;
            return float.NaN;
        }

        public void Dispose()
        {
            m_MainThread.Dispose();
            m_UsedMemory.Dispose();
            m_GcAlloc.Dispose();
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static class AndroidStats
    {
        static AndroidJavaObject s_Activity;
        static AndroidJavaObject s_PowerManager;
        static bool s_Failed;

        public static void Poll(out float batteryTemperatureC, out int thermalStatus)
        {
            batteryTemperatureC = float.NaN;
            thermalStatus = -1;
            if (s_Failed)
                return;
            try
            {
                if (s_Activity == null)
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                        s_Activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    s_PowerManager = s_Activity.Call<AndroidJavaObject>("getSystemService", "power");
                }
                using (var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED"))
                using (var intent = s_Activity.Call<AndroidJavaObject>("registerReceiver", null, filter))
                {
                    var tenths = intent != null ? intent.Call<int>("getIntExtra", "temperature", int.MinValue) : int.MinValue;
                    if (tenths != int.MinValue)
                        batteryTemperatureC = tenths / 10f;
                }
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    if (version.GetStatic<int>("SDK_INT") >= 29 && s_PowerManager != null)
                        thermalStatus = s_PowerManager.Call<int>("getCurrentThermalStatus");
                }
            }
            catch (Exception e)
            {
                s_Failed = true;
                Debug.LogWarning($"[XRSessionCapture] Android battery and thermal stats are not available: {e.Message}");
            }
        }
    }
#endif
}
