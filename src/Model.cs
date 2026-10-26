using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace PlayerRadio
{
    public sealed class TrackMeta
    {
        public byte[] Hash;      // SHA-256 of file bytes (32 bytes)
        public string Title;
        public float Duration;   // seconds
        public long Size;
        public string Ext;       // ".mp3" / ".wav" / ".ogg"
        private string _hex;
        public string Hex => _hex ??= Util.Hex(Hash);
    }

    /// <summary>
    /// One player's own radio. Written ONLY by its owner and published as that player's custom property "pr.r"
    /// (Photon keeps player properties for late joiners). Positions are anchored to the network clock.
    /// </summary>
    public sealed class RadioState
    {
        private const byte Format = 2; // 0.2.x per-player format (0.1.x used a shared room state)

        public int Rev;
        public TrackMeta Current;      // null = nothing selected
        public TrackMeta Next;         // what the owner will play next (listeners prefetch it)
        public bool Playing;
        public double AnchorTime;      // net time at which position == AnchorPos
        public float AnchorPos;        // seconds

        public string CurrentHex => Current?.Hex;

        public float PositionAt(double now)
        {
            if (!Playing) return AnchorPos;
            double dt = now - AnchorTime;
            if (dt < 0 || dt > 86400) dt = 0; // guards against clock-base changes / wraparound
            return AnchorPos + (float)dt;
        }

        public RadioState Clone() => (RadioState)MemberwiseClone(); // TrackMeta instances are treated as immutable

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Format);
                w.Write(Rev);
                w.Write(Playing);
                w.Write(AnchorTime);
                w.Write(AnchorPos);
                w.Write(Current != null);
                if (Current != null) Util.WriteMeta(w, Current);
                w.Write(Next != null);
                if (Next != null) Util.WriteMeta(w, Next);
                return ms.ToArray();
            }
        }

        public static RadioState Deserialize(byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                if (r.ReadByte() != Format) throw new InvalidDataException("Unknown radio state format (different Player Radio version?)");
                var s = new RadioState
                {
                    Rev = r.ReadInt32(),
                    Playing = r.ReadBoolean(),
                    AnchorTime = r.ReadDouble(),
                    AnchorPos = r.ReadSingle()
                };
                if (r.ReadBoolean()) s.Current = Util.ReadMeta(r);
                if (r.ReadBoolean()) s.Next = Util.ReadMeta(r);
                return s;
            }
        }
    }

    public static class Util
    {
        public static string Hex(byte[] b)
        {
            if (b == null) return null;
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        public static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static void WriteMeta(BinaryWriter w, TrackMeta m)
        {
            w.Write(m.Hash);
            w.Write(Trunc(m.Title, 60));
            w.Write(m.Duration);
            w.Write(m.Size);
            w.Write(m.Ext ?? ".mp3");
        }

        public static TrackMeta ReadMeta(BinaryReader r)
        {
            return new TrackMeta
            {
                Hash = r.ReadBytes(32),
                Title = r.ReadString(),
                Duration = r.ReadSingle(),
                Size = r.ReadInt64(),
                Ext = SanitizeExt(r.ReadString())
            };
        }

        public static string SanitizeExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext == ".wav" || ext == ".ogg" ? ext : ".mp3";
        }

        public static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        public static string Time(float seconds)
        {
            if (seconds < 0 || float.IsNaN(seconds)) seconds = 0;
            int t = (int)seconds;
            return $"{t / 60}:{t % 60:00}";
        }
    }
}
