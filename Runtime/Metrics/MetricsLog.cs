using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace XRSessionCapture
{
    /// <summary>Writes one CSV row per frame and keeps what <see cref="MetricsSummary"/> needs.</summary>
    public sealed class MetricsLog : IDisposable
    {
        public const string FileName = "metrics.csv";
        public const string SummaryFileName = "metrics-summary.json";

        const string Header = "frame,time_s,frame_ms,fps,cpu_ms,gpu_ms,main_thread_ms,xr_app_gpu_ms,xr_compositor_gpu_ms,dropped_frames,refresh_hz,used_memory_mb,gc_alloc_kb,battery_pct,battery_temp_c,thermal_status,left_controller_battery_pct,right_controller_battery_pct";

        readonly StreamWriter m_Writer;
        readonly StringBuilder m_Line = new StringBuilder(256);
        readonly List<MetricsFrame> m_Frames = new List<MetricsFrame>(4096);
        readonly string m_Directory;
        bool m_Closed;

        public IReadOnlyList<MetricsFrame> Frames => m_Frames;

        public MetricsLog(string directory)
        {
            m_Directory = directory;
            Directory.CreateDirectory(directory);
            m_Writer = new StreamWriter(Path.Combine(directory, FileName), false, new UTF8Encoding(false), 64 * 1024);
            m_Writer.WriteLine(Header);
        }

        public void Add(in MetricsFrame m)
        {
            m_Frames.Add(m);
            m_Line.Clear();
            m_Line.Append(m.frame).Append(',');
            Append(m.time, "F4");
            Append(m.frameTimeMs);
            Append(m.Fps);
            Append(m.cpuFrameMs);
            Append(m.gpuFrameMs);
            Append(m.mainThreadMs);
            Append(m.xrAppGpuMs);
            Append(m.xrCompositorGpuMs);
            m_Line.Append(m.droppedFrames).Append(',');
            Append(m.refreshRate);
            Append(m.usedMemoryMB, "F1");
            Append(m.gcAllocKB, "F2");
            Append(m.batteryPercent, "F0");
            Append(m.batteryTemperatureC, "F1");
            m_Line.Append(m.thermalStatus).Append(',');
            Append(m.leftControllerBatteryPercent, "F0");
            Append(m.rightControllerBatteryPercent, "F0", last: true);
            m_Writer.WriteLine(m_Line.ToString());
            if (m_Frames.Count % 90 == 0)
                m_Writer.Flush();
        }

        void Append(double value, string format = "F3", bool last = false)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value))
                m_Line.Append(value.ToString(format, CultureInfo.InvariantCulture));
            if (!last)
                m_Line.Append(',');
        }

        public void Flush()
        {
            if (!m_Closed)
                m_Writer.Flush();
        }

        /// <summary>Closes the CSV and writes metrics-summary.json next to it.</summary>
        public MetricsSummary Close()
        {
            if (m_Closed)
                return null;
            m_Closed = true;
            m_Writer.Flush();
            m_Writer.Dispose();
            var summary = MetricsSummary.From(m_Frames);
            File.WriteAllText(Path.Combine(m_Directory, SummaryFileName), JsonUtility.ToJson(summary, true));
            return summary;
        }

        public void Dispose() => Close();
    }

    /// <summary>Aggregate numbers for a session. Times are in milliseconds; -1 means not available.</summary>
    [Serializable]
    public class MetricsSummary
    {
        public int frames;
        public float durationSeconds;
        public float refreshRate;
        public float averageFps;
        public float frameTimeAvgMs;
        public float frameTimeP50Ms;
        public float frameTimeP95Ms;
        public float frameTimeP99Ms;
        public float frameTimeMaxMs;
        /// <summary>Percentage of frames that took longer than one refresh interval (+5%).</summary>
        public float overBudgetPercent;
        public int droppedFrames;
        public float cpuAvgMs = -1;
        public float gpuAvgMs = -1;
        public float xrAppGpuAvgMs = -1;
        public float usedMemoryPeakMB = -1;
        public float batteryUsedPercent = -1;
        public float batteryTemperatureMaxC = -1;
        public int thermalStatusMax = -1;

        public static MetricsSummary From(IReadOnlyList<MetricsFrame> data)
        {
            var s = new MetricsSummary { frames = data.Count };
            if (data.Count == 0)
                return s;
            // The first frames after starting are usually hitches from loading; skip up to 10 for the timing stats.
            var skip = Math.Min(10, data.Count / 10);
            var times = new List<float>(data.Count);
            double sumCpu = 0, sumGpu = 0, sumXrGpu = 0;
            int nCpu = 0, nGpu = 0, nXrGpu = 0;
            float refreshSum = 0;
            int refreshCount = 0;
            float firstBattery = float.NaN, lastBattery = float.NaN;
            for (var i = 0; i < data.Count; i++)
            {
                var m = data[i];
                s.droppedFrames += m.droppedFrames;
                if (m.refreshRate > 0)
                {
                    refreshSum += m.refreshRate;
                    refreshCount++;
                }
                if (!float.IsNaN(m.usedMemoryMB))
                    s.usedMemoryPeakMB = Mathf.Max(s.usedMemoryPeakMB, m.usedMemoryMB);
                if (!float.IsNaN(m.batteryTemperatureC))
                    s.batteryTemperatureMaxC = Mathf.Max(s.batteryTemperatureMaxC, m.batteryTemperatureC);
                s.thermalStatusMax = Math.Max(s.thermalStatusMax, m.thermalStatus);
                if (!float.IsNaN(m.batteryPercent))
                {
                    if (float.IsNaN(firstBattery))
                        firstBattery = m.batteryPercent;
                    lastBattery = m.batteryPercent;
                }
                if (i < skip)
                    continue;
                times.Add(m.frameTimeMs);
                if (!float.IsNaN(m.cpuFrameMs)) { sumCpu += m.cpuFrameMs; nCpu++; }
                if (!float.IsNaN(m.gpuFrameMs)) { sumGpu += m.gpuFrameMs; nGpu++; }
                if (!float.IsNaN(m.xrAppGpuMs)) { sumXrGpu += m.xrAppGpuMs; nXrGpu++; }
            }

            s.durationSeconds = (float)(data[data.Count - 1].time - data[0].time);
            s.refreshRate = refreshCount > 0 ? refreshSum / refreshCount : 0;
            if (nCpu > 0) s.cpuAvgMs = (float)(sumCpu / nCpu);
            if (nGpu > 0) s.gpuAvgMs = (float)(sumGpu / nGpu);
            if (nXrGpu > 0) s.xrAppGpuAvgMs = (float)(sumXrGpu / nXrGpu);
            if (!float.IsNaN(firstBattery))
                s.batteryUsedPercent = firstBattery - lastBattery;

            if (times.Count > 0)
            {
                double total = 0;
                foreach (var t in times)
                    total += t;
                s.frameTimeAvgMs = (float)(total / times.Count);
                s.averageFps = s.frameTimeAvgMs > 0 ? 1000f / s.frameTimeAvgMs : 0;
                var budget = s.refreshRate > 0 ? 1000f / s.refreshRate * 1.05f : 1000f / 90f * 1.05f;
                var over = 0;
                foreach (var t in times)
                    if (t > budget)
                        over++;
                s.overBudgetPercent = over * 100f / times.Count;
                times.Sort();
                s.frameTimeP50Ms = Percentile(times, 0.50f);
                s.frameTimeP95Ms = Percentile(times, 0.95f);
                s.frameTimeP99Ms = Percentile(times, 0.99f);
                s.frameTimeMaxMs = times[times.Count - 1];
            }
            return s;
        }

        static float Percentile(List<float> sorted, float p)
        {
            var index = Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[index];
        }
    }
}
