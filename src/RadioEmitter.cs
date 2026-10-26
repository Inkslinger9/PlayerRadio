using UnityEngine;
using Zorro.Core;

namespace PlayerRadio
{
    /// <summary>
    /// One player's radio AudioSource (one per radio owner). It is NOT parented to the character (characters get
    /// destroyed on scene changes / death warps); instead it is moved to the owner's torso every LateUpdate.
    /// </summary>
    public sealed class RadioEmitter : MonoBehaviour
    {
        public AudioSource Source { get; private set; }
        public string Placement { get; private set; } = "";
        private float _appliedMin = -1, _appliedMax = -1;
        private float _blend = 1f;
        private float _mixerRetryAt;
        private bool _mixerAssigned;
        private AnimationCurve _curve;
        public bool Spatial => _blend > 0.5f;

        private void Awake()
        {
            Source = gameObject.AddComponent<AudioSource>();
            Source.playOnAwake = false;
            Source.loop = false;
            Source.spatialBlend = 1f;
            Source.dopplerLevel = 0f;
            Source.spread = 60f;
            Source.priority = 40;
            Source.rolloffMode = AudioRolloffMode.Custom;
            ApplyDistances();
        }

        private void ApplyDistances()
        {
            float max = Mathf.Max(5f, Plugin.MaxDistance.Value);
            float min = Mathf.Clamp(Plugin.MinDistance.Value, 0.5f, max * 0.9f);
            if (Mathf.Approximately(min, _appliedMin) && Mathf.Approximately(max, _appliedMax)) return;
            _appliedMin = min;
            _appliedMax = max;
            Source.minDistance = min;
            Source.maxDistance = max;
            // Custom rolloff is evaluated over distance / maxDistance: flat inside min, smooth fade to 0 at max.
            float m = min / max;
            var curve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(m, 1f),
                new Keyframe(m + (1f - m) * 0.35f, 0.45f),
                new Keyframe(m + (1f - m) * 0.7f, 0.15f),
                new Keyframe(1f, 0f));
            Source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            _curve = curve;
        }

        private void TryAssignMixer()
        {
            if (_mixerAssigned || Time.unscaledTime < _mixerRetryAt) return;
            _mixerRetryAt = Time.unscaledTime + 3f;
            var g = MixerRouting.Resolve();
            if (g != null)
            {
                Source.outputAudioMixerGroup = g;
                _mixerAssigned = true;
            }
        }

        public void ForceMixerReassign() { _mixerAssigned = false; _mixerRetryAt = 0; }

        /// <summary>
        /// How loud this radio is at <paramref name="listener"/>, 0..1, from the same rolloff curve Unity uses
        /// (ignores volume settings). Non-spatial = 1.
        /// </summary>
        public float LoudnessAt(Vector3 listener)
        {
            if (!Spatial || _curve == null) return 1f;
            float max = Mathf.Max(0.01f, Source.maxDistance);
            float d = Vector3.Distance(listener, transform.position);
            if (d >= max) return 0f;
            return Mathf.Clamp01(_curve.Evaluate(d / max));
        }

        /// <summary>Called by the radio in LateUpdate with the resolved world position (or null = non-spatial) and its local volume.</summary>
        public void Place(Vector3? pos, string placement, float volume)
        {
            ApplyDistances();
            TryAssignMixer();
            Placement = placement;
            if (pos.HasValue) transform.position = pos.Value;
            float targetBlend = pos.HasValue ? 1f : 0f;

            bool cinematic = false;
            if (Plugin.DuckDuringCinematics.Value)
            {
                var ph = Singleton<PeakHandler>.Instance;
                cinematic = ph != null && ph.isPlayingCinematic;
            }
            _blend = Mathf.MoveTowards(_blend, targetBlend, Time.unscaledDeltaTime * 2f);
            Source.spatialBlend = _blend;
            // Non-spatial fallback (your own radio while you have no body, e.g. main menu) plays a bit quieter.
            float placementGain = pos.HasValue ? 1f : 0.6f;
            Source.volume = Mathf.Clamp01(volume) * placementGain * (cinematic ? 0.2f : 1f);
        }
    }
}
