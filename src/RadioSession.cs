using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using PhotonPlayer = Photon.Realtime.Player;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace PlayerRadio
{
    public enum NetMode
    {
        /// <summary>Not in any Photon room (main menu etc.): your radio plays just for you.</summary>
        Local,
        /// <summary>PhotonNetwork.OfflineMode (PEAK's "play offline"): your radio, no networking.</summary>
        Offline,
        /// <summary>Online room: your radio state is published; other players' radios are played here.</summary>
        Online
    }

    /// <summary>
    /// Per-player radios. Every player owns exactly one radio that plays THEIR OWN Music folder from THEIR character.
    /// The owner is the only writer of its state, published as the player custom property "pr.r" (Photon delivers
    /// player properties to late joiners automatically). Everyone plays everyone's radio in 3D at the owner's body.
    /// Missing tracks are streamed by hash from the owner or anyone else who has them ("pr.have").
    /// </summary>
    public sealed class RadioSession : MonoBehaviourPunCallbacks, IOnEventCallback
    {
        public static RadioSession Instance { get; private set; }

        public const byte EvTransfer = 172;
        public const string PropRadio = "pr.r";
        public const string PropHave = "pr.have";
        public const string PropVersion = "pr.ver";
        private const int HavePrefixBytes = 8;
        private const int MaxHaveEntries = 400;

        public TrackLibrary Library { get; private set; }
        public FileTransfer Transfer { get; private set; }

        /// <summary>Your own radio.</summary>
        public Radio Mine { get; private set; }
        private readonly Dictionary<int, Radio> _others = new Dictionary<int, Radio>();
        public IEnumerable<Radio> Others => _others.Values;
        public IEnumerable<Radio> AllRadios { get { yield return Mine; foreach (var r in _others.Values) yield return r; } }

        /// <summary>Your playlist (your Music folder only), refreshed on rescans.</summary>
        public List<TrackMeta> MyTracks { get; private set; } = new List<TrackMeta>();
        private int _tracksLibVersion = -1;

        public Hotkey KeyPlayPause, KeySkip, KeyPrevious, KeyMuteOthers, KeyVolDown, KeyVolUp, KeyOpenFolder;
        public List<Hotkey> Hotkeys;

        /// <summary>Local-only toggle (F8): silence everyone else's radio for you.</summary>
        public bool OthersMuted { get; private set; }

        private readonly Dictionary<int, HashSet<string>> _haves = new Dictionary<int, HashSet<string>>();
        private readonly HashSet<int> _incompatibleWarned = new HashSet<int>();
        private int _publishedHaveVersion = -1;
        private float _nextHavePublish;
        private float _nextDownloadCheck;

        private string _context;
        private string _modeLine;
        private bool _bindingsDumpedInGame;

        public string Toast { get; private set; }
        public float ToastUntil { get; private set; }

        // ======================================================================
        // Lifecycle
        // ======================================================================

        private void Awake()
        {
            Instance = this;
            Library = new TrackLibrary();
            Transfer = new FileTransfer(this);
            Mine = new Radio(0, true);

            KeyPlayPause = new Hotkey("PlayPause", Plugin.KeyPlayPause);
            KeySkip = new Hotkey("Skip", Plugin.KeySkip);
            KeyPrevious = new Hotkey("Previous", Plugin.KeyPrevious);
            KeyMuteOthers = new Hotkey("ToggleOtherRadios", Plugin.KeyMuteOthers);
            KeyVolDown = new Hotkey("VolumeDown", Plugin.KeyVolumeDown);
            KeyVolUp = new Hotkey("VolumeUp", Plugin.KeyVolumeUp);
            KeyOpenFolder = new Hotkey("OpenMusicFolder", Plugin.KeyOpenMusicFolder);
            Hotkeys = new List<Hotkey> { KeyPlayPause, KeySkip, KeyPrevious, KeyMuteOthers, KeyVolDown, KeyVolUp, KeyOpenFolder };

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            Plugin.Log.LogInfo($"Photon: PUN {PhotonNetwork.PunVersion}; per-player radios via player property '{PropRadio}', transfers on event {EvTransfer} (channel 1).");
            Diagnostics.DumpInputBindings(Hotkeys, "startup");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            foreach (var r in _others.Values) r.Dispose();
            _others.Clear();
            Mine?.Dispose();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            try
            {
                Library.Tick(this);
                CheckContext();
                RefreshMyTracks();
                HandleHotkeys();
                OwnerTick();
                PublishHavesIfNeeded();
                Transfer.Tick(Time.unscaledDeltaTime);
                EnsureDownloads();
                foreach (var r in AllRadios) r.UpdatePlayback(this);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Radio update error: {e}");
            }
        }

        private void LateUpdate()
        {
            try { foreach (var r in AllRadios) r.Place(this); }
            catch (Exception e) { Plugin.Log.LogError($"Radio placement error: {e}"); }
        }

        // ======================================================================
        // Mode / context
        // ======================================================================

        public static double Now => (PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) ? PhotonNetwork.Time : Time.realtimeSinceStartupAsDouble;

        public int LocalActor => PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        public NetMode Mode
        {
            get
            {
                if (PhotonNetwork.OfflineMode) return NetMode.Offline;
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return NetMode.Local;
                return NetMode.Online;
            }
        }

        public bool IsOnline => Mode == NetMode.Online;

        /// <summary>"0.2" for 0.2.x: radios and transfers are only exchanged with the same major.minor.</summary>
        public static string Protocol => Plugin.Version.Substring(0, Plugin.Version.LastIndexOf('.'));

        public static bool HasMod(PhotonPlayer p) => p?.CustomProperties != null && p.CustomProperties.ContainsKey(PropVersion);

        public static bool IsCompatible(PhotonPlayer p) =>
            p != null && p.CustomProperties != null && p.CustomProperties.TryGetValue(PropVersion, out var v) &&
            v is string s && (s == Protocol || s.StartsWith(Protocol + ".", StringComparison.Ordinal));

        public bool IsCompatiblePeer(int actor) => IsOnline && actor != LocalActor && IsCompatible(PhotonNetwork.CurrentRoom.GetPlayer(actor));

        public int ModdedPlayerCount()
        {
            if (!IsOnline) return 1;
            return PhotonNetwork.CurrentRoom.Players.Values.Count(p => p != null && (p.IsLocal || IsCompatible(p)));
        }

        public string ModeShort
        {
            get
            {
                switch (Mode)
                {
                    case NetMode.Local: return "solo (local)";
                    case NetMode.Offline: return "solo (offline)";
                    default:
                        int n = ModdedPlayerCount();
                        return n <= 1 ? "online (no other radios)" : $"online ({n - 1} other radio{(n == 2 ? "" : "s")})";
                }
            }
        }

        private void CheckContext()
        {
            string ctx = PhotonNetwork.OfflineMode ? "offline"
                : PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? "room:" + PhotonNetwork.CurrentRoom.Name
                : "local";
            if (ctx != _context)
            {
                Plugin.Log.LogInfo($"Radio context {_context ?? "(start)"} -> {ctx}.");
                _context = ctx;
                OnContextChanged();
            }

            int players = PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 1;
            string line = $"{Mode} (offlineMode={PhotonNetwork.OfflineMode}, inRoom={PhotonNetwork.InRoom}, localActor={LocalActor}, players={players}, withRadio={ModdedPlayerCount()})";
            if (line != _modeLine)
            {
                _modeLine = line;
                Plugin.Log.LogInfo("Session mode: " + line);
            }
        }

        private void OnContextChanged()
        {
            // Other players' radios belong to the old room.
            foreach (var r in _others.Values) r.Dispose();
            _others.Clear();
            _haves.Clear();
            _incompatibleWarned.Clear();
            Transfer.Reset();
            _publishedHaveVersion = -1;

            // Your radio keeps going, re-anchored to the new clock (network time vs. local time).
            var s = Mine.State.Clone();
            float pos = Mine.Source != null && Mine.Source.isPlaying && Mine.ClipHex == s.CurrentHex ? Mine.Source.time : Mathf.Max(0f, s.AnchorPos);
            s.AnchorPos = pos;
            s.AnchorTime = Now;
            Mine.State = s;
            Mine.OnSceneChanged();

            if (IsOnline)
            {
                var props = new Hashtable { { PropVersion, Plugin.Version }, { PropRadio, Mine.State.Serialize() } };
                PhotonNetwork.LocalPlayer.SetCustomProperties(props);
                Plugin.Log.LogInfo($"Joined room '{PhotonNetwork.CurrentRoom.Name}' (SendRate={PhotonNetwork.SendRate}, SerializationRate={PhotonNetwork.SerializationRate}); published my radio (rev {Mine.State.Rev}).");
                foreach (var p in PhotonNetwork.CurrentRoom.Players.Values)
                    if (p != null && !p.IsLocal) ApplyPlayerProps(p, p.CustomProperties, true);
            }
        }

        // ======================================================================
        // Player properties (other players' radios)
        // ======================================================================

        private void ApplyPlayerProps(PhotonPlayer p, Hashtable props, bool initial)
        {
            if (p == null || p.IsLocal || props == null) return;
            int a = p.ActorNumber;
            if (props.TryGetValue(PropVersion, out var ver))
            {
                if (!IsCompatible(p))
                {
                    if (_incompatibleWarned.Add(a))
                    {
                        Plugin.Log.LogWarning($"{p.NickName} (actor {a}) runs Player Radio {ver}; this is {Plugin.Version}. Radios only work between {Protocol}.x versions.");
                        ShowToast($"{Util.Trunc(p.NickName, 20)} has Player Radio {ver} (needs {Protocol}.x)", 6f);
                    }
                    RemoveRadio(a, "incompatible version");
                    return;
                }
                if (!initial) Plugin.Log.LogInfo($"{p.NickName} (actor {a}) has Player Radio {ver}.");
            }
            if (!IsCompatible(p)) return;

            if (props.TryGetValue(PropHave, out var haveObj) && haveObj is byte[] hb)
            {
                var set = new HashSet<string>();
                for (int i = 0; i + HavePrefixBytes <= hb.Length; i += HavePrefixBytes)
                    set.Add(Util.Hex(hb.Skip(i).Take(HavePrefixBytes).ToArray()));
                _haves[a] = set;
                Plugin.Verbose($"{p.NickName} can send {set.Count} track(s).");
            }

            if (props.TryGetValue(PropRadio, out var stObj) && stObj is byte[] stb)
            {
                RadioState s;
                try { s = RadioState.Deserialize(stb); }
                catch (Exception e) { Plugin.Log.LogWarning($"Could not read {p.NickName}'s radio: {e.Message}"); return; }
                if (!_others.TryGetValue(a, out var r))
                {
                    r = new Radio(a, false);
                    _others[a] = r;
                    var c = FindCharacter(a, false);
                    if (c != null && CharacterAlive(c)) r.LastKnownPos = TorsoPos(c);
                }
                else if (s.Rev == r.State.Rev) return;
                var old = r.State;
                r.State = s;
                if (s.Current != null && old.CurrentHex != s.CurrentHex)
                    Plugin.Log.LogInfo($"{p.NickName}'s radio -> '{s.Current.Title}' ({(Library.Has(s.Current.Hex) ? "have it" : "need to download")}).");
                if (old.Playing != s.Playing)
                    Plugin.Log.LogInfo($"{p.NickName}'s radio {(s.Playing ? "playing" : "paused")} (rev {s.Rev}).");
                _nextDownloadCheck = 0f;
            }
        }

        private void RemoveRadio(int actor, string why)
        {
            if (!_others.TryGetValue(actor, out var r)) return;
            r.Dispose();
            _others.Remove(actor);
            Plugin.Log.LogInfo($"Removed {PlayerName(actor)}'s radio ({why}).");
        }

        // ======================================================================
        // Your own radio (you are the only writer)
        // ======================================================================

        private void RefreshMyTracks()
        {
            if (Library.Version == _tracksLibVersion || Library.Scanning) return;
            _tracksLibVersion = Library.Version;
            MyTracks = Library.OwnTracks();
            Plugin.Log.LogInfo($"My playlist: {MyTracks.Count} track(s) from {Library.MusicDir}.");
            // "Next" may have changed (tracks added/removed): republish if so.
            var s = Mine.State;
            if (s.Current != null)
            {
                var next = NextAfter(s.Current.Hex, +1);
                if (next?.Hex != s.Next?.Hex)
                {
                    var ns = s.Clone();
                    ns.Next = next;
                    Commit(ns, "playlist changed");
                }
            }
        }

        private int IndexInMyTracks(string hex)
        {
            if (hex == null) return -1;
            for (int i = 0; i < MyTracks.Count; i++) if (MyTracks[i].Hex == hex) return i;
            return -1;
        }

        private TrackMeta NextAfter(string hex, int dir)
        {
            int n = MyTracks.Count;
            if (n == 0) return null;
            int idx = IndexInMyTracks(hex);
            if (idx < 0) return dir > 0 ? MyTracks[0] : MyTracks[n - 1];
            return MyTracks[((idx + dir) % n + n) % n];
        }

        private void Commit(RadioState s, string what)
        {
            s.Rev = Mine.State.Rev + 1;
            if (s.Current != null && s.Next == null && MyTracks.Count > 1) s.Next = NextAfter(s.Current.Hex, +1);
            var old = Mine.State;
            Mine.State = s;
            if (s.Current != null && old.CurrentHex != s.CurrentHex) Plugin.Log.LogInfo($"My radio -> '{s.Current.Title}' ({what}).");
            if (old.Playing != s.Playing) Plugin.Log.LogInfo($"My radio {(s.Playing ? "playing" : "paused")} ({what}).");
            Plugin.Verbose($"My radio rev {s.Rev}: {what}; track={s.CurrentHex?.Substring(0, 12)} next={s.Next?.Hex?.Substring(0, 12)} playing={s.Playing} anchor={s.AnchorPos:0.00}@{s.AnchorTime:0.000}");
            if (IsOnline) PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { PropRadio, s.Serialize() } });
        }

        private void PlayPause()
        {
            double now = Now;
            var s = Mine.State.Clone();
            if (s.Playing)
            {
                s.AnchorPos = s.PositionAt(now);
                s.AnchorTime = now;
                s.Playing = false;
                Commit(s, "pause");
                ShowToast("Your radio: paused");
                return;
            }
            if (MyTracks.Count == 0)
            {
                ShowToast($"No songs yet - press [{KeyOpenFolder.Display}] to open your Music folder", 5f);
                return;
            }
            if (s.Current == null || IndexInMyTracks(s.CurrentHex) < 0)
            {
                s.Current = MyTracks[0];
                s.Next = null;
                s.AnchorPos = 0;
            }
            else if (s.Current.Duration > 0 && s.AnchorPos >= s.Current.Duration - 0.5f) s.AnchorPos = 0;
            s.Playing = true;
            s.AnchorTime = now;
            Commit(s, "play");
            ShowToast("Your radio: playing (everyone near you hears it)");
        }

        private void Step(int dir)
        {
            if (MyTracks.Count == 0)
            {
                ShowToast($"No songs yet - press [{KeyOpenFolder.Display}] to open your Music folder", 5f);
                return;
            }
            double now = Now;
            var s = Mine.State.Clone();
            if (dir < 0 && s.Current != null && s.PositionAt(now) > 3f)
            {
                s.AnchorPos = 0; // restart current track
                s.AnchorTime = now;
                Commit(s, "restart");
                return;
            }
            s.Current = NextAfter(s.CurrentHex, dir);
            s.Next = null;
            s.AnchorPos = 0;
            s.AnchorTime = now;
            Commit(s, dir > 0 ? "next" : "previous");
        }

        private void OwnerTick()
        {
            var s = Mine.State;
            if (!s.Playing || s.Current == null) return;
            string hex = s.CurrentHex;
            float dur = Mine.Duration;
            float pos = s.PositionAt(Now);
            bool ended = (dur > 0 && pos >= dur) || pos > 4 * 3600;
            bool gone = !Library.Has(hex) && !Mine.IsLoading && Mine.ClipHex != hex; // file deleted from the Music folder
            if (!ended && !gone) return;
            if (MyTracks.Count == 0)
            {
                var ns = s.Clone();
                ns.Playing = false;
                ns.AnchorPos = 0;
                ns.AnchorTime = Now;
                Commit(ns, "end of track (no songs left)");
                return;
            }
            var n2 = s.Clone();
            n2.Current = NextAfter(hex, +1);
            n2.Next = null;
            n2.AnchorPos = 0;
            n2.AnchorTime = Now;
            Commit(n2, gone ? "track file removed" : "auto-advance");
        }

        // ======================================================================
        // Streaming: who has what, what to fetch
        // ======================================================================

        private void PublishHavesIfNeeded()
        {
            if (!IsOnline || Library.Scanning || Library.Version == _publishedHaveVersion || Time.unscaledTime < _nextHavePublish) return;
            _publishedHaveVersion = Library.Version;
            _nextHavePublish = Time.unscaledTime + 3f;
            var hashes = Library.AllHashes(MaxHaveEntries);
            var bytes = new byte[hashes.Count * HavePrefixBytes];
            for (int i = 0; i < hashes.Count; i++) Buffer.BlockCopy(hashes[i], 0, bytes, i * HavePrefixBytes, HavePrefixBytes);
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { PropHave, bytes } });
            Plugin.Verbose($"Published {hashes.Count} sendable track id(s).");
        }

        /// <summary>Who could send this track: the radio's owner first, then anyone else who has it.</summary>
        public List<int> HoldersOf(string hex, int ownerHint)
        {
            var list = new List<int>();
            if (!IsOnline || hex == null) return list;
            string prefix = hex.Substring(0, HavePrefixBytes * 2);
            bool Has(int a) =>
                (_haves.TryGetValue(a, out var set) && set.Contains(prefix)) ||
                (_others.TryGetValue(a, out var r) && (r.State.CurrentHex == hex || r.State.Next?.Hex == hex));
            if (ownerHint != 0 && IsCompatiblePeer(ownerHint) && Has(ownerHint)) list.Add(ownerHint);
            foreach (var p in PhotonNetwork.CurrentRoom.Players.Values.OrderBy(p => p.ActorNumber))
            {
                int a = p.ActorNumber;
                if (a == ownerHint || p.IsLocal || !IsCompatible(p)) continue;
                if (Has(a)) list.Add(a);
            }
            return list;
        }

        /// <summary>Track ids any radio currently plays or plays next (protected from cache cleanup).</summary>
        public HashSet<string> HashesInUse()
        {
            var set = new HashSet<string>();
            foreach (var r in AllRadios)
            {
                if (r.State.CurrentHex != null) set.Add(r.State.CurrentHex);
                if (r.State.Next != null) set.Add(r.State.Next.Hex);
            }
            return set;
        }

        private void EnsureDownloads()
        {
            if (!IsOnline || Time.unscaledTime < _nextDownloadCheck) return;
            _nextDownloadCheck = Time.unscaledTime + 0.5f;

            var listener = ListenerPosition();
            var radios = _others.Values
                .OrderBy(r => r.State.Playing ? 0 : 1)
                .ThenBy(r => listener.HasValue && r.Emitter != null ? Vector3.Distance(listener.Value, r.Emitter.transform.position) : 0f)
                .ToList();

            // Cancel transfers nobody needs any more (owner skipped past them / left).
            var wanted = new HashSet<string>();
            foreach (var r in radios)
            {
                if (r.State.CurrentHex != null) wanted.Add(r.State.CurrentHex);
                if (r.State.Next != null) wanted.Add(r.State.Next.Hex);
            }
            foreach (var hex in Transfer.DownloadingHexes.ToList())
                if (!wanted.Contains(hex)) Transfer.Cancel(hex);

            if (!Plugin.EnableStreaming.Value) return;
            // 1) tracks radios are on right now, playing ones and nearest first
            bool missingCurrent = false;
            foreach (var r in radios)
            {
                var m = r.State.Current;
                if (m == null || Library.Has(m.Hex) || Transfer.IsDownloading(m.Hex)) continue;
                missingCurrent = true;
                if (!Transfer.CanStartDownload) return;
                Transfer.Request(m, r.Actor, false);
            }
            // 2) prefetch each owner's next track, once nothing current is waiting
            if (missingCurrent) return;
            foreach (var r in radios)
            {
                var m = r.State.Next;
                if (m == null || Library.Has(m.Hex) || Transfer.IsDownloading(m.Hex)) continue;
                if (!Transfer.CanStartDownload) return;
                Transfer.Request(m, r.Actor, true);
            }
        }

        public void OnDownloadComplete(string hex, string title)
        {
            foreach (var r in _others.Values)
                if (r.State.CurrentHex == hex) { ShowToast($"Got '{Util.Trunc(title, 36)}' ({PlayerName(r.Actor)}'s radio)"); break; }
            _nextHavePublish = 0f; // tell others we can send it too
        }

        // ======================================================================
        // Ducking support / HUD queries
        // ======================================================================

        private AudioListener _listener;
        private float _nextListenerLookup;

        public Vector3? ListenerPosition()
        {
            if (_listener == null || !_listener.isActiveAndEnabled)
            {
                if (Time.unscaledTime >= _nextListenerLookup)
                {
                    _nextListenerLookup = Time.unscaledTime + 2f;
                    _listener = FindObjectsByType<AudioListener>(FindObjectsSortMode.None).FirstOrDefault(l => l.isActiveAndEnabled);
                }
                if (_listener == null) return null;
            }
            return _listener.transform.position;
        }

        /// <summary>
        /// How much the game's music should be ducked, 0..1. Your own radio counts fully while it is sounding and you
        /// can hear it; other radios count by how loud they are where you are (distance-weighted rolloff). With
        /// DuckOnlyWhenInRange, other radios only count when you are clearly within earshot.
        /// </summary>
        public float DuckTarget(out string why)
        {
            why = null;
            float best = 0f;
            var lp = ListenerPosition();
            foreach (var r in AllRadios)
            {
                if (r?.Emitter == null || !r.Sounding()) continue;
                float vol = r.IsLocal ? Plugin.MyRadioVolume.Value : (OthersMuted ? 0f : Plugin.OtherRadiosVolume.Value);
                if (vol <= 0.01f) continue;
                float w = lp.HasValue ? r.Emitter.LoudnessAt(lp.Value) : (r.IsLocal ? 1f : 0f);
                if (!r.IsLocal)
                {
                    if (!r.HasPosition) continue;
                    if (Plugin.DuckOnlyWhenInRange.Value) w = w >= 0.45f ? 1f : 0f;
                    else if (w < 0.05f) w = 0f;
                }
                if (w > best)
                {
                    best = w;
                    why = r.IsLocal ? "your radio" : PlayerName(r.Actor) + "'s radio";
                }
            }
            return best;
        }

        /// <summary>Other playing radios, nearest first, with distance (or -1 if unknown).</summary>
        public List<(Radio radio, float dist)> NearbyRadios(int max)
        {
            var lp = ListenerPosition();
            float range = Mathf.Max(5f, Plugin.MaxDistance.Value);
            var list = new List<(Radio, float)>();
            foreach (var r in _others.Values)
            {
                if (!r.On || !r.HasPosition) continue;
                float d = lp.HasValue ? Vector3.Distance(lp.Value, r.Emitter.transform.position) : -1f;
                if (d > range) continue;
                list.Add((r, d));
            }
            return list.OrderBy(x => x.Item2).Take(max).ToList();
        }

        // ======================================================================
        // Characters
        // ======================================================================

        public int OwnerActor(Character c)
        {
            if (c == null) return 0;
            var v = c.photonView;
            if (v != null && v.Owner != null) return v.Owner.ActorNumber;
            return c.IsLocal ? LocalActor : 0;
        }

        public Character FindCharacter(int actor, bool local)
        {
            if (local) return Character.localCharacter;
            if (actor == 0) return null;
            foreach (var c in Character.AllCharacters)
            {
                if (c == null || c.isBot || !c.gameObject.activeInHierarchy) continue;
                if (!c.IsLocal && OwnerActor(c) == actor) return c;
            }
            return null;
        }

        public static bool CharacterAlive(Character c)
        {
            if (c == null || c.data == null) return false;
            return !c.data.dead && !c.IsGhost;
        }

        /// <summary>Character.DeathPos() is where the game parks dead bodies (0, 5000, -5000).</summary>
        public static bool InDeathVoid(Vector3 p) => (p - Character.DeathPos()).sqrMagnitude < 200f * 200f;

        public static Vector3 TorsoPos(Character c)
        {
            try
            {
                var dict = c.refs?.ragdoll != null ? c.refs.ragdoll.partDict : null;
                if (dict != null && dict.TryGetValue(BodypartType.Torso, out var bp) && bp != null) return bp.transform.position;
            }
            catch { /* ragdoll not built yet */ }
            return c.transform.position + Vector3.up;
        }

        private Radio RadioOf(Character c)
        {
            if (c == null || c.isBot) return null;
            if (c.IsLocal) return Mine;
            return _others.TryGetValue(OwnerActor(c), out var r) ? r : null;
        }

        /// <summary>Nameplate tag: this character's own radio is playing.</summary>
        public bool IsRadioCharacter(Character c)
        {
            var r = RadioOf(c);
            return r != null && r.On;
        }

        public string PlayerName(int actor)
        {
            if (actor == LocalActor && actor != -1) return "you";
            if (actor == 0) return "you";
            var p = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            return p != null && !string.IsNullOrEmpty(p.NickName) ? p.NickName : $"actor {actor}";
        }

        /// <summary>Harmony: Character.Awake postfix (every character, local and remote).</summary>
        public void OnCharacterAwake(Character c)
        {
            if (c == null || c.isBot) return;
            Plugin.Verbose($"Character spawned: {c.name} (actor {OwnerActor(c)}, local={c.IsLocal})");
            if (!_bindingsDumpedInGame && c == Character.localCharacter)
            {
                _bindingsDumpedInGame = true;
                Diagnostics.DumpInputBindings(Hotkeys, "local character spawned");
            }
        }

        /// <summary>Harmony: Character.RPCA_Die prefix, runs on every client before the body is warped away.</summary>
        public void OnCharacterDying(Character c)
        {
            var r = RadioOf(c);
            if (r == null) return;
            var p = TorsoPos(c);
            if (InDeathVoid(p)) return;
            r.DeathPos = p;
            if (r.On) Plugin.Log.LogInfo($"{(r.IsLocal ? "You" : PlayerName(r.Actor))} died; {(r.IsLocal ? "your" : "their")} radio keeps playing at {p} until revived.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"Scene loaded: {scene.name} ({mode}).");
            foreach (var r in AllRadios) r.OnSceneChanged();
        }

        // ======================================================================
        // Input
        // ======================================================================

        public void ShowToast(string msg, float seconds = 3f)
        {
            Toast = msg;
            ToastUntil = Time.unscaledTime + seconds;
        }

        private void HandleHotkeys()
        {
            if (!Application.isFocused) return;
            // Opening the folder doesn't affect gameplay, so it also works while a game menu has input.
            if (KeyOpenFolder.Pressed()) RadioActions.OpenMusicFolder();
            var gui = GUIManager.instance;
            if (gui != null && (gui.windowBlockingInput || gui.wheelActive)) return; // same checks as Character.CanDoInput

            if (KeyPlayPause.Pressed()) PlayPause();
            if (KeySkip.Pressed()) Step(+1);
            if (KeyPrevious.Pressed()) Step(-1);
            if (KeyMuteOthers.Pressed())
            {
                OthersMuted = !OthersMuted;
                Plugin.Log.LogInfo($"Other players' radios {(OthersMuted ? "muted" : "unmuted")} (local only).");
                ShowToast(OthersMuted ? "Other radios muted (only for you)" : "Other radios unmuted");
            }
            if (KeyVolDown.Pressed()) ChangeVolume(-Plugin.VolumeStep.Value);
            if (KeyVolUp.Pressed()) ChangeVolume(Plugin.VolumeStep.Value);
        }

        private void ChangeVolume(float delta)
        {
            Plugin.MyRadioVolume.Value = Mathf.Clamp01(Mathf.Round((Plugin.MyRadioVolume.Value + delta) * 100f) / 100f);
            ShowToast($"Your radio volume {Mathf.RoundToInt(Plugin.MyRadioVolume.Value * 100)}% (as you hear it)");
        }

        // ======================================================================
        // Photon callbacks
        // ======================================================================

        public void OnEvent(EventData ev)
        {
            if (ev.Code != EvTransfer) return;
            if (!(ev.CustomData is byte[] data)) return;
            if (!IsCompatiblePeer(ev.Sender)) return; // e.g. a 0.1.x client: different wire format
            try { Transfer.OnEvent(ev.Sender, data); }
            catch (Exception e) { Plugin.Log.LogWarning($"Bad radio event {ev.Code} from actor {ev.Sender}: {e.Message}"); }
        }

        public override void OnJoinedRoom() => CheckContext();

        public override void OnLeftRoom() => CheckContext();

        public override void OnPlayerEnteredRoom(PhotonPlayer newPlayer)
        {
            Plugin.Log.LogInfo($"Player joined: {newPlayer.NickName} (actor {newPlayer.ActorNumber}, has Player Radio: {HasMod(newPlayer)}).");
            ApplyPlayerProps(newPlayer, newPlayer.CustomProperties, true);
        }

        public override void OnPlayerPropertiesUpdate(PhotonPlayer targetPlayer, Hashtable changedProps)
        {
            if (targetPlayer == null || targetPlayer.IsLocal || !IsOnline) return;
            ApplyPlayerProps(targetPlayer, changedProps, false);
        }

        public override void OnPlayerLeftRoom(PhotonPlayer otherPlayer)
        {
            int a = otherPlayer.ActorNumber;
            Plugin.Log.LogInfo($"Player left: {otherPlayer.NickName} (actor {a}).");
            RemoveRadio(a, "left");
            _haves.Remove(a);
            Transfer.OnPlayerLeft(a);
            _nextDownloadCheck = 0f;
        }
    }
}
