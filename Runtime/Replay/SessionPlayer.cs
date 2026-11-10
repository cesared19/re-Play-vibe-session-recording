using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.XR;

namespace XRSessionCapture
{
    public enum ReplayTiming
    {
        /// <summary>
        /// One recorded frame per rendered frame, and <see cref="Time.captureDeltaTime"/> set to the recorded delta,
        /// so game time advances exactly as in the recording whatever the real frame rate. Use this for tests and screenshot comparison.
        /// </summary>
        FrameLocked,
        /// <summary>Follows the wall clock and interpolates poses between recorded frames. Use this to watch a session at its real speed.</summary>
        RealTime,
    }

    [Serializable]
    public class ReplayOptions
    {
        public ReplayTiming timing = ReplayTiming.FrameLocked;
        [Tooltip("Disable the real headset and controllers while replaying, so they do not fight the replayed input.")]
        public bool disableRealDevices = true;
        [Tooltip("Playback speed for RealTime timing.")]
        public float speed = 1f;
    }

    /// <summary>
    /// Feeds a <see cref="SessionCapture"/> into virtual XR devices. Anything bound to &lt;XRHMD&gt; or &lt;XRController&gt;
    /// (TrackedPoseDriver, XR Interaction Toolkit, your own actions) receives the recorded poses and buttons.
    /// </summary>
    public sealed class SessionPlayer
    {
        readonly SessionCapture m_Capture;
        readonly ReplayOptions m_Options;
        readonly List<InputDevice> m_DisabledDevices = new List<InputDevice>();

        ReplayHMD m_HMD;
        ReplayController m_Left;
        ReplayController m_Right;
        int m_Index = -1;
        int m_LastFrameCount = -1;
        double m_StartRealtime;
        float m_PreviousCaptureDeltaTime;
        FrameSample m_Current;

        public SessionCapture Capture => m_Capture;
        public bool IsPlaying { get; private set; }
        public bool IsFinished { get; private set; }

        /// <summary>Index in <see cref="SessionCapture.frames"/> of the sample applied this frame.</summary>
        public int CurrentIndex => m_Index;

        /// <summary>The recorded frame number of the sample applied this frame (matches <see cref="Marker.frame"/>).</summary>
        public int CurrentRecordedFrame => m_Current.frame;

        public FrameSample Current => m_Current;
        public float Progress => m_Capture.frames.Count == 0 ? 1f : Mathf.Clamp01((m_Index + 1) / (float)m_Capture.frames.Count);

        /// <summary>Raised by <see cref="Stop"/> when the whole capture was played. Poll <see cref="IsFinished"/> and call <see cref="Stop"/> from Update.</summary>
        public event Action Finished;

        /// <summary>Raised once per frame, right after a new sample is applied, with the recorded frame number.</summary>
        public event Action<int> FrameApplied;

        public SessionPlayer(SessionCapture capture, ReplayOptions options = null)
        {
            m_Capture = capture ?? throw new ArgumentNullException(nameof(capture));
            m_Options = options ?? new ReplayOptions();
        }

        public void Start()
        {
            if (IsPlaying)
                return;
            if (m_Capture.frames.Count == 0)
                throw new InvalidOperationException("The capture has no frames.");

            ReplayLayouts.Register();
            m_HMD = InputSystem.AddDevice<ReplayHMD>("XRSessionReplayHMD");
            m_Left = InputSystem.AddDevice<ReplayController>("XRSessionReplayLeft");
            m_Right = InputSystem.AddDevice<ReplayController>("XRSessionReplayRight");
            InputSystem.SetDeviceUsage(m_Left, UnityEngine.InputSystem.CommonUsages.LeftHand);
            InputSystem.SetDeviceUsage(m_Right, UnityEngine.InputSystem.CommonUsages.RightHand);

            if (m_Options.disableRealDevices)
            {
                foreach (var device in InputSystem.devices)
                    DisableIfRealXRDevice(device);
                InputSystem.onDeviceChange += OnDeviceChange;
            }

            m_PreviousCaptureDeltaTime = Time.captureDeltaTime;
            m_StartRealtime = Time.realtimeSinceStartupAsDouble;
            m_Index = -1;
            m_LastFrameCount = -1;
            IsPlaying = true;
            IsFinished = false;
            InputSystem.onBeforeUpdate += OnBeforeUpdate;
        }

