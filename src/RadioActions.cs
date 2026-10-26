using System;
using System.IO;
using UnityEngine;

namespace PlayerRadio
{
    /// <summary>User-facing actions shared by hotkeys and (later) HUD buttons.</summary>
    public static class RadioActions
    {
        private static float _lastOpen = -10f;

        /// <summary>The Music folder actually in use (the library's, else the configured/default path), as a full path.</summary>
        public static string MusicFolder
        {
            get
            {
                var lib = RadioSession.Instance != null ? RadioSession.Instance.Library : null;
                var dir = lib != null ? lib.MusicDir : TrackLibrary.ResolveMusicDir();
                return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        /// <summary>Opens the Music folder in the OS file browser (creates it and the how-to file first). Returns true on success.</summary>
        public static bool OpenMusicFolder()
        {
            if (Time.unscaledTime - _lastOpen < 1f) return false; // ignore key repeat / double clicks
            _lastOpen = Time.unscaledTime;

            string dir = null;
            try
            {
                dir = MusicFolder;
                Directory.CreateDirectory(dir);
                MusicFolderGuide.Ensure(dir);

                // Ask Unity to open the folder as a file:/// URL (the OS shows it in its file browser); this mod never uses System.Diagnostics.Process.
                // The trailing separator marks it as a folder; Uri escapes spaces and special characters.
                var url = new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri;
                Application.OpenURL(url);
                Plugin.Verbose($"OpenURL {url}");

                Plugin.Log.LogInfo($"Opened Music folder: {dir}");
                RadioSession.Instance?.ShowToast("Opened Music folder");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not open Music folder '{dir}': {e.Message}");
                RadioSession.Instance?.ShowToast("Could not open Music folder (see log)");
                return false;
            }
        }
    }
}
