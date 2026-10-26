using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayerRadio
{
    /// <summary>
    /// Writes "HOW TO ADD MUSIC.txt" into the Music folder. The first line carries "[guide vN #hash]" where the hash
    /// covers the rest of the file: if the user edited the file the hash no longer matches and we never touch it.
    /// An unmodified older copy (or one listing an old hotkey) is replaced. Deleting the file regenerates it.
    /// </summary>
    public static class MusicFolderGuide
    {
        public const string FileName = "HOW TO ADD MUSIC.txt";
        private const int GuideVersion = 3;
        private const string LegacyFileName = "_PUT_MP3_WAV_OGG_FILES_HERE.txt"; // written by 0.1.0-0.1.1
        private const string LegacyText =
            "Player Radio: drop .mp3, .wav or .ogg files in this folder. They are added to the shared lobby playlist.\r\n" +
            "Players who don't have a track get it streamed to them automatically (if EnableStreaming is on).\r\n";
        private static readonly Regex Header = new Regex(@"\[guide v(\d+) #([0-9a-f]{8})\]\s*$");

        public static void Ensure(string musicDir)
        {
            try
            {
                RemoveLegacy(musicDir);
                string path = Path.Combine(musicDir, FileName);
                string body = BuildBody();
                string wanted = $"PLAYER RADIO - HOW TO ADD MUSIC   [guide v{GuideVersion} #{Hash(body)}]\r\n" + body;

                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path);
                    if (existing == wanted) return;
                    int nl = existing.IndexOf('\n');
                    string first = (nl >= 0 ? existing.Substring(0, nl) : existing).TrimEnd('\r');
                    string rest = nl >= 0 ? existing.Substring(nl + 1) : "";
                    var m = Header.Match(first);
                    bool ours = m.Success && m.Groups[2].Value == Hash(rest);
                    if (!ours)
                    {
                        Plugin.Verbose($"'{FileName}' was edited by the user; leaving it alone.");
                        return;
                    }
                    File.WriteAllText(path, wanted);
                    Plugin.Log.LogInfo($"Updated '{FileName}' (guide v{m.Groups[1].Value} -> v{GuideVersion}).");
                    return;
                }
                File.WriteAllText(path, wanted);
                Plugin.Log.LogInfo($"Wrote '{path}'.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not write '{FileName}': {e.Message}");
            }
        }

        private static void RemoveLegacy(string musicDir)
        {
            string p = Path.Combine(musicDir, LegacyFileName);
            if (!File.Exists(p)) return;
            if (File.ReadAllText(p).Replace("\r\n", "\n") != LegacyText.Replace("\r\n", "\n")) return; // user edited it: keep
            File.Delete(p);
            Plugin.Log.LogInfo($"Removed old '{LegacyFileName}' (replaced by '{FileName}').");
        }

        private static string Key(BepInEx.Configuration.ConfigEntry<string> e)
        {
            var p = e?.Value ?? "";
            int i = p.LastIndexOf('/');
            var k = (i >= 0 ? p.Substring(i + 1) : p).Trim();
            return k.Length == 0 ? "(unbound)" : k.ToUpperInvariant();
        }

        private static string BuildBody()
        {
            var sb = new StringBuilder();
            void L(string s = "") => sb.Append(s).Append("\r\n");
            L();
            L("1. Put your songs in THIS folder. Supported formats: .mp3, .wav and .ogg.");
            L("   Subfolders are fine (for example Music\\Chill\\song.mp3).");
            L("   Other files (like this .txt, cover images or playlists) are ignored.");
            L();
            L("2. New, changed or removed files are picked up automatically about every 10 seconds.");
            L("   No restart needed.");
            L();
            L("3. These are YOUR songs: they play from YOUR radio, which sits on your character.");
            L("   Every player has their own radio with their own songs. Everyone near you hears yours,");
            L("   you hear theirs (in 3D, so it gets quieter with distance). Several radios can play at once.");
            L("   If another player doesn't have your song, it is sent to them over the game's connection");
            L("   (rate-limited, checked, then cached). Turn that off with EnableStreaming = false in the config.");
            L();
            L("4. Other players need the Player Radio mod (same 0.2.x version) to hear your radio.");
            L();
            L($"5. In game, press {Key(Plugin.KeyOpenMusicFolder)} to open this folder.");
            L($"   Your radio: {Key(Plugin.KeyPlayPause)} play/pause, {Key(Plugin.KeySkip)} next, {Key(Plugin.KeyPrevious)} previous,");
            L($"   {Key(Plugin.KeyVolumeDown)}/{Key(Plugin.KeyVolumeUp)} its volume (just for you).");
            L($"   {Key(Plugin.KeyMuteOthers)} mutes/unmutes everyone else's radio, just for you.");
            L("   Change keys in BepInEx\\config\\com.inkslinger.playerradio.cfg.");
            L();
            L("Mod manager users (r2modman / Gale): this folder is inside your PROFILE folder, at");
            L("   <your profile>\\BepInEx\\config\\PlayerRadio\\Music");
            L("Use the mod manager's option to open/browse the profile folder to get there.");
            L();
            L("(Delete this file to get a fresh copy. If you edit it, Player Radio will leave it alone.)");
            return sb.ToString();
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n")));
                return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
