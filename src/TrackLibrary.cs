using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace PlayerRadio
{
    /// <summary>
    /// Local tracks: the user's Music folder plus the download cache.
    /// Music:  BepInEx/config/PlayerRadio/Music   (survives mod updates; mod managers' "open config folder")
    /// Cache:  BepInEx/cache/PlayerRadio          (downloaded tracks named &lt;sha256&gt;.&lt;ext&gt; + index.tsv)
    /// </summary>
    public sealed class TrackLibrary
    {
        private sealed class LocalTrack
        {
            public TrackMeta Meta;
            public string Path;
            public bool IsCache;
        }

        private sealed class IndexRow
        {
            public long Size;
            public long MTime;
            public string Hex;
            public float Duration;
            public string Title;
        }

        private const long MaxFileBytes = 512L * 1024 * 1024;

        public readonly string MusicDir;
        public readonly string CacheDir;
        private readonly string _indexPath;

        private Dictionary<string, LocalTrack> _byHex = new Dictionary<string, LocalTrack>();
        private readonly Dictionary<string, IndexRow> _index = new Dictionary<string, IndexRow>(StringComparer.OrdinalIgnoreCase);

        public int Version { get; private set; }
        public bool Scanning { get; private set; }
        public string ScanStatus { get; private set; } = "";
        private string _lastSignature;
        private float _nextCheck;

        public TrackLibrary()
        {
            MusicDir = ResolveMusicDir();
            CacheDir = System.IO.Path.Combine(Paths.CachePath, "PlayerRadio");
            _indexPath = System.IO.Path.Combine(CacheDir, "index.tsv");
            try
            {
                Directory.CreateDirectory(MusicDir);
                Directory.CreateDirectory(CacheDir);
                MusicFolderGuide.Ensure(MusicDir);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not create Music/Cache folders: {e.Message}");
            }
            LoadIndex();
            Plugin.Log.LogInfo($"Music folder: {MusicDir}");
            Plugin.Log.LogInfo($"Cache folder: {CacheDir}");
        }

        public static string ResolveMusicDir()
        {
            var o = Plugin.MusicFolderOverride?.Value;
            if (!string.IsNullOrWhiteSpace(o)) return o.Trim();
            return System.IO.Path.Combine(Paths.ConfigPath, "PlayerRadio", "Music");
        }

        // ---------- queries ----------

        public int Count => _byHex.Count;
        public int LibraryCount => _byHex.Values.Count(t => !t.IsCache);

        public bool TryGetLocalPath(string hex, out string path)
        {
            path = null;
            if (hex == null || !_byHex.TryGetValue(hex, out var t)) return false;
            if (!File.Exists(t.Path)) return false;
            path = t.Path;
            return true;
        }

        public bool Has(string hex) => hex != null && _byHex.ContainsKey(hex);

        public TrackMeta Get(string hex) => hex != null && _byHex.TryGetValue(hex, out var t) ? t.Meta : null;

        /// <summary>Your own playlist: playable tracks from YOUR Music folder only (downloaded cache files never join it), sorted by title.</summary>
        public List<TrackMeta> OwnTracks() =>
            _byHex.Values.Where(t => !t.IsCache && t.Meta.Duration > 0).Select(t => t.Meta)
                .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Hex, StringComparer.Ordinal).ToList();

        /// <summary>Hashes of every track we could send (own Music folder first, then cache), for the "pr.have" player property.</summary>
        public List<byte[]> AllHashes(int max) =>
            _byHex.Values.OrderBy(t => t.IsCache ? 1 : 0).Select(t => t.Meta.Hash).Take(max).ToList();

        public string CachePathFor(string hex, string ext) => System.IO.Path.Combine(CacheDir, hex + Util.SanitizeExt(ext));

        public void SetDuration(string hex, float seconds)
        {
            if (hex == null || !_byHex.TryGetValue(hex, out var t) || seconds <= 0) return;
            if (Mathf.Abs(t.Meta.Duration - seconds) < 0.01f) return;
            t.Meta.Duration = seconds;
            if (_index.TryGetValue(t.Path, out var row)) row.Duration = seconds;
            SaveIndex();
        }

        /// <summary>Called by the transfer manager after a verified download landed in the cache.</summary>
        public void RegisterCached(TrackMeta meta, string path)
        {
            var fi = new FileInfo(path);
            var m = new TrackMeta { Hash = meta.Hash, Title = meta.Title, Duration = meta.Duration, Size = fi.Length, Ext = Util.SanitizeExt(meta.Ext) };
            _byHex[m.Hex] = new LocalTrack { Meta = m, Path = path, IsCache = true };
            _index[path] = new IndexRow { Size = fi.Length, MTime = fi.LastWriteTimeUtc.Ticks, Hex = m.Hex, Duration = m.Duration, Title = m.Title };
            SaveIndex();
            Version++; // the next Tick() sees the new file and rescans (cheap: index hit)
        }

        public void EnforceCacheLimit(ICollection<string> keep)
        {
            try
            {
                long cap = (long)Plugin.CacheSizeMB.Value * 1024 * 1024;
                var files = AudioLoader.Extensions.SelectMany(e => new DirectoryInfo(CacheDir).GetFiles("*" + e)).OrderBy(f => f.LastAccessTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= cap) break;
                    if (keep != null && keep.Contains(System.IO.Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant())) continue;
                    total -= f.Length;
                    var hex = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    f.Delete();
                    _byHex.Remove(hex);
                    _index.Remove(f.FullName);
                    Plugin.Log.LogInfo($"Cache limit: deleted {f.Name}");
                    Version++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cache cleanup failed: {e.Message}");
            }
        }

        // ---------- scanning ----------

        /// <summary>Call every frame; rescans when the folders' contents change (polled every 10 s).</summary>
        public void Tick(MonoBehaviour host)
        {
            if (Scanning || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 10f;
            string sig = ComputeSignature();
            if (sig != _lastSignature)
            {
                _lastSignature = sig;
                host.StartCoroutine(Scan());
            }
        }

        private List<(string path, bool isCache)> ListFiles()
        {
            var result = new List<(string, bool)>();
            // Only audio extensions are listed (.txt, images, playlists etc. are ignored).
            // The Music folder is scanned recursively (max 8 levels, hidden/system folders skipped); the cache is flat.
            void Add(string dir, bool cache, int depth)
            {
                if (!Directory.Exists(dir)) return;
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception e) { Plugin.Verbose($"Skipping folder '{dir}': {e.Message}"); return; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (Array.IndexOf(AudioLoader.Extensions, ext) >= 0) result.Add((f, cache));
                }
                if (cache || depth >= 8) return;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { return; }
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                foreach (var sub in subs)
                {
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (System.IO.Path.GetFileName(sub).StartsWith(".")) continue;
                    }
                    catch { continue; }
                    Add(sub, false, depth + 1);
                }
            }
            try
            {
                Add(MusicDir, false, 0);
                Add(CacheDir, true, 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Listing music folders failed: {e.Message}");
            }
            return result;
        }

        private string ComputeSignature()
        {
            var sb = new StringBuilder();
            foreach (var (p, _) in ListFiles())
            {
                try
                {
                    var fi = new FileInfo(p);
                    sb.Append(p).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
                catch { /* file vanished */ }
            }
            return sb.ToString();
        }

        private IEnumerator Scan()
        {
            Scanning = true;
            ScanStatus = "Scanning music...";
            var files = ListFiles();
            var indexSnapshot = new Dictionary<string, IndexRow>(_index, StringComparer.OrdinalIgnoreCase);

            // Hash on a worker thread (SHA-256 of the full file = track id).
            var task = Task.Run(() =>
            {
                var rows = new List<(string path, bool cache, IndexRow row)>();
                using (var sha = SHA256.Create())
                {
                    foreach (var (path, cache) in files)
                    {
                        try
                        {
                            var fi = new FileInfo(path);
                            if (fi.Length <= 0 || fi.Length > MaxFileBytes) continue;
                            if (indexSnapshot.TryGetValue(path, out var known) && known.Size == fi.Length && known.MTime == fi.LastWriteTimeUtc.Ticks && known.Hex != null)
                            {
                                rows.Add((path, cache, known));
                                continue;
                            }
                            string hex;
                            using (var fs = File.OpenRead(path)) hex = Util.Hex(sha.ComputeHash(fs));
                            if (cache)
                            {
                                // Cached files are named by their hash; reject corrupted ones.
                                var expected = System.IO.Path.GetFileNameWithoutExtension(path);
                                if (!string.Equals(expected, hex, StringComparison.OrdinalIgnoreCase)) { File.Delete(path); continue; }
                            }
                            rows.Add((path, cache, new IndexRow
                            {
                                Size = fi.Length,
                                MTime = fi.LastWriteTimeUtc.Ticks,
                                Hex = hex,
                                Duration = known != null && known.Hex == hex ? known.Duration : 0f,
                                Title = known?.Title ?? (cache ? hex.Substring(0, 8) : System.IO.Path.GetFileNameWithoutExtension(path))
                            }));
                        }
                        catch (Exception) { /* unreadable file: skip */ }
                    }
                }
                return rows;
            });
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Plugin.Log.LogError($"Music scan failed: {task.Exception?.GetBaseException().Message}");
                Scanning = false;
                ScanStatus = "";
                yield break;
            }

            var newMap = new Dictionary<string, LocalTrack>();
            _index.Clear();
            foreach (var (path, cache, row) in task.Result)
            {
                if (!cache) row.Title = System.IO.Path.GetFileNameWithoutExtension(path);
                _index[path] = row;
                // Prefer the Music-folder copy over a cached copy of the same track.
                if (newMap.TryGetValue(row.Hex, out var existing) && !existing.IsCache) continue;
                newMap[row.Hex] = new LocalTrack
                {
                    Path = path,
                    IsCache = cache,
                    Meta = new TrackMeta { Hash = Util.FromHex(row.Hex), Title = row.Title, Duration = row.Duration, Size = row.Size, Ext = Util.SanitizeExt(System.IO.Path.GetExtension(path)) }
                };
            }
            _byHex = newMap;

            // Probe durations (decode once; remembered in index.tsv).
            var unknown = newMap.Values.Where(t => !t.IsCache && t.Meta.Duration <= 0).ToList();
            int i = 0;
            foreach (var t in unknown)
            {
                i++;
                ScanStatus = $"Reading track lengths {i}/{unknown.Count}...";
                AudioClip clip = null;
                string err = null;
                yield return AudioLoader.Load(t.Path, (c, e) => { clip = c; err = e; });
                if (clip != null)
                {
                    t.Meta.Duration = clip.length;
                    if (_index.TryGetValue(t.Path, out var row)) row.Duration = clip.length;
                    UnityEngine.Object.Destroy(clip);
                }
                else
                {
                    Plugin.Log.LogWarning($"Can't decode '{System.IO.Path.GetFileName(t.Path)}': {err}. It will be ignored.");
                }
            }

            SaveIndex();
            Version++;
            Scanning = false;
            ScanStatus = "";
            Plugin.Log.LogInfo($"Music scan done: {LibraryCount} track(s) in Music folder, {_byHex.Count - LibraryCount} in cache.");
            foreach (var t in _byHex.Values.Where(x => !x.IsCache))
                Plugin.Verbose($"  {t.Meta.Hex.Substring(0, 12)} {Util.Time(t.Meta.Duration)} {t.Meta.Title}");
        }

        // ---------- index persistence ----------

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath)) return;
                foreach (var line in File.ReadAllLines(_indexPath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length < 6) continue;
                    _index[p[0]] = new IndexRow
                    {
                        Size = long.Parse(p[1], CultureInfo.InvariantCulture),
                        MTime = long.Parse(p[2], CultureInfo.InvariantCulture),
                        Hex = p[3],
                        Duration = float.Parse(p[4], CultureInfo.InvariantCulture),
                        Title = p[5]
                    };
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read {_indexPath} (will rebuild): {e.Message}");
                _index.Clear();
            }
        }

        private void SaveIndex()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _index)
                {
                    string title = (kv.Value.Title ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kv.Value.MTime.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kv.Value.Hex).Append('\t')
                      .Append(kv.Value.Duration.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(title).Append('\n');
                }
                File.WriteAllText(_indexPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not write {_indexPath}: {e.Message}");
            }
        }
    }
}
