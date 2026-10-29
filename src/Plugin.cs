using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PlayerRadio
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.inkslinger.playerradio";
        public const string Name = "Player Radio";
        public const string Version = "0.2.3"; // the network protocol is the major.minor part ("0.2"), see RadioSession.Protocol

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Hotkeys (Unity Input System control paths)
        internal static ConfigEntry<string> KeyPlayPause, KeySkip, KeyPrevious, KeyMuteOthers, KeyVolumeDown, KeyVolumeUp, KeyOpenMusicFolder;
        // Audio
        internal static ConfigEntry<float> MyRadioVolume, OtherRadiosVolume, VolumeStep, MaxDistance, MinDistance;
        internal static ConfigEntry<string> MixerGroupName, ExcludeFromDucking;
        internal static ConfigEntry<bool> DuckDuringCinematics, DuckGameMusic, DuckOnlyWhenInRange;
        internal static ConfigEntry<float> GameMusicDuckVolume;
        // Network / streaming
        internal static ConfigEntry<bool> EnableStreaming;
        internal static ConfigEntry<int> StreamRateKBps, DownloadRateKBps, MaxStreamFileMB, CacheSizeMB;
        // Library
        internal static ConfigEntry<string> MusicFolderOverride;
        // UI / debug
        internal static ConfigEntry<bool> ShowHud, ShowKeyHints, ShowNearbyRadios, MarkSourcePlayerName, VerboseLogging;
        internal static ConfigEntry<bool> EnableHotkeys, PauseMenuPage, DumpPauseMenu;
        internal static ConfigEntry<string> PauseMenuButtonPosition;

        private Harmony _harmony;
        private GameObject _host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            BindConfig();

            Log.LogInfo($"{Name} {Version} by Inkslinger loading (Unity {Application.unityVersion}).");

            _harmony = new Harmony(Guid);
            ApplyPatch(typeof(Patch_CharacterAwake));
            ApplyPatch(typeof(Patch_CharacterDie));
            ApplyPatch(typeof(Patch_CharacterZombify));
            ApplyPatch(typeof(Patch_CharacterInteractibleName));
            ApplyPatch(typeof(Patch_PauseMenuMainPageStart));

            _host = new GameObject("PlayerRadio");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<RadioSession>();
            _host.AddComponent<RadioHud>();
            _host.AddComponent<GameMusicDucker>();

            Log.LogInfo($"{Name} loaded. Music folder: {TrackLibrary.ResolveMusicDir()}");
        }

        private void OnDestroy()
        {
            // Plugin unload (e.g. ScriptEngine reload / game exit): never leave the game music ducked.
            GameMusicDucker.SharedGain = 1f;
            if (GameMusicDucker.Instance != null) GameMusicDucker.Instance.RestoreAll("plugin unload");
        }

        private void ApplyPatch(Type patchClass)
        {
            try
            {
                _harmony.CreateClassProcessor(patchClass).Patch();
                Log.LogInfo($"Harmony patch applied: {patchClass.Name}");
            }
            catch (Exception e)
            {
                // Fail soft: the radio still works without any individual patch.
                Log.LogWarning($"Harmony patch {patchClass.Name} failed (game update?): {e.Message}");
            }
        }

        private void BindConfig()
        {
            const string keyDesc = "Unity Input System control path, e.g. <Keyboard>/f5, <Keyboard>/numpad5, <Mouse>/forwardButton.";
            EnableHotkeys = Config.Bind("Hotkeys", "EnableHotkeys", true, "Turn the radio hotkeys below on or off. They also work while the pause menu is open. Everything they do is also on the pause menu's RADIO page.");
            KeyPlayPause = Config.Bind("Hotkeys", "PlayPause", "<Keyboard>/f5", "Play / pause YOUR radio (plays your Music folder from your character; everyone nearby hears it). " + keyDesc);
            KeySkip = Config.Bind("Hotkeys", "Skip", "<Keyboard>/f7", "Your radio: next track. " + keyDesc);
            KeyPrevious = Config.Bind("Hotkeys", "Previous", "<Keyboard>/f6", "Your radio: previous track (restarts the current track if more than 3 s in). " + keyDesc);
            KeyMuteOthers = Config.Bind("Hotkeys", "ToggleOtherRadios", "<Keyboard>/f8", "Mute / unmute OTHER players' radios for you only (not saved; resets on restart). " + keyDesc);
            KeyVolumeDown = Config.Bind("Hotkeys", "VolumeDown", "<Keyboard>/f9", "Lower the volume of YOUR radio as you hear it (local only). " + keyDesc);
            KeyVolumeUp = Config.Bind("Hotkeys", "VolumeUp", "<Keyboard>/f10", "Raise the volume of YOUR radio as you hear it (local only). " + keyDesc);
            KeyOpenMusicFolder = Config.Bind("Hotkeys", "OpenMusicFolder", "<Keyboard>/f4", "Open your Music folder in File Explorer (works in menus too). " + keyDesc);

            MyRadioVolume = Config.Bind("Audio", "MyRadioVolume", 0.7f, new ConfigDescription("How loud YOUR radio is for you (local only; F9/F10). Also scaled by the game's Master/Music sliders.", new AcceptableValueRange<float>(0f, 1f)));
            OtherRadiosVolume = Config.Bind("Audio", "OtherRadiosVolume", 0.7f, new ConfigDescription("How loud OTHER players' radios are for you (local only).", new AcceptableValueRange<float>(0f, 1f)));
            VolumeStep = Config.Bind("Audio", "VolumeStep", 0.1f, new ConfigDescription("Volume change per key press.", new AcceptableValueRange<float>(0.01f, 0.5f)));
            MaxDistance = Config.Bind("Audio", "MaxDistance", 60f, new ConfigDescription("Distance (m) at which a radio becomes inaudible.", new AcceptableValueRange<float>(5f, 500f)));
            MinDistance = Config.Bind("Audio", "MinDistance", 3f, new ConfigDescription("Distance (m) within which a radio plays at full volume.", new AcceptableValueRange<float>(0.5f, 50f)));
            MixerGroupName = Config.Bind("Audio", "MixerGroup", "Music", "Name of the game's audio mixer group to route the radio through (so the game's volume sliders apply). Falls back to the SFX group, then Master.");
            DuckGameMusic = Config.Bind("Audio", "DuckGameMusic", true, "Fade the game's own background music out (~1.5 s) while your radio plays or another player's radio is audible near you (deeper the closer it is), and back in (~2 s) afterwards. Only affects your client.");
            GameMusicDuckVolume = Config.Bind("Audio", "GameMusicDuckVolume", 0f, new ConfigDescription("Game music level while ducked (0 = silent, 1 = no change).", new AcceptableValueRange<float>(0f, 1f)));
            DuckOnlyWhenInRange = Config.Bind("Audio", "DuckOnlyWhenInRange", false, "Stricter ducking for OTHER players' radios: only duck (fully) when you are clearly within earshot. Off = duck in proportion to how loud the nearest radio is. Your own radio always ducks.");
            ExcludeFromDucking = Config.Bind("Audio", "ExcludeFromDucking", "Ambience", "Comma-separated name parts: game audio objects or clips whose name contains one of these are never ducked (case-insensitive).");
            DuckDuringCinematics = Config.Bind("Audio", "DuckDuringCinematics", true, "Lower radios while the game plays a cinematic.");

            EnableStreaming = Config.Bind("Network", "EnableStreaming", true, "Send/receive track files to/from players who are missing them (uses the game's Photon connection; kept slow on purpose).");
            StreamRateKBps = Config.Bind("Network", "StreamRateKBps", 64, new ConfigDescription("Upload rate limit for sending tracks to others, in KB/s (total across all recipients; each recipient also gets at most its requested rate).", new AcceptableValueRange<int>(8, 256)));
            MaxStreamFileMB = Config.Bind("Network", "MaxStreamFileMB", 25, new ConfigDescription("Largest track file that will be sent or accepted, in MB.", new AcceptableValueRange<int>(1, 100)));
            CacheSizeMB = Config.Bind("Network", "CacheSizeMB", 500, new ConfigDescription("Maximum size of the downloaded-track cache, in MB (oldest files are deleted first).", new AcceptableValueRange<int>(50, 10000)));
            DownloadRateKBps = Config.Bind("Network", "DownloadRateKBps", 128, new ConfigDescription("Total download rate you ask senders for, in KB/s (shared between up to 2 simultaneous downloads).", new AcceptableValueRange<int>(8, 512)));

            MusicFolderOverride = Config.Bind("Library", "MusicFolder", "", "Optional absolute path to your music folder. Empty = BepInEx/config/PlayerRadio/Music.");

            ShowHud = Config.Bind("UI", "ShowHud", true, "Show the small radio overlay (your radio, transfers, nearby radios).");
            ShowKeyHints = Config.Bind("UI", "ShowKeyHints", true, "Show the hotkey hints line in the top-right overlay (hidden anyway when EnableHotkeys is off).");
            PauseMenuPage = Config.Bind("UI", "PauseMenuRadioPage", true, "Add a RADIO button to the pause menu that opens the Player Radio page (now playing, controls, volumes, song list).");
            PauseMenuButtonPosition = Config.Bind("UI", "PauseMenuButtonPosition", "", "Leave empty to place the pause-menu RADIO button automatically (under the existing buttons). Or set x,y (anchoredPosition in the button column, e.g. 0,-300) if it overlaps something; see the pause-menu dump in the log.");
            ShowNearbyRadios = Config.Bind("UI", "ShowNearbyRadios", true, "List other players' radios playing near you (owner, title, distance) in the overlay.");
            MarkSourcePlayerName = Config.Bind("UI", "MarkRadioCarrierName", true, "Append [Radio] to the name shown when looking at a player whose radio is playing.");
            VerboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Log every state change, transfer step and drift correction.");
            DumpPauseMenu = Config.Bind("Debug", "DumpPauseMenuHierarchy", true, "Log the pause menu's UI hierarchy (names, positions, components) once per game launch, the first time the pause menu opens. For fixing the RADIO button layout; safe to turn off.");
            ConfigVersion = Config.Bind("Internal", "ConfigVersion", 1, "Used by Player Radio to upgrade old settings. Don't change.");
            MigrateConfig();
        }

        internal static ConfigEntry<int> ConfigVersion;
        private const int CurrentConfigVersion = 2;

        /// <summary>
        /// BepInEx keeps values from an existing cfg, so changed defaults never reach old installs on their own.
        /// v1 -> v2 (0.2.1): play/pause moved F6 -> F5 and previous F5 -> F6. Swapped only if both are still the
        /// old defaults (a user who rebound them keeps their keys). Also drops settings removed in 0.2.0.
        /// </summary>
        private void MigrateConfig()
        {
            int from = ConfigVersion.Value;
            if (from >= CurrentConfigVersion) return;
            try
            {
                if (from < 2)
                {
                    bool oldPlay = string.Equals(KeyPlayPause.Value?.Trim(), "<Keyboard>/f6", StringComparison.OrdinalIgnoreCase);
                    bool oldPrev = string.Equals(KeyPrevious.Value?.Trim(), "<Keyboard>/f5", StringComparison.OrdinalIgnoreCase);
                    if (oldPlay && oldPrev)
                    {
                        KeyPlayPause.Value = "<Keyboard>/f5";
                        KeyPrevious.Value = "<Keyboard>/f6";
                        Log.LogInfo("Config upgrade: play/pause is now F5 and previous is now F6 (swapped from the old defaults).");
                    }
                    else Log.LogInfo($"Config upgrade: keeping your play/pause ({KeyPlayPause.Value}) and previous ({KeyPrevious.Value}) keys.");

                    // Settings removed in 0.2.0 linger in the cfg as orphans; drop them.
                    var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(Config, null) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                    if (orphans != null)
                    {
                        int n = 0;
                        foreach (var def in new[] { new ConfigDefinition("Audio", "Volume"), new ConfigDefinition("Hotkeys", "TakeOver"), new ConfigDefinition("Network", "SourceHandoff") })
                            if (orphans.Remove(def)) n++;
                        if (n > 0) Log.LogInfo($"Config upgrade: removed {n} setting(s) that no longer exist.");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"Config upgrade failed: {e.Message}"); }
            ConfigVersion.Value = CurrentConfigVersion;
            Config.Save();
        }

        internal static void Verbose(string msg)
        {
            if (VerboseLogging != null && VerboseLogging.Value) Log.LogInfo("[v] " + msg);
        }
    }
}
