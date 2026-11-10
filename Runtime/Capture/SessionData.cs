using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRSessionCapture
{
    /// <summary>Digital controller state, one bit per button.</summary>
    [Flags]
    public enum ControllerButtons : uint
    {
        None = 0,
        TriggerPressed = 1 << 0,
        GripPressed = 1 << 1,
        Primary = 1 << 2,
        Secondary = 1 << 3,
        Menu = 1 << 4,
        StickClick = 1 << 5,
        StickTouch = 1 << 6,
        PrimaryTouch = 1 << 7,
        SecondaryTouch = 1 << 8,
        TriggerTouch = 1 << 9,
    }

    [Serializable]
    public struct PoseSample
    {
        public Vector3 position;
        public Quaternion rotation;

        public PoseSample(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public static PoseSample Identity => new PoseSample(Vector3.zero, Quaternion.identity);

        public static PoseSample Lerp(PoseSample a, PoseSample b, float t)
        {
            return new PoseSample(Vector3.Lerp(a.position, b.position, t), Quaternion.Slerp(Normalize(a.rotation), Normalize(b.rotation), t));
        }

        internal static Quaternion Normalize(Quaternion q)
        {
            var mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f)
                return Quaternion.identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }
    }

    [Serializable]
    public struct HeadSample
    {
        public bool isTracked;
        public int trackingState;
        public PoseSample pose;
    }

    [Serializable]
    public struct ControllerSample
    {
        public bool connected;
        public bool isTracked;
        public int trackingState;
        public PoseSample device;
        public PoseSample pointer;
        public float trigger;
        public float grip;
        public Vector2 stick;
        public ControllerButtons buttons;

        public bool IsPressed(ControllerButtons button) => (buttons & button) != 0;

        /// <summary>Poses and analog values are interpolated; buttons and tracking flags come from <paramref name="a"/>.</summary>
        public static ControllerSample Lerp(ControllerSample a, ControllerSample b, float t)
        {
            if (!a.connected || !b.connected)
                return a;
            var result = a;
            result.device = PoseSample.Lerp(a.device, b.device, t);
            result.pointer = PoseSample.Lerp(a.pointer, b.pointer, t);
            result.trigger = Mathf.Lerp(a.trigger, b.trigger, t);
            result.grip = Mathf.Lerp(a.grip, b.grip, t);
            result.stick = Vector2.Lerp(a.stick, b.stick, t);
            return result;
        }
    }

    /// <summary>Everything recorded for one rendered frame. Poses are in tracking space, as the XR runtime reports them.</summary>
    [Serializable]
    public struct FrameSample
    {
        public int frame;
        public double time;
        public float deltaTime;
        public HeadSample head;
        public ControllerSample left;
        public ControllerSample right;

        public static FrameSample Lerp(FrameSample a, FrameSample b, float t)
        {
            var result = a;
            result.time = a.time + (b.time - a.time) * t;
            result.head.pose = PoseSample.Lerp(a.head.pose, b.head.pose, t);
            result.left = ControllerSample.Lerp(a.left, b.left, t);
            result.right = ControllerSample.Lerp(a.right, b.right, t);
            return result;
        }
    }

    public enum MarkerKind : byte
    {
        /// <summary>A screenshot was taken on this frame. During replay one is taken on the same frame and compared.</summary>
        Screenshot = 1,
        /// <summary>A free-form note (for example, "opened the menu").</summary>
        Note = 2,
    }

    [Serializable]
    public struct Marker
    {
        public int frame;
        public double time;
        public MarkerKind kind;
        public string name;
    }

    /// <summary>Metadata stored at the start of every capture file.</summary>
    [Serializable]
    public class SessionInfo
    {
        public int formatVersion;
        public string id;
        public string createdUtc;
        public string deviceModel;
        public string deviceName;
        public string operatingSystem;
        public string xrDevice;
        public string applicationId;
        public string applicationVersion;
        public string unityVersion;
        public string sceneName;
        public float displayRefreshRate;
        public int screenshotWidth;
        public int screenshotHeight;
        public bool usedFixedSeed;
        public int randomSeed;
        public string notes;
    }

    /// <summary>A capture loaded in memory.</summary>
    public class SessionCapture
    {
        public SessionInfo info;
        public readonly List<FrameSample> frames = new List<FrameSample>();
        public readonly List<Marker> markers = new List<Marker>();

        /// <summary>True if the file ended without its end record (the app was closed while recording). Everything before that point is kept.</summary>
        public bool truncated;

        public double Duration => frames.Count == 0 ? 0 : frames[frames.Count - 1].time;

        public static SessionCapture Load(string path) => SessionReader.Read(path);

        /// <summary>Index of the last frame whose time is &lt;= <paramref name="time"/> (binary search).</summary>
        public int FrameIndexAt(double time)
        {
            int lo = 0, hi = frames.Count - 1;
            if (hi < 0)
                return -1;
            if (time <= frames[0].time)
                return 0;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (frames[mid].time <= time)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The sample at <paramref name="time"/>, interpolating poses between the surrounding frames.</summary>
        public FrameSample SampleAt(double time)
        {
            var i = FrameIndexAt(time);
            if (i < 0)
                return default;
            if (i >= frames.Count - 1)
                return frames[frames.Count - 1];
            var a = frames[i];
            var b = frames[i + 1];
            var span = b.time - a.time;
            var t = span <= 0 ? 0f : (float)((time - a.time) / span);
            return FrameSample.Lerp(a, b, Mathf.Clamp01(t));
        }
    }
}
