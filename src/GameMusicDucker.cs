using System;
using System.Collections.Generic;
using System.Linq;
using Peak;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using Zorro.Core;

namespace PlayerRadio
{
    /// <summary>
    /// Fades the game's own music out while the shared radio is audible for the local player, and back in afterwards.
    ///
    /// How PEAK plays music (verified in the decompiled Assembly-CSharp):
    ///  - AmbienceAudio (component under the local Character): public AudioSource mainMusic (biome "climb stingers",
    ///    tombClimb) and stingerSource (start / sunrise / sunset / night stingers). Its Update() writes
    ///    mainMusic.volume / stingerSource.volume EVERY frame (Mathf.Lerp toward 0 / 0.35, or fixed 0.35-1.0).
    ///  - Scene objects tagged "Music" (RemoveMusic.Start uses GameObject.FindGameObjectsWithTag("Music") and divides
    ///    their AudioSource.volume by 1.01 each frame), toggled by EnableMusic / BiomeMusicCheck via SetActive.
    ///  - MyresAmbience.fearMusic (volume Lerped every frame), Peak.ScoutmasterSoulPillar.music (volume ramped).
    ///  - Global mixer setting: MusicVolumeSetting -> exposed param "MusicVolume". There is no game-side duck/mute flag.
    ///
    /// Because the game rewrites AudioSource.volume every frame (and reads it back in its Lerps), we do NOT touch
    /// AudioSource.volume and we do NOT touch the "Music" mixer group / MusicVolume (the radio itself is routed to
    /// "Music" so the player's Music slider applies to it). Instead we attach a tiny DSP filter (OnAudioFilterRead)
    /// to each game-music AudioSource's GameObject that multiplies its samples by a shared gain. The game's own volume
    /// logic keeps working untouched; our gain is applied on top, after it, in the audio thread.
    ///
    /// Only actual music is ducked (0.1.2): each candidate is classified by its CURRENT clip. "Au_" clips (PEAK's
    /// sound-effect/ambience naming) and names matching ExcludeFromDucking (default "Ambience") are skipped; objects
    /// that are only tagged "Music" must also have "music" in their object or clip name.
    /// </summary>
    public sealed class GameMusicDucker : MonoBehaviour
    {
        private const float FadeOutSeconds = 1.5f;
        private const float FadeInSeconds = 2.0f;
        private const float RescanInterval = 3f;
        private const float GapGraceSeconds = 3f; // stay ducked across track changes / clip loading
        private const float EvalInterval = 0.25f;

        // Read by the audio thread.
        internal static volatile float SharedGain = 1f;

        private float _progress;            // 0 = not ducked, 1 = fully ducked
        private float _nextScan, _nextEval;
        private float _lastAudible = -100f;
        private bool _wasWanted;
        private float _lastTarget;
        private string _lastWhy;
        private readonly List<MusicDuckFilter> _filters = new List<MusicDuckFilter>();

        public static GameMusicDucker Instance { get; private set; }
        public bool IsDucking => _progress > 0.001f;
        public int FilterCount => _filters.Count;

        private void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            _nextScan = 0f;           // pick up the new scene's music immediately
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RestoreAll("plugin unload");
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit() => RestoreAll(null);

        /// <summary>Immediately un-duck and remove every filter we added.</summary>
        public void RestoreAll(string why)
        {
            SharedGain = 1f;
            _progress = 0f;
            int n = 0;
            foreach (var f in _filters)
            {
                if (f == null) continue;
                Destroy(f);
                n++;
            }
            _filters.Clear();
            _cands.Clear();
            if (why != null && n > 0) Plugin.Log.LogInfo($"Game music restored ({why}); removed {n} duck filter(s).");
        }