        public void Stop()
        {
            if (!IsPlaying)
                return;
            IsPlaying = false;
            InputSystem.onBeforeUpdate -= OnBeforeUpdate;
            InputSystem.onDeviceChange -= OnDeviceChange;
            if (m_Options.timing == ReplayTiming.FrameLocked)
                Time.captureDeltaTime = m_PreviousCaptureDeltaTime;

            RemoveDevice(ref m_HMD);
            RemoveDevice(ref m_Left);
            RemoveDevice(ref m_Right);
            foreach (var device in m_DisabledDevices)
            {
                if (device.added && !device.enabled)
                    InputSystem.EnableDevice(device);
            }
            m_DisabledDevices.Clear();
            if (IsFinished)
                Finished?.Invoke();
        }

        static void RemoveDevice<T>(ref T device) where T : InputDevice
        {
            if (device != null && device.added)
                InputSystem.RemoveDevice(device);
            device = null;
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected || change == InputDeviceChange.Enabled)
                DisableIfRealXRDevice(device);
        }

        void DisableIfRealXRDevice(InputDevice device)
        {
            if (device is ReplayHMD || device is ReplayController)
                return;
            if (!(device is XRHMD) && !(device is XRController))
                return;
            if (!device.enabled)
                return;
            InputSystem.DisableDevice(device);
            if (!m_DisabledDevices.Contains(device))
                m_DisabledDevices.Add(device);
        }

        void OnBeforeUpdate()
        {
            if (!IsPlaying)
                return;
            var updateType = InputState.currentUpdateType;
            if (updateType == InputUpdateType.Editor)
                return;

            // Advance once per rendered frame; the BeforeRender update re-applies the same sample.
            if (Time.frameCount != m_LastFrameCount)
            {
                m_LastFrameCount = Time.frameCount;
                if (!Advance())
                    return;
            }
            Apply();
        }

        bool Advance()
        {
            var frames = m_Capture.frames;
            if (m_Options.timing == ReplayTiming.FrameLocked)
            {
                m_Index++;
                if (m_Index >= frames.Count)
                {
                    Finish();
                    return false;
                }
                m_Current = frames[m_Index];
                // The next frame's delta: game time then advances exactly as it did while recording.
                var next = m_Index + 1 < frames.Count ? frames[m_Index + 1].deltaTime : m_Current.deltaTime;
                Time.captureDeltaTime = Mathf.Clamp(next, 1f / 240f, 0.25f);
            }
            else
            {
                var elapsed = (Time.realtimeSinceStartupAsDouble - m_StartRealtime) * Mathf.Max(0.01f, m_Options.speed);
                if (elapsed > m_Capture.Duration)
                {
                    Finish();
                    return false;
                }
                m_Index = m_Capture.FrameIndexAt(elapsed);
                m_Current = m_Capture.SampleAt(elapsed);
            }
            FrameApplied?.Invoke(m_Current.frame);
            return true;
        }

        void Apply()
        {
            InputSystem.QueueStateEvent(m_HMD, ReplayLayouts.ToState(m_Current.head));
            InputSystem.QueueStateEvent(m_Left, ReplayLayouts.ToState(m_Current.left));
            InputSystem.QueueStateEvent(m_Right, ReplayLayouts.ToState(m_Current.right));
        }

        void Finish()
        {
            // Devices are not removed from inside an input update: the owner calls Stop() (SessionCaptureManager does it in Update).
            if (IsFinished)
                return;
            IsFinished = true;
            InputSystem.onBeforeUpdate -= OnBeforeUpdate;
            if (m_Options.timing == ReplayTiming.FrameLocked)
                Time.captureDeltaTime = m_PreviousCaptureDeltaTime;
        }
    }
}
