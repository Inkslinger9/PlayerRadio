using System;
using System.Linq;
using UnityEngine.Audio;
using Zorro.Core;

namespace PlayerRadio
{
    /// <summary>Finds the game's mixer group for the radio (Music -> SFX -> Master) so the game's volume sliders apply.</summary>
    public static class MixerRouting
    {
        private static bool _logged;
        public static string ChosenDescription { get; private set; } = "(not resolved yet)";

        // Exposed parameter names verified in Peak.Settings.AudioMixerUnifier / Zorro.Settings.VolumeSetting.
        private static readonly string[] KnownParams =
            { "MasterVolume", "MusicVolume", "SFXVolume", "VoiceVolume", "EffectsStrength", "LoadingFade" };

        public static AudioMixerGroup Resolve()
        {
            try
            {
                var refs = SingletonAsset<StaticReferences>.Instance;
                var master = refs != null ? refs.masterMixerGroup : null;
                var mixer = master != null ? master.audioMixer : null;
                if (mixer == null)
                {
                    var sfxOnly = SFX_Player.instance != null ? SFX_Player.instance.defaultMixerGroup : null;
                    if (sfxOnly != null) { Chosen(sfxOnly, "SFX_Player.defaultMixerGroup (master mixer not found)"); return sfxOnly; }
                    return null;
                }

                var all = mixer.FindMatchingGroups(string.Empty) ?? new AudioMixerGroup[0];
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogInfo($"Audio mixer '{mixer.name}' (master group '{master.name}') has {all.Length} group(s): " +
                                       string.Join(", ", all.Select(g => g.name)));
                    foreach (var p in KnownParams)
                    {
                        if (mixer.GetFloat(p, out float v)) Plugin.Log.LogInfo($"  exposed param {p} = {v:0.0} dB");
                        else Plugin.Log.LogInfo($"  exposed param {p}: not found");
                    }
                }

                string want = (Plugin.MixerGroupName.Value ?? "").Trim();
                if (want.Length > 0)
                {
                    var exact = all.FirstOrDefault(g => string.Equals(g.name, want, StringComparison.OrdinalIgnoreCase))
                                ?? all.FirstOrDefault(g => g.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (exact != null) { Chosen(exact, $"mixer group '{exact.name}' (matched config '{want}')"); return exact; }
                }
                var sfx = SFX_Player.instance != null ? SFX_Player.instance.defaultMixerGroup : null;
                if (sfx != null) { Chosen(sfx, $"SFX_Player.defaultMixerGroup '{sfx.name}' (fallback: no group named '{want}')"); return sfx; }
                Chosen(master, $"master group '{master.name}' (fallback)");
                return master;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Mixer lookup failed (will retry): {e.Message}");
                return null;
            }
        }

        private static void Chosen(AudioMixerGroup g, string desc)
        {
            if (ChosenDescription != desc) Plugin.Log.LogInfo("Radio audio routed to " + desc);
            ChosenDescription = desc;
        }
    }
}
