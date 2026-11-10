using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace XRSessionCapture
{
    /// <summary>
    /// Binary capture format (.xrsc), little-endian:
    /// <code>
    /// "XRSC" | int32 formatVersion | string infoJson
    /// then records: byte tag + payload
    ///   0x01 frame  : int32 frame, double time, float deltaTime, head, left, right
    ///   0x02 marker : int32 frame, double time, byte kind, string name
    ///   0xFF end    : int32 frameCount, int32 markerCount
    /// </code>
    /// Strings use <see cref="BinaryWriter.Write(string)"/> (7-bit length prefix, UTF-8).
    /// </summary>
    public static class SessionFile
    {
        public const int FormatVersion = 1;
        public const string Extension = ".xrsc";
        public const string CaptureFileName = "session" + Extension;
        internal static readonly byte[] Magic = { (byte)'X', (byte)'R', (byte)'S', (byte)'C' };
        internal const byte TagFrame = 0x01;
        internal const byte TagMarker = 0x02;
        internal const byte TagEnd = 0xFF;
    }

    public sealed class SessionWriter : IDisposable
    {
        readonly FileStream m_Stream;
        readonly BinaryWriter m_Writer;
        int m_FrameCount;
        int m_MarkerCount;
        bool m_Closed;

        public string Path { get; }
        public int FrameCount => m_FrameCount;

        public SessionWriter(string path, SessionInfo info)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            m_Stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            m_Writer = new BinaryWriter(m_Stream, Encoding.UTF8);
            info.formatVersion = SessionFile.FormatVersion;
            m_Writer.Write(SessionFile.Magic);
            m_Writer.Write(SessionFile.FormatVersion);
            m_Writer.Write(JsonUtility.ToJson(info));
        }

        public void WriteFrame(in FrameSample sample)
        {
            m_Writer.Write(SessionFile.TagFrame);
            m_Writer.Write(sample.frame);
            m_Writer.Write(sample.time);
            m_Writer.Write(sample.deltaTime);
            WriteHead(m_Writer, sample.head);
            WriteController(m_Writer, sample.left);
            WriteController(m_Writer, sample.right);
            m_FrameCount++;
            // Flush about once a second so a crash or force-quit loses little.
            if (m_FrameCount % 90 == 0)
                m_Writer.Flush();
        }

        public void WriteMarker(in Marker marker)
        {
            m_Writer.Write(SessionFile.TagMarker);
            m_Writer.Write(marker.frame);
            m_Writer.Write(marker.time);
            m_Writer.Write((byte)marker.kind);
            m_Writer.Write(marker.name ?? string.Empty);
            m_MarkerCount++;
        }

        public void Flush()
        {
            if (m_Closed)
                return;
            m_Writer.Flush();
            m_Stream.Flush(true);
        }

        public void Dispose()
        {
            if (m_Closed)
                return;
            m_Closed = true;
            m_Writer.Write(SessionFile.TagEnd);
            m_Writer.Write(m_FrameCount);
            m_Writer.Write(m_MarkerCount);
            m_Writer.Flush();
            m_Writer.Dispose();
        }

        static void WritePose(BinaryWriter w, in PoseSample p)
        {
            w.Write(p.position.x);
            w.Write(p.position.y);
            w.Write(p.position.z);
            w.Write(p.rotation.x);
            w.Write(p.rotation.y);
            w.Write(p.rotation.z);
            w.Write(p.rotation.w);
        }

        static void WriteHead(BinaryWriter w, in HeadSample h)
        {
            w.Write(h.isTracked);
            w.Write(h.trackingState);
            WritePose(w, h.pose);
        }

        static void WriteController(BinaryWriter w, in ControllerSample c)
        {
            w.Write(c.connected);
            if (!c.connected)
                return;
            w.Write(c.isTracked);
            w.Write(c.trackingState);
            WritePose(w, c.device);
            WritePose(w, c.pointer);
            w.Write(c.trigger);
            w.Write(c.grip);
            w.Write(c.stick.x);
            w.Write(c.stick.y);
            w.Write((uint)c.buttons);
        }
    }

    public static class SessionReader
    {
        public static SessionCapture Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            using (var r = new BinaryReader(stream, Encoding.UTF8))
            {
                var magic = r.ReadBytes(4);
                for (var i = 0; i < 4; i++)
                    if (magic.Length < 4 || magic[i] != SessionFile.Magic[i])
                        throw new InvalidDataException($"{path} is not an XR session capture.");
                var version = r.ReadInt32();
                if (version > SessionFile.FormatVersion)
                    throw new InvalidDataException($"{path} uses format version {version}; this package reads up to {SessionFile.FormatVersion}. Update the package.");

                var capture = new SessionCapture { info = JsonUtility.FromJson<SessionInfo>(r.ReadString()) };
                capture.truncated = true;
                try
                {
                    while (stream.Position < stream.Length)
                    {
                        var tag = r.ReadByte();
                        if (tag == SessionFile.TagFrame)
                        {
                            var s = new FrameSample
                            {
                                frame = r.ReadInt32(),
                                time = r.ReadDouble(),
                                deltaTime = r.ReadSingle(),
                            };
                            s.head = ReadHead(r);
                            s.left = ReadController(r);
                            s.right = ReadController(r);
                            capture.frames.Add(s);
                        }
                        else if (tag == SessionFile.TagMarker)
                        {
                            capture.markers.Add(new Marker
                            {
                                frame = r.ReadInt32(),
                                time = r.ReadDouble(),
                                kind = (MarkerKind)r.ReadByte(),
                                name = r.ReadString(),
                            });
                        }
                        else if (tag == SessionFile.TagEnd)
                        {
                            r.ReadInt32();
                            r.ReadInt32();
                            capture.truncated = false;
                            break;
                        }
                        else
                        {
                            throw new InvalidDataException($"Unknown record 0x{tag:X2} at byte {stream.Position - 1} in {path}.");
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    // Recording was cut short: keep the complete records read so far.
                }
                return capture;
            }
        }

        static PoseSample ReadPose(BinaryReader r)
        {
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            return new PoseSample(p, q);
        }

        static HeadSample ReadHead(BinaryReader r)
        {
            return new HeadSample { isTracked = r.ReadBoolean(), trackingState = r.ReadInt32(), pose = ReadPose(r) };
        }

        static ControllerSample ReadController(BinaryReader r)
        {
            var c = new ControllerSample { connected = r.ReadBoolean() };
            if (!c.connected)
                return c;
            c.isTracked = r.ReadBoolean();
            c.trackingState = r.ReadInt32();
            c.device = ReadPose(r);
            c.pointer = ReadPose(r);
            c.trigger = r.ReadSingle();
            c.grip = r.ReadSingle();
            c.stick = new Vector2(r.ReadSingle(), r.ReadSingle());
            c.buttons = (ControllerButtons)r.ReadUInt32();
            return c;
        }
    }
}
