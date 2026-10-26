using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlayerRadio
{
    /// <summary>
    /// One player's radio as seen by this client: the owner's published state, its own AudioSource (emitter) and
    /// decoded clip, and where it is in the world. Your own radio is one of these too (IsLocal), so playback,
    /// placement and death handling are the same code for everyone.
    /// </summary>
    public sealed class Radio
    {
        public readonly int Actor;
        public readonly bool IsLocal;
        public RadioState State = new RadioState();
        public readonly RadioEmitter Emitter;

        /// <summary>Where the owner died (radio stays there, playing, until they're revived or leave).</summary>
        public Vector3? DeathPos;
        public Vector3? LastKnownPos;
        public bool HasPosition { get; private set; }

        private AudioClip _clip;
        private string _clipHex;
        private string _loadingHex;
        private readonly Dictionary<string, float> _loadFailedAt = new Dictionary<string, float>();
        private float _nextDriftCheck;
        private float _lastAudibleAt = -100f;

        public Radio(int actor, bool isLocal)
        {
            Actor = actor;
            IsLocal = isLocal;
            var go = new GameObject(isLocal ? "PlayerRadio.MyRadio" : "PlayerRadio.Radio." + actor);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Emitter = go.AddComponent<RadioEmitter>();
        }

        public AudioSource Source => Emitter.Source;
        public string ClipHex => _clipHex;
        public bool IsLoading => _loadingHex != null;
        public bool On => State.Playing && State.Current != null;

        /// <summary>Actually producing sound right now (or between tracks / on a short hiccup, within 3 s).</summary>
        public bool Sounding(float holdSeconds = 3f)
        {
            if (Source != null && Source.isPlaying && Source.volume > 0.001f) _lastAudibleAt = Time.unscaledTime;
            return On && Time.unscaledTime - _lastAudibleAt < holdSeconds;
        }

        public float Duration
        {
            get
            {
                var m = State.Current;
                if (m == null) return 0f;
                if (_clip != null && _clipHex == m.Hex) return _clip.length;
                return m.Duration;
            }
        }

        public void Dispose()
        {
            try
            {
                if (Source != null) { Source.Stop(); Source.clip = null; }
                if (_clip != null) UnityEngine.Object.Destroy(_clip);
                if (Emitter != null) UnityEngine.Object.Destroy(Emitter.gameObject);
            }
            catch { /* shutting down */ }
            _clip = null;
            _clipHex = null;
        }

        /// <summary>What the HUD should say about the current track if it can't play yet (null = playing fine).</summary>
        public string Blocker(RadioSession s)
        {
            string want = State.CurrentHex;
            if (want == null || _clipHex == want) return null;
            if (_loadingHex == want) return "Loading...";
            if (s.Library.Has(want)) return _loadFailedAt.ContainsKey(want) ? "Can't decode this file" : "Loading...";
            var st = s.Transfer.StatusFor(want);
            if (st != null) return st;
            if (!Plugin.EnableStreaming.Value) return "Missing track (streaming disabled)";
            if (s.Transfer.RecentlyFailed(want)) return "Download failed, retrying soon";
            return "Waiting to download...";
        }

        public void UpdatePlayback(RadioSession s)
        {
            var src = Source;
            var st = State;
            string want = st.CurrentHex;
            float t = Time.unscaledTime;

            if (want == null)
            {
                if (src.isPlaying) src.Stop();
                return;
            }

            if (_clipHex != want)
            {
                if (src.isPlaying) src.Stop();
                if (_loadingHex != null) return;
                if (_loadFailedAt.TryGetValue(want, out var ft) && t - ft < 30f) return;
                if (s.Library.TryGetLocalPath(want, out var path)) s.StartCoroutine(LoadClip(s, want, path));
                return;
            }

            float target = st.PositionAt(RadioSession.Now);
            float len = _clip.length;
            if (st.Playing)
            {
                if (target >= len - 0.05f)
                {
                    if (src.isPlaying) src.Stop(); // the owner advances to the next track
                    return;
                }
                if (!src.isPlaying)
                {
                    if (target > len - 0.5f) return;
                    if (src.clip != _clip) src.clip = _clip;
                    src.time = Mathf.Clamp(target, 0f, len - 0.01f);
                    src.Play();
                    _nextDriftCheck = t + 1f;
                    Plugin.Verbose($"[{s.PlayerName(Actor)}] play '{_clip.name}' from {target:0.00}s");
                }
                else if (t >= _nextDriftCheck)
                {
                    _nextDriftCheck = t + 1f;
                    float drift = src.time - target;
                    if (Mathf.Abs(drift) > 0.25f)
                    {
                        src.time = Mathf.Clamp(target, 0f, len - 0.01f);
                        Plugin.Verbose($"[{s.PlayerName(Actor)}] drift {drift:+0.00;-0.00}s corrected");
                    }
                }
            }
            else if (src.isPlaying)
            {
                src.Pause();
            }
        }

        private IEnumerator LoadClip(RadioSession s, string hex, string path)
        {
            _loadingHex = hex;
            AudioClip clip = null;
            string err = null;
            yield return AudioLoader.Load(path, (c, e) => { clip = c; err = e; });
            _loadingHex = null;
            if (Emitter == null) { if (clip != null) UnityEngine.Object.Destroy(clip); yield break; } // radio removed meanwhile
            if (clip == null)
            {
                _loadFailedAt[hex] = Time.unscaledTime;
                Plugin.Log.LogError($"Could not load '{Path.GetFileName(path)}': {err}");
                if (IsLocal) s.ShowToast("Radio: can't decode this track");
                yield break;
            }
            var src = Source;
            if (_clip != null)
            {
                src.Stop();
                src.clip = null;
                UnityEngine.Object.Destroy(_clip);
            }
            _clip = clip;
            _clipHex = hex;
            src.clip = clip;
            s.Library.SetDuration(hex, clip.length);
            Plugin.Log.LogInfo($"[{s.PlayerName(Actor)}'s radio] loaded '{Path.GetFileName(path)}' ({Util.Time(clip.length)}, {clip.channels}ch {clip.frequency}Hz).");
        }

        /// <summary>Follow the owner's torso; stay at the death spot while they're dead; vanish (silently) if there is nowhere to put it.</summary>
        public void Place(RadioSession s)
        {
            Vector3? pos = null;
            string where;
            var c = s.FindCharacter(Actor, IsLocal);
            if (c != null && RadioSession.CharacterAlive(c) && !RadioSession.InDeathVoid(RadioSession.TorsoPos(c)))
            {
                // Alive (also passed out / ragdolled / carried): the torso bone moves with the body.
                pos = RadioSession.TorsoPos(c);
                LastKnownPos = pos;
                if (DeathPos.HasValue)
                {
                    Plugin.Log.LogInfo($"{s.PlayerName(Actor)} is back up: radio reattached.");
                    DeathPos = null;
                }
                where = IsLocal ? "on you" : "on " + s.PlayerName(Actor);
            }
            else if (DeathPos.HasValue || (c != null && LastKnownPos.HasValue))
            {
                if (!DeathPos.HasValue) DeathPos = LastKnownPos;
                pos = DeathPos;
                where = IsLocal ? "where you fell" : $"where {s.PlayerName(Actor)} fell";
            }
            else
            {
                where = IsLocal ? "no body yet (plays for you only)" : "not nearby";
            }
            HasPosition = pos.HasValue;

            float vol;
            if (IsLocal) vol = Plugin.MyRadioVolume.Value;
            else vol = pos.HasValue && !s.OthersMuted ? Plugin.OtherRadiosVolume.Value : 0f; // remote radio with no body: silent
            Emitter.Place(pos, where, vol);
        }

        public void OnSceneChanged()
        {
            DeathPos = null;
            LastKnownPos = null;
            Emitter.ForceMixerReassign();
        }
    }
}
