using System.Text;
using UnityEngine;

namespace PlayerRadio
{
    /// <summary>Small IMGUI overlay (top-right): your radio, transfers, nearby radios, key hints. IMGUI avoids any font/canvas dependency.</summary>
    public sealed class RadioHud : MonoBehaviour
    {
        private GUIStyle _style, _shadow;
        private int _styleSize;
        private readonly StringBuilder _sb = new StringBuilder();
        private float _hintsUntil;

        private void Start() => _hintsUntil = Time.unscaledTime + 20f;

        private void OnGUI()
        {
            if (!Plugin.ShowHud.Value) return;
            var s = RadioSession.Instance;
            if (s == null) return;

            bool inGame = Character.localCharacter != null;
            var mine = s.Mine;
            bool active = mine != null && mine.State.Current != null;
            bool toast = s.Toast != null && Time.unscaledTime < s.ToastUntil;
            bool transfer = s.Transfer.DownloadCount > 0 || s.Transfer.UploadCount > 0;
            var nearby = Plugin.ShowNearbyRadios.Value && inGame ? s.NearbyRadios(4) : null;
            bool others = nearby != null && nearby.Count > 0;
            if (!inGame && !active && !toast && !transfer) return;

            int size = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 1080f * 15f), 11, 28);
            if (_style == null || size != _styleSize)
            {
                _styleSize = size;
                _style = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.UpperRight, wordWrap = false, richText = true };
                _style.normal.textColor = new Color(1f, 0.95f, 0.8f, 0.95f);
                _shadow = new GUIStyle(_style);
                _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }

            _sb.Length = 0;
            int lines = 0;
            void Line(string t) { _sb.Append(t).Append('\n'); lines++; }

            // --- your radio ---
            if (active)
            {
                var st = mine.State;
                string title = Util.Trunc(st.Current.Title, 48);
                float dur = mine.Duration;
                float pos = Mathf.Min(st.PositionAt(RadioSession.Now), dur > 0 ? dur : float.MaxValue);
                string blocker = mine.Blocker(s);
                Line("<b>Your radio</b> " + (st.Playing ? "> " : "|| ") + title);
                if (blocker != null) Line(blocker);
                else Line(Util.Time(pos) + " / " + Util.Time(dur) + "  -  " + mine.Emitter.Placement);
            }
            else if (inGame)
            {
                int n = s.MyTracks.Count;
                Line("<b>Your radio</b> off - " + n + (n == 1 ? " song" : " songs"));
                if (!string.IsNullOrEmpty(s.Library.ScanStatus)) Line(s.Library.ScanStatus);
            }

            // --- transfers ---
            foreach (var hex in s.Transfer.DownloadingHexes)
            {
                var status = s.Transfer.StatusFor(hex);
                if (status != null) Line(status);
            }
            if (s.Transfer.UploadCount > 0)
                Line("Sending songs to " + s.Transfer.UploadCount + (s.Transfer.UploadCount == 1 ? " player" : " players"));

            Line("Vol mine " + Mathf.RoundToInt(Plugin.MyRadioVolume.Value * 100) + "% / others " +
                 (s.OthersMuted ? "muted" : Mathf.RoundToInt(Plugin.OtherRadiosVolume.Value * 100) + "%") + "  -  " + s.ModeShort);

            // --- other radios nearby ---
            if (others)
            {
                foreach (var (r, d) in nearby)
                {
                    string who = Util.Trunc(s.PlayerName(r.Actor), 16);
                    string what = Util.Trunc(r.State.Current?.Title ?? "?", 32);
                    string where = d >= 0 ? " (" + Mathf.RoundToInt(d) + " m)" : "";
                    string block = r.Blocker(s);
                    Line("<size=" + Mathf.RoundToInt(size * 0.9f) + ">" + who + ": " + what + where + (block != null ? " - " + block : "") + "</size>");
                }
            }

            if (toast) Line("<i>" + s.Toast + "</i>");

            if (Plugin.ShowKeyHints.Value && (Time.unscaledTime < _hintsUntil || !active || toast))
            {
                _sb.Append("<size=").Append(Mathf.RoundToInt(size * 0.85f)).Append('>')
                   .Append('[').Append(s.KeyPlayPause.Display).Append("] play/pause  [")
                   .Append(s.KeyPrevious.Display).Append('/').Append(s.KeySkip.Display).Append("] prev/next  [")
                   .Append(s.KeyMuteOthers.Display).Append("] mute others  [")
                   .Append(s.KeyVolDown.Display).Append('/').Append(s.KeyVolUp.Display).Append("] volume  [")
                   .Append(s.KeyOpenFolder.Display).Append("] music folder</size>");
                lines++;
            }

            string text = _sb.ToString().TrimEnd('\n');
            float w = Mathf.Min(Screen.width * 0.6f, size * 40f);
            var rect = new Rect(Screen.width - w - 16f, 12f, w, size * 1.6f * (lines + 2));
            var shadowRect = new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height);
            GUI.Label(shadowRect, StripColor(text), _shadow);
            GUI.Label(rect, text, _style);
        }

        private static string StripColor(string s) => s; // rich text tags carry no colour, shadow style colours it
    }
}
