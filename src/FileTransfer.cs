using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PlayerRadio
{
    /// <summary>
    /// Event 172: chunked, rate-limited, reliable file streaming on Photon channel 1 (game traffic uses channel 0).
    /// Tracks are addressed only by SHA-256, so any modded player who has the file can send it.
    /// Limits: uploads have a total cap (StreamRateKBps) plus a per-recipient cap (whatever that recipient asked for);
    /// downloads run at most <see cref="MaxDownloads"/> at a time and split DownloadRateKBps between their senders.
    /// Only modded clients are ever targeted (TargetActors), vanilla clients never see these events.
    /// </summary>
    public sealed class FileTransfer
    {
        public const int ChunkSize = 8 * 1024;
        public const int MaxDownloads = 2;
        private const byte MsgReq = 1, MsgHdr = 2, MsgChunk = 3, MsgErr = 5, MsgRate = 6;
        private const int MaxOutgoingJobs = 6;
        private const int QueueBackoff = 40;        // QueuedOutgoingCommands threshold
        private const float StallTimeout = 15f;
        private const float RetryFailedAfter = 30f;

        private static readonly SendOptions BulkSend = new SendOptions { DeliveryMode = DeliveryMode.Reliable, Channel = 1 };

        private sealed class Outgoing
        {
            public int Actor;
            public byte[] Hash;
            public string Hex;
            public Task<byte[]> Read;
            public byte[] Data;
            public int Next;
            public int Count;
            public bool HeaderSent;
            public TrackMeta Meta;
            public double RateBytes;   // per-recipient cap
            public double Tokens;
        }

        private sealed class Incoming
        {
            public byte[] Hash;
            public string Hex;
            public TrackMeta Meta;
            public int OwnerHint;      // the radio owner we want it for (asked first)
            public int Holder;
            public readonly HashSet<int> Tried = new HashSet<int>();
            public bool HeaderSeen;
            public long Size;
            public int Count;
            public bool[] Got;
            public int GotCount;
            public FileStream Fs;
            public string PartPath;
            public float LastActivity;
            public Task<string> Verify;
            public float StartedAt;
            public long BytesThisSession;
            public int RequestedKBps;
            public bool Prefetch;
        }

        private readonly RadioSession _session;
        private readonly List<Outgoing> _out = new List<Outgoing>();
        private readonly Dictionary<string, Incoming> _in = new Dictionary<string, Incoming>();
        private readonly Dictionary<string, float> _failedAt = new Dictionary<string, float>();
        private int _rr;
        private double _tokens;

        public FileTransfer(RadioSession session) { _session = session; }

        public int DownloadCount => _in.Count;
        public bool CanStartDownload => _in.Count < MaxDownloads;
        public bool IsDownloading(string hex) => hex != null && _in.ContainsKey(hex);
        public int UploadCount => _out.Select(o => o.Actor).Distinct().Count();
        public IEnumerable<string> DownloadingHexes => _in.Keys;

        public bool RecentlyFailed(string hex) =>
            hex != null && _failedAt.TryGetValue(hex, out var t) && Time.unscaledTime - t < RetryFailedAfter;

        public string StatusFor(string hex)
        {
            if (hex == null || !_in.TryGetValue(hex, out var j)) return null;
            string title = Util.Trunc(j.Meta?.Title ?? j.Hex.Substring(0, 8), 32);
            if (j.Verify != null) return $"Verifying '{title}'...";
            if (!j.HeaderSeen) return $"Requesting '{title}' from {_session.PlayerName(j.Holder)}...";
            float pct = j.Count == 0 ? 0 : 100f * j.GotCount / j.Count;
            float secs = Mathf.Max(0.5f, Time.unscaledTime - j.StartedAt);
            return $"{(j.Prefetch ? "Prefetching" : "Downloading")} '{title}' {pct:0}% ({j.BytesThisSession / 1024f / secs:0} KB/s from {_session.PlayerName(j.Holder)})";
        }

        public void Reset()
        {
            _out.Clear();
            foreach (var j in _in.Values.ToList()) Abort(j, false);
            _in.Clear();
            _failedAt.Clear();
        }

        // ---------------- receiving side ----------------

        /// <summary>Start downloading a track we don't have. Returns false if it can't start right now.</summary>
        public bool Request(TrackMeta meta, int ownerHint, bool prefetch)
        {
            if (!Plugin.EnableStreaming.Value || meta == null || !CanStartDownload) return false;
            string hex = meta.Hex;
            if (_in.ContainsKey(hex) || RecentlyFailed(hex)) return false;
            if (meta.Size > (long)Plugin.MaxStreamFileMB.Value * 1024 * 1024)
            {
                _failedAt[hex] = Time.unscaledTime;
                Plugin.Log.LogWarning($"Not downloading '{meta.Title}': {meta.Size / 1048576f:0.0} MB exceeds MaxStreamFileMB.");
                return false;
            }
            var job = new Incoming
            {
                Hash = meta.Hash, Hex = hex,
                Meta = new TrackMeta { Hash = meta.Hash, Title = meta.Title, Duration = meta.Duration, Size = meta.Size, Ext = meta.Ext },
                OwnerHint = ownerHint, Prefetch = prefetch,
                StartedAt = Time.unscaledTime, LastActivity = Time.unscaledTime
            };
            _in[hex] = job;
            if (!SendRequestToNextHolder(job))
            {
                _in.Remove(hex);
                _failedAt[hex] = Time.unscaledTime;
                return false;
            }
            Rebalance();
            return true;
        }

        private int ShareKBps() => Math.Max(8, Plugin.DownloadRateKBps.Value / Math.Max(1, _in.Count));

        /// <summary>Split the total download budget between active downloads; tell senders when a share changes.</summary>
        private void Rebalance()
        {
            int share = ShareKBps();
            foreach (var j in _in.Values)
            {
                if (j.RequestedKBps == share || j.Holder == 0) continue;
                j.RequestedKBps = share;
                var payload = new byte[1 + 32 + 4];
                payload[0] = MsgRate;
                Buffer.BlockCopy(j.Hash, 0, payload, 1, 32);
                BitConverter.GetBytes(share).CopyTo(payload, 33);
                PhotonNetwork.RaiseEvent(RadioSession.EvTransfer, payload, new RaiseEventOptions { TargetActors = new[] { j.Holder } }, BulkSend);
            }
        }

        private bool SendRequestToNextHolder(Incoming job)
        {
            var candidates = _session.HoldersOf(job.Hex, job.OwnerHint).Where(a => !job.Tried.Contains(a)).ToList();
            if (candidates.Count == 0) return false;

            job.Holder = candidates[0];
            job.Tried.Add(job.Holder);
            job.LastActivity = Time.unscaledTime;
            job.RequestedKBps = ShareKBps();
            int from = FirstMissing(job);
            var payload = new byte[1 + 32 + 4 + 4];
            payload[0] = MsgReq;
            Buffer.BlockCopy(job.Hash, 0, payload, 1, 32);
            BitConverter.GetBytes(from).CopyTo(payload, 33);
            BitConverter.GetBytes(job.RequestedKBps).CopyTo(payload, 37);
            bool ok = PhotonNetwork.RaiseEvent(RadioSession.EvTransfer, payload,
                new RaiseEventOptions { TargetActors = new[] { job.Holder } }, BulkSend);
            Plugin.Log.LogInfo($"Requesting {(job.Prefetch ? "(prefetch) " : "")}'{job.Meta?.Title}' ({job.Hex.Substring(0, 12)}) from {_session.PlayerName(job.Holder)} (actor {job.Holder}), chunk {from}, {job.RequestedKBps} KB/s (sent={ok}).");
            return ok;
        }

        private static int FirstMissing(Incoming j)
        {
            if (j.Got == null) return 0;
            for (int i = 0; i < j.Got.Length; i++) if (!j.Got[i]) return i;
            return j.Got.Length;
        }

        private void Abort(Incoming j, bool markFailed)
        {
            try { j.Fs?.Dispose(); } catch { }
            j.Fs = null;
            if (markFailed)
            {
                _failedAt[j.Hex] = Time.unscaledTime;
                try { if (j.PartPath != null && File.Exists(j.PartPath)) File.Delete(j.PartPath); } catch { }
            }
            _in.Remove(j.Hex);
            Rebalance();
        }

        private void RetryOrAbort(Incoming j, string why)
        {
            Plugin.Log.LogInfo($"Transfer of {j.Hex.Substring(0, 12)} from actor {j.Holder}: {why}; trying another holder.");
            try { j.Fs?.Flush(); } catch { }
            if (!SendRequestToNextHolder(j))
            {
                Plugin.Log.LogInfo($"Nobody else can send {j.Hex.Substring(0, 12)} right now; will retry in {RetryFailedAfter:0}s.");
                Abort(j, true);
            }
        }

        private void OnHeader(int sender, Incoming j, BinaryReader r)
        {
            if (sender != j.Holder) return;
            long size = r.ReadInt64();
            int chunkSize = r.ReadInt32();
            int count = r.ReadInt32();
            string ext = Util.SanitizeExt(r.ReadString());
            if (chunkSize != ChunkSize || size <= 0 || size > (long)Plugin.MaxStreamFileMB.Value * 1024 * 1024 || count != (int)((size + ChunkSize - 1) / ChunkSize))
            {
                Plugin.Log.LogWarning($"Rejecting transfer header from {sender} (size {size}, chunk {chunkSize}, count {count}).");
                Abort(j, true);
                return;
            }
            if (!j.HeaderSeen || j.Size != size)
            {
                try { j.Fs?.Dispose(); } catch { }
                j.PartPath = Path.Combine(_session.Library.CacheDir, j.Hex + ".part");
                j.Fs = new FileStream(j.PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                j.Fs.SetLength(size);
                j.Size = size;
                j.Count = count;
                j.Got = new bool[count];
                j.GotCount = 0;
            }
            j.Meta.Ext = ext;
            j.HeaderSeen = true;
            j.LastActivity = Time.unscaledTime;
            Plugin.Verbose($"Transfer header: {j.Hex.Substring(0, 12)} {size} bytes in {count} chunks from {sender}.");
        }

        private void OnChunk(int sender, Incoming j, byte[] payload, int offset)
        {
            if (!j.HeaderSeen || sender != j.Holder || j.Verify != null) return;
            int index = BitConverter.ToInt32(payload, offset);
            int len = payload.Length - offset - 4;
            if (index < 0 || index >= j.Count || len <= 0 || len > ChunkSize) return;
            if (j.Got[index]) return;
            j.Fs.Seek((long)index * ChunkSize, SeekOrigin.Begin);
            j.Fs.Write(payload, offset + 4, len);
            j.Got[index] = true;
            j.GotCount++;
            j.BytesThisSession += len;
            j.LastActivity = Time.unscaledTime;

            if (j.GotCount == j.Count)
            {
                j.Fs.Flush();
                j.Fs.Dispose();
                j.Fs = null;
                string part = j.PartPath;
                j.Verify = Task.Run(() =>
                {
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(part))
                        return Util.Hex(sha.ComputeHash(fs));
                });
            }
        }

        private void FinishVerify(Incoming j)
        {
            string got = j.Verify.IsFaulted ? null : j.Verify.Result;
            if (!string.Equals(got, j.Hex, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning($"Downloaded track {j.Hex.Substring(0, 12)} failed SHA-256 check (got {got}); discarding.");
                Abort(j, true);
                return;
            }
            string final = _session.Library.CachePathFor(j.Hex, j.Meta.Ext);
            try
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(j.PartPath, final);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not move download into cache: {e.Message}");
                Abort(j, true);
                return;
            }
            float secs = Time.unscaledTime - j.StartedAt;
            Plugin.Log.LogInfo($"Downloaded '{j.Meta.Title}' ({j.Size / 1024} KB) in {secs:0.0}s from actor {j.Holder}, SHA-256 verified -> {final}");
            _in.Remove(j.Hex);
            Rebalance();
            _session.Library.RegisterCached(j.Meta, final);
            _session.Library.EnforceCacheLimit(_session.HashesInUse());
            _session.OnDownloadComplete(j.Hex, j.Meta.Title);
        }

        // ---------------- sending side ----------------

        private void Refuse(int sender, byte[] hash, string hex, string reason)
        {
            Plugin.Log.LogInfo($"Refusing track request {hex.Substring(0, 12)} from actor {sender}: {reason}.");
            var err = new byte[33];
            err[0] = MsgErr;
            Buffer.BlockCopy(hash, 0, err, 1, 32);
            PhotonNetwork.RaiseEvent(RadioSession.EvTransfer, err, new RaiseEventOptions { TargetActors = new[] { sender } }, BulkSend);
        }

        private void OnRequest(int sender, byte[] hash, BinaryReader r)
        {
            int from = r.ReadInt32();
            int kbps = r.ReadInt32();
            string hex = Util.Hex(hash);
            string reason = null;
            if (!Plugin.EnableStreaming.Value) reason = "streaming disabled";
            else if (!_session.IsCompatiblePeer(sender)) reason = "sender is not running a compatible Player Radio";
            else if (!_session.Library.TryGetLocalPath(hex, out _)) reason = "not found";
            else if (_out.Count >= MaxOutgoingJobs && !_out.Any(o => o.Actor == sender && o.Hex == hex)) reason = "busy";
            var meta = _session.Library.Get(hex);
            if (reason == null && meta != null && meta.Size > (long)Plugin.MaxStreamFileMB.Value * 1024 * 1024) reason = "file too large";
            if (reason != null) { Refuse(sender, hash, hex, reason); return; }

            _out.RemoveAll(o => o.Actor == sender && o.Hex == hex);
            _session.Library.TryGetLocalPath(hex, out var path);
            var job = new Outgoing { Actor = sender, Hash = hash, Hex = hex, Meta = meta, Next = Math.Max(0, from) };
            SetRate(job, kbps);
            job.Read = Task.Run(() => File.ReadAllBytes(path));
            _out.Add(job);
            Plugin.Log.LogInfo($"Sending '{meta?.Title}' to {_session.PlayerName(sender)} (actor {sender}) from chunk {job.Next} at <= {job.RateBytes / 1024:0} KB/s.");
        }

        private static void SetRate(Outgoing job, int requestedKBps)
        {
            int cap = Plugin.StreamRateKBps.Value;
            int kb = requestedKBps <= 0 ? cap : Math.Min(requestedKBps, cap);
            job.RateBytes = Math.Max(8, kb) * 1024.0;
        }

        private void PumpOutgoing(float dt)
        {
            if (_out.Count == 0) { _tokens = 0; return; }
            double total = Plugin.StreamRateKBps.Value * 1024.0;
            _tokens = Math.Min(_tokens + total * dt, ChunkSize * 2.0);
            foreach (var j in _out) j.Tokens = Math.Min(j.Tokens + j.RateBytes * dt, ChunkSize * 2.0);

            int guard = 0, idle = 0;
            while (_out.Count > 0 && _tokens >= ChunkSize && guard++ < 16 && idle < _out.Count)
            {
                var peer = PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
                if (peer != null && peer.QueuedOutgoingCommands > QueueBackoff) return; // let Photon drain first

                _rr = (_rr + 1) % _out.Count;
                var job = _out[_rr];
                if (job.Data == null)
                {
                    if (!job.Read.IsCompleted) { idle++; continue; }
                    if (job.Read.IsFaulted) { Plugin.Log.LogWarning($"Read failed for {job.Hex}: {job.Read.Exception?.GetBaseException().Message}"); _out.RemoveAt(_rr); continue; }
                    job.Data = job.Read.Result;
                    job.Count = (job.Data.Length + ChunkSize - 1) / ChunkSize;
                }
                if (job.Tokens < ChunkSize) { idle++; continue; } // this recipient's own cap
                idle = 0;
                var opts = new RaiseEventOptions { TargetActors = new[] { job.Actor } };
                if (!job.HeaderSent)
                {
                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(MsgHdr);
                        w.Write(job.Hash);
                        w.Write((long)job.Data.Length);
                        w.Write(ChunkSize);
                        w.Write(job.Count);
                        w.Write(job.Meta?.Ext ?? ".mp3");
                        PhotonNetwork.RaiseEvent(RadioSession.EvTransfer, ms.ToArray(), opts, BulkSend);
                    }
                    job.HeaderSent = true;
                }
                if (job.Next >= job.Count)
                {
                    Plugin.Log.LogInfo($"Finished sending {job.Hex.Substring(0, 12)} to actor {job.Actor}.");
                    _out.RemoveAt(_rr);
                    continue;
                }
                int off = job.Next * ChunkSize;
                int len = Math.Min(ChunkSize, job.Data.Length - off);
                var payload = new byte[1 + 32 + 4 + len];
                payload[0] = MsgChunk;
                Buffer.BlockCopy(job.Hash, 0, payload, 1, 32);
                BitConverter.GetBytes(job.Next).CopyTo(payload, 33);
                Buffer.BlockCopy(job.Data, off, payload, 37, len);
                if (!PhotonNetwork.RaiseEvent(RadioSession.EvTransfer, payload, opts, BulkSend)) return;
                job.Next++;
                _tokens -= len;
                job.Tokens -= len;
            }
        }

        // ---------------- common ----------------

        public void Tick(float dt)
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)
            {
                if (_out.Count > 0 || _in.Count > 0) Reset();
                return;
            }
            PumpOutgoing(dt);

            foreach (var j in _in.Values.ToList())
            {
                if (j.Verify != null)
                {
                    if (j.Verify.IsCompleted) FinishVerify(j);
                }
                else if (Time.unscaledTime - j.LastActivity > StallTimeout)
                {
                    RetryOrAbort(j, "stalled");
                }
            }
        }

        /// <summary>Drop a prefetch nobody needs anymore (e.g. its radio's owner left or skipped past it).</summary>
        public void Cancel(string hex)
        {
            if (hex != null && _in.TryGetValue(hex, out var j) && j.Verify == null)
            {
                Plugin.Verbose($"Cancelling download {hex.Substring(0, 12)} (no longer needed).");
                Abort(j, false);
            }
        }

        public void OnPlayerLeft(int actor)
        {
            _out.RemoveAll(o => o.Actor == actor);
            foreach (var j in _in.Values.Where(j => j.Holder == actor && j.Verify == null).ToList())
                RetryOrAbort(j, "sender left");
        }

        public void OnEvent(int sender, byte[] data)
        {
            if (data == null || data.Length < 33) return;
            byte kind = data[0];
            var hash = new byte[32];
            Buffer.BlockCopy(data, 1, hash, 0, 32);
            string hex = Util.Hex(hash);
            try
            {
                switch (kind)
                {
                    case MsgReq:
                        using (var r = new BinaryReader(new MemoryStream(data, 33, data.Length - 33))) OnRequest(sender, hash, r);
                        break;
                    case MsgRate:
                        if (data.Length >= 37)
                            foreach (var o in _out) if (o.Actor == sender && o.Hex == hex) SetRate(o, BitConverter.ToInt32(data, 33));
                        break;
                    case MsgHdr:
                        if (_in.TryGetValue(hex, out var jh))
                            using (var r = new BinaryReader(new MemoryStream(data, 33, data.Length - 33))) OnHeader(sender, jh, r);
                        break;
                    case MsgChunk:
                        if (_in.TryGetValue(hex, out var jc)) OnChunk(sender, jc, data, 33);
                        break;
                    case MsgErr:
                        if (_in.TryGetValue(hex, out var je) && je.Holder == sender && je.Verify == null) RetryOrAbort(je, "sender refused");
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Bad transfer message {kind} from actor {sender}: {e.Message}");
            }
        }
    }
}