        private void Update()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.Log.LogError($"Game music ducker error: {e}"); SharedGain = 1f; }
        }

        private void Tick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            string why = null;
            float target = Plugin.DuckGameMusic.Value ? RadioDuckTarget(out why) : 0f;
            bool wanted = target > 0.001f;

            if (wanted && !_wasWanted)
            {
                _nextScan = 0f; // make sure filters exist before the fade starts
                Plugin.Verbose($"Game music: ducking ({why}, depth {target:0.00}).");
            }
            else if (!wanted && _wasWanted) Plugin.Verbose("Game music: restoring (no radio audible).");
            _wasWanted = wanted;

            // Move towards the target depth at the configured fade speeds.
            if (_progress < target) _progress = Mathf.Min(target, _progress + dt / FadeOutSeconds);
            else if (_progress > target) _progress = Mathf.Max(target, _progress - dt / FadeInSeconds);

            float floor = Mathf.Clamp01(Plugin.GameMusicDuckVolume.Value);
            float eased = _progress * _progress * (3f - 2f * _progress); // smoothstep
            SharedGain = Mathf.Lerp(1f, floor, eased);

            // Only scan while ducking matters (cheap enough: every 3 s, real time).
            if ((wanted || _progress > 0f) && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Scan();
            }
            // Re-classify by CURRENT clip (clips change, e.g. AmbienceAudio.stingerSource) a few times a second.
            if ((wanted || _progress > 0f) && Time.unscaledTime >= _nextEval)
            {
                _nextEval = Time.unscaledTime + EvalInterval;
                Evaluate(false);
            }
        }

        /// <summary>0..1 duck depth wanted by the radios (yours, or the loudest other radio where you are).</summary>
        private float RadioDuckTarget(out string why)
        {
            why = null;
            var session = RadioSession.Instance;
            if (session == null) return 0f;
            float t = session.DuckTarget(out why);
            if (t > 0.001f) { _lastAudible = Time.unscaledTime; _lastTarget = t; _lastWhy = why; return t; }
            // Short gaps (next track loading, a hiccup): hold the previous depth briefly.
            if (Time.unscaledTime - _lastAudible < GapGraceSeconds) { why = _lastWhy; return _lastTarget; }
            return 0f;
        }

        // ------------------------------------------------------------------
        // Finding the game's music sources
        // ------------------------------------------------------------------

        /// <summary>A game AudioSource that MIGHT be music; whether it is ducked is decided per current clip.</summary>
        private sealed class Candidate
        {
            public AudioSource Src;
            public string Via;          // why it was picked up
            public bool KnownField;     // explicit music field in a game script
            public bool MusicGroup;     // routed to the Music / Music_Setting mixer group
            public bool Tagged;         // GameObject tag "Music"
            public HashSet<AudioClip> MusicClips; // AmbienceAudio sources: the only clips its code ever assigns
            public MusicDuckFilter Filter;
            public bool? Decision;
            public string DecidedClip;
            public string Reason;
        }

        private readonly Dictionary<AudioSource, Candidate> _cands = new Dictionary<AudioSource, Candidate>();
        private string _excludeRaw;
        private string[] _exclude = new string[0];

        /// <summary>Our own radio emitters are never ducked.</summary>
        private static bool IsRadio(AudioSource s) => s.GetComponentInParent<RadioEmitter>(true) != null;

        private void Scan()
        {
            _filters.RemoveAll(f => f == null);
            foreach (var dead in _cands.Keys.Where(k => k == null).ToList()) _cands.Remove(dead);
            var musicGroups = FindMusicGroups();

            var found = new Dictionary<AudioSource, Candidate>();
            Candidate Get(AudioSource s)
            {
                if (s == null || IsRadio(s)) return null;
                if (!found.TryGetValue(s, out var c)) found[s] = c = new Candidate { Src = s };
                return c;
            }
            void Known(AudioSource s, string via)
            {
                var c = Get(s);
                if (c == null) return;
                c.KnownField = true;
                c.Via = c.Via ?? via;
            }

            // 1) Explicit music fields in game scripts (names verified in the decompiled code).
            //    AmbienceAudio.stingerSource only ever gets startStinger/sunRiseStinger/sunSetStinger/nightStinger and the
            //    biome sunrise/sunset clips assigned in code; its inspector default clip (e.g. 'Au_Cartoon_Wobble1') is
            //    filtered out by the per-clip rules below.
            foreach (var a in FindObjectsByType<AmbienceAudio>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var clips = AmbienceMusicClips(a);
                Known(a.mainMusic, "AmbienceAudio.mainMusic");
                Known(a.stingerSource, "AmbienceAudio.stingerSource");
                foreach (var src in new[] { a.mainMusic, a.stingerSource })
                    if (src != null && found.TryGetValue(src, out var c)) c.MusicClips = clips;
            }
            foreach (var m in FindObjectsByType<MyresAmbience>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Known(m.fearMusic, "MyresAmbience.fearMusic");
            foreach (var p in FindObjectsByType<ScoutmasterSoulPillar>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Known(p.music, "ScoutmasterSoulPillar.music");
            //    GUIManager.stingerSound plays the hero-title stingers (SetHeroTitle / ForcePlayStinger) through 'Music'.

            // 2) Sources routed to the Music / Music_Setting mixer groups, and objects tagged "Music"
            //    (the tag is also used on ambience objects, so tag-only sources must look like music; see Decide()).
            foreach (var s in FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (s == null || IsRadio(s)) continue;
                var g = s.outputAudioMixerGroup;
                bool inGroup = g != null && musicGroups.Contains(g);
                bool tagged = false;
                try { tagged = s.gameObject.CompareTag("Music"); } catch { /* tag missing in this build */ }
                if (!inGroup && !tagged) continue;
                var c = Get(s);
                if (inGroup) { c.MusicGroup = true; c.Via = c.Via ?? "mixer group '" + g.name + "'"; }
                if (tagged) { c.Tagged = true; c.Via = c.Via ?? "tag 'Music'"; }
            }

            int added = 0;
            foreach (var kv in found)
            {
                if (_cands.ContainsKey(kv.Key)) continue;
                var go = kv.Key.gameObject;
                if (go == null) continue;
                // Never filter a GameObject that also holds the AudioListener (that would duck ALL game audio).
                if (go.GetComponent<AudioListener>() != null) continue;
                var f = go.GetComponent<MusicDuckFilter>();
                if (f == null) f = go.AddComponent<MusicDuckFilter>();
                if (!_filters.Contains(f)) _filters.Add(f);
                kv.Value.Filter = f;
                _cands[kv.Key] = kv.Value;
                added++;
            }
            if (added > 0) Evaluate(true);
        }

        /// <summary>Decides per source (by its CURRENT clip) whether it is music, and sets each filter's Active flag.</summary>
        private void Evaluate(bool summary)
        {
            RefreshExcludes();
            var ducked = new List<string>();
            var skipped = new List<string>();
            var activeByFilter = new Dictionary<MusicDuckFilter, bool>();

            foreach (var c in _cands.Values)
            {
                if (c.Src == null || c.Filter == null) continue;
                string clip = c.Src.clip != null ? c.Src.clip.name : null;
                bool changed = c.Decision == null || c.DecidedClip != clip;
                if (changed)
                {
                    bool duck = Decide(c, clip, out string reason);
                    string desc = $"'{c.Src.gameObject.name}' via {c.Via}" + (clip != null ? $" (clip '{clip}')" : " (no clip)");
                    if (summary || c.Decision == null)
                        (duck ? ducked : skipped).Add(duck ? desc : desc + ": " + reason);
                    else if (c.Decision != duck)
                        Plugin.Log.LogInfo($"Game music ducking: {(duck ? "now ducking" : "no longer ducking")} {desc}" + (duck ? "" : ": " + reason));
                    c.Decision = duck;
                    c.DecidedClip = clip;
                    c.Reason = reason;
                }
                activeByFilter.TryGetValue(c.Filter, out bool a);
                activeByFilter[c.Filter] = a || c.Decision == true;
            }
            foreach (var kv in activeByFilter) kv.Key.Active = kv.Value;

            if (ducked.Count + skipped.Count > 0)
                Plugin.Log.LogInfo($"Game music ducking in '{SceneManager.GetActiveScene().name}': " +
                                   $"ducked {ducked.Count}" + (ducked.Count > 0 ? " [" + string.Join("; ", ducked) + "]" : "") +
                                   $", skipped {skipped.Count}" + (skipped.Count > 0 ? " [" + string.Join("; ", skipped) + "]" : "") + ".");
        }

        private bool Decide(Candidate c, string clip, out string reason)
        {
            string obj = c.Src.gameObject.name ?? "";
            foreach (var ex in _exclude)
            {
                if (obj.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) { reason = $"name matches ExcludeFromDucking '{ex}'"; return false; }
                if (clip != null && clip.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) { reason = $"clip matches ExcludeFromDucking '{ex}'"; return false; }
            }
            if (c.MusicClips != null)
            {
                // AmbienceAudio's code only ever assigns music stingers to mainMusic/stingerSource; anything else
                // there is the inspector's placeholder clip (e.g. 'Au_Cartoon_Wobble1').
                if (clip == null) { reason = "known music source (no clip yet)"; return true; }
                bool music = c.MusicClips.Any(m => m != null && m.name == clip);
                reason = music ? "AmbienceAudio music stinger" : "clip is not one of AmbienceAudio's music clips (placeholder/SFX)";
                return music;
            }
            // PEAK names its sound-effect / ambience clips "Au_..." (e.g. Au_Escalator, Au_Ambience_Beach,
            // Au_Cartoon_Wobble1); music clips are named e.g. "Music Airport", "Music_FearMyres", "nadir-music".
            if (clip != null && clip.StartsWith("Au_", StringComparison.OrdinalIgnoreCase)) { reason = "sound-effect clip (Au_ prefix)"; return false; }
            if (c.KnownField || c.MusicGroup) { reason = c.KnownField ? "known music source" : "routed to the music mixer group"; return true; }
            // Tag-only: the "Music" tag is also on ambience objects, so require the name or clip to say "music".
            bool looksLikeMusic = obj.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  (clip != null && clip.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0);
            reason = looksLikeMusic ? "tagged Music and named like music" : "tagged Music but name/clip don't look like music";
            return looksLikeMusic;
        }

        /// <summary>Every AudioClip field of AmbienceAudio (all are music stingers; names verified in the decompiled code).</summary>
        private static HashSet<AudioClip> AmbienceMusicClips(AmbienceAudio a)
        {
            var set = new HashSet<AudioClip>();
            void Add(AudioClip c) { if (c != null) set.Add(c); }
            void AddAll(AudioClip[] arr) { if (arr != null) foreach (var c in arr) Add(c); }
            AddAll(a.startStinger); AddAll(a.sunRiseStinger); AddAll(a.sunSetStinger); AddAll(a.nightStinger);
            Add(a.climbStingerBeach); Add(a.climbStingerTropics); Add(a.climbStingerRoots); Add(a.climbStingerAlpine);
            Add(a.climbStingerMesa); Add(a.climbStingerCaldera); Add(a.climbStingerGloom); Add(a.climbStingerKiln);
            Add(a.climbStingerCitadel); Add(a.climbStingerNadir); Add(a.climbStingerPeak); Add(a.tombClimb);
            Add(a.tropicsSunrise); Add(a.tropicsSunset); Add(a.rootsSunrise); Add(a.rootsSunset);
            Add(a.alpineSunrise); Add(a.alpineSunset); Add(a.desertSunrise); Add(a.desertSunset);
            return set;
        }

        private void RefreshExcludes()
        {
            string raw = Plugin.ExcludeFromDucking.Value ?? "";
            if (raw == _excludeRaw) return;
            _excludeRaw = raw;
            _exclude = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            foreach (var c in _cands.Values) c.Decision = null; // re-decide everything with the new list
        }

        private static HashSet<AudioMixerGroup> FindMusicGroups()
        {
            var set = new HashSet<AudioMixerGroup>();
            try
            {
                var master = SingletonAsset<StaticReferences>.Instance != null ? SingletonAsset<StaticReferences>.Instance.masterMixerGroup : null;
                var mixer = master != null ? master.audioMixer : null;
                if (mixer == null) return set;
                foreach (var g in mixer.FindMatchingGroups(string.Empty) ?? new AudioMixerGroup[0])
                    if (g != null && (g.name == "Music" || g.name == "Music_Setting")) set.Add(g);
            }
            catch (Exception e) { Plugin.Verbose("Music group lookup failed: " + e.Message); }
            return set;
        }
    }

    /// <summary>DSP gain stage on a game-music GameObject; ramps smoothly across each audio buffer.</summary>
    public sealed class MusicDuckFilter : MonoBehaviour
    {
        private float _current = 1f;

        /// <summary>Set by the ducker from the source's current clip; false = pass through untouched.</summary>
        internal volatile bool Active;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            float target = Active ? GameMusicDucker.SharedGain : 1f;
            float start = _current;
            if (start >= 0.9999f && target >= 0.9999f) { _current = 1f; return; }
            if (channels <= 0) return;
            int frames = data.Length / channels;
            if (frames == 0) return;
            float step = (target - start) / frames;
            float g = start;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                g += step;
                for (int c = 0; c < channels; c++, i++) data[i] *= g;
            }
            _current = target;
        }
    }
}
