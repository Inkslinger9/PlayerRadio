using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Zorro.ControllerSupport;
using Zorro.Settings;
using Zorro.UI;

namespace PlayerRadio
{
    /// <summary>
    /// The pause menu's RADIO page. A Zorro.UI page like the game's Settings/Controls pages:
    ///  - UIPage (Zorro.UI): opened with UIPageHandler.TransistionToPage(UIPage, PageTransistion) + SetActivePageTransistion.
    ///  - IHaveParentPage: PauseMenuHandler.Update sends Esc / gamepad back to GetParentPage() (AC/PauseMenuHandler.cs).
    ///  - INavigationPage (Zorro.ControllerSupport): PageNavigationHandler selects GetFirstSelectedGameObject() for gamepads.
    /// Built at runtime from clones of the Settings button plus plain uGUI/TextMeshPro objects.
    /// </summary>
    public sealed class RadioPausePage : UIPage, IHaveParentPage, INavigationPage
    {
        private UIPageHandler _handler;
        private PauseMenuMainPage _main;
        private GUIManager _gui;

        private TMP_FontAsset _font;
        private Material _fontMat;

        private TextMeshProUGUI _nowTitle, _nowTime, _songsHeader;
        private Button _playBtn, _prevBtn, _nextBtn, _muteBtn, _folderBtn, _backBtn;
        private RectTransform _viewport, _content;

        private readonly List<RadioVolumeSetting> _volumes = new List<RadioVolumeSetting>();
        private readonly List<SettingsUICell> _cells = new List<SettingsUICell>();
        private readonly List<(Slider slider, ConfigEntry<float> entry, TextMeshProUGUI label, string name)> _plainSliders = new List<(Slider, ConfigEntry<float>, TextMeshProUGUI, string)>();
        private readonly List<(TrackMeta track, TextMeshProUGUI text)> _rows = new List<(TrackMeta, TextMeshProUGUI)>();

        private List<TrackMeta> _listSource;
        private string _highlightHex = "";
        private float _nextRefresh;
        private GameObject _lastFollowed;
        private bool _errorLogged;

        /// <summary>True while the RADIO page is on screen (the IMGUI overlay hides itself then; the page shows the same info).</summary>
        internal static bool Showing { get; private set; }

        private const int MaxRows = 1000;
        private static readonly Color TextColor = new Color(0.93f, 0.91f, 0.86f, 1f);
        private static readonly Color DimColor = new Color(0.75f, 0.72f, 0.66f, 1f);
        private static readonly Color AccentColor = new Color(1f, 0.82f, 0.4f, 1f);

        // ==================================================================
        // Construction
        // ==================================================================

        internal static RadioPausePage Create(UIPageHandler handler, PauseMenuMainPage main, Button template)
        {
            var go = new GameObject(RadioPauseMenu.PageName, typeof(RectTransform));
            go.SetActive(false); // nothing below wakes up (LocalizedText.OnEnable etc.) before we've stripped it
            go.layer = main.gameObject.layer;
            go.transform.SetParent(main.transform.parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            var page = go.AddComponent<RadioPausePage>();
            page._handler = handler;
            page._main = main;
            page.pageHandler = handler;
            page.Build(template);
            return page;
        }

        private void Build(Button template)
        {
            var tmplText = template.GetComponentInChildren<TMP_Text>(true);
            _font = tmplText != null ? tmplText.font : TMP_Settings.defaultFontAsset;
            _fontMat = tmplText != null ? tmplText.fontSharedMaterial : null;

            // Dark panel in the middle of the screen.
            var panel = NewUI("Panel", transform);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0.2f, 0.07f); prt.anchorMax = new Vector2(0.8f, 0.93f);
            prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0.06f, 0.055f, 0.05f, 0.9f);
            var vl = panel.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(32, 32, 24, 24);
            vl.spacing = 10f;
            vl.childControlWidth = true; vl.childControlHeight = true;
            vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            vl.childAlignment = TextAnchor.UpperCenter;

            Text(panel.transform, "Title", "PLAYER RADIO", 40f, TextAlignmentOptions.Center, 52f, AccentColor);
            _nowTitle = Text(panel.transform, "NowPlaying", "", 28f, TextAlignmentOptions.Center, 38f, TextColor);
            _nowTime = Text(panel.transform, "NowTime", "", 22f, TextAlignmentOptions.Center, 30f, DimColor);

            var transport = Row(panel.transform, "Transport", 64f);
            _prevBtn = GameButton(template, transport, "Prev", "PREV", RadioActions.Previous, 64f);
            _playBtn = GameButton(template, transport, "PlayPause", "PLAY", RadioActions.PlayPause, 64f);
            _nextBtn = GameButton(template, transport, "Next", "NEXT", RadioActions.Next, 64f);

            GameObject cellPrefab = null;
            try
            {
                var settingsPage = _handler.GetComponentInChildren<PauseMenuSettingsMenuPage>(true);
                if (settingsPage != null && settingsPage.sharedSettingsMenu != null) cellPrefab = settingsPage.sharedSettingsMenu.m_settingsCellPrefab;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Pause menu: settings row prefab lookup failed ({e.Message}); using plain sliders."); }
            if (cellPrefab == null) Plugin.Log.LogWarning("Pause menu: game settings row prefab not found; using plain sliders.");
            VolumeRow(panel.transform, cellPrefab, "MY RADIO VOLUME", Plugin.MyRadioVolume);
            VolumeRow(panel.transform, cellPrefab, "OTHER RADIOS VOLUME", Plugin.OtherRadiosVolume);

            var toggles = Row(panel.transform, "Toggles", 56f);
            _muteBtn = GameButton(template, toggles, "MuteOthers", "MUTE OTHER RADIOS", RadioActions.ToggleOthersMuted, 56f);
            _folderBtn = GameButton(template, toggles, "OpenFolder", "OPEN MUSIC FOLDER", () => RadioActions.OpenMusicFolder(), 56f);

            _songsHeader = Text(panel.transform, "SongsHeader", "YOUR SONGS", 24f, TextAlignmentOptions.Left, 32f, AccentColor);
            SongList(panel.transform);

            var back = Row(panel.transform, "BackRow", 60f);
            _backBtn = GameButton(template, back, "Back", "BACK", Back, 60f);
        }

        private GameObject NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = gameObject.layer;
            go.transform.SetParent(parent, false);
            return go;
        }

        private TextMeshProUGUI Text(Transform parent, string name, string text, float size, TextAlignmentOptions align, float height, Color color)
        {
            var go = NewUI(name, parent);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            if (_fontMat != null) t.fontSharedMaterial = _fontMat;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.text = text;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height; le.minHeight = height;
            return t;
        }

        private Transform Row(Transform parent, string name, float height)
        {
            var go = NewUI(name, parent);
            var hl = go.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 14f;
            hl.childControlWidth = true; hl.childControlHeight = true;
            hl.childForceExpandWidth = true; hl.childForceExpandHeight = true;
            hl.childAlignment = TextAnchor.MiddleCenter;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height; le.minHeight = height;
            return go.transform;
        }

        /// <summary>A clone of the game's Settings button (same look, hover feedback and click sound) with our label and action.</summary>
        private Button GameButton(Button template, Transform parent, string name, string label, Action onClick, float height)
        {
            var b = RadioPauseMenu.CloneButton(template.gameObject, parent, "PR_" + name, label);
            b.onClick.AddListener(() =>
            {
                try { onClick(); Refresh(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Radio page '{name}': {e.Message}"); }
            });
            var le = b.GetComponent<LayoutElement>();
            if (le == null) le = b.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = false;
            le.preferredHeight = height; le.minHeight = height;
            le.flexibleWidth = 1f; le.preferredWidth = 200f; le.minWidth = 120f;
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null)
            {
                float max = t.fontSize > 1f ? t.fontSize : 30f;
                t.enableAutoSizing = true;
                t.fontSizeMax = max;
                t.fontSizeMin = Mathf.Min(14f, max);
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }
            return b;
        }

        /// <summary>A volume slider: the game's own settings row (SettingsUICell + FloatSettingUI) if available, else a plain uGUI slider.</summary>
        private void VolumeRow(Transform parent, GameObject cellPrefab, string label, ConfigEntry<float> entry)
        {
            if (cellPrefab != null && GameHandler.Instance != null && GameHandler.Instance.SettingsHandler != null)
            {
                GameObject go = null;
                try
                {
                    go = Instantiate(cellPrefab, parent, false);
                    go.name = "PR_" + label.Replace(' ', '_');
                    var cell = go.GetComponent<SettingsUICell>();
                    if (cell == null) throw new Exception("no SettingsUICell on the row prefab");
                    RadioPauseMenu.StripLocalization(go);
                    cell.fadeInSFX = null; // no fade-in sound every time the page opens
                    var setting = new RadioVolumeSetting(entry);
                    cell.Setup(setting);
                    RadioPauseMenu.StripLocalization(go); // the input cell Setup() instantiated
                    if (cell.m_text != null) cell.m_text.text = label;
                    var rt = (RectTransform)go.transform;
                    float h = rt.rect.height > 1f ? rt.rect.height : (rt.sizeDelta.y > 1f ? rt.sizeDelta.y : 60f);
                    var le = go.GetComponent<LayoutElement>();
                    if (le == null) le = go.AddComponent<LayoutElement>();
                    le.ignoreLayout = false;
                    le.preferredHeight = h; le.minHeight = h;
                    _volumes.Add(setting);
                    _cells.Add(cell);
                    return;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Pause menu: game slider row failed for {label} ({e.Message}); using a plain slider.");
                    if (go != null) DestroyImmediate(go);
                }
            }
            PlainSlider(parent, label, entry);
        }

        private void PlainSlider(Transform parent, string label, ConfigEntry<float> entry)
        {
            var row = Row(parent, "PR_" + label.Replace(' ', '_'), 44f);
            ((HorizontalLayoutGroup)row.GetComponent<LayoutGroup>()).childForceExpandWidth = false;
            var text = Text(row, "Label", label, 24f, TextAlignmentOptions.Left, 44f, TextColor);
            var tle = text.GetComponent<LayoutElement>();
            tle.preferredWidth = 380f; tle.flexibleWidth = 0f;

            var sgo = DefaultControls.CreateSlider(new DefaultControls.Resources());
            sgo.name = "Slider";
            SetLayerRecursive(sgo.transform, gameObject.layer);
            sgo.transform.SetParent(row, false);
            var sle = sgo.AddComponent<LayoutElement>();
            sle.flexibleWidth = 1f; sle.preferredHeight = 24f; sle.minWidth = 200f;
            Tint(sgo.transform.Find("Background"), new Color(0.25f, 0.24f, 0.22f, 1f));
            Tint(sgo.transform.Find("Fill Area/Fill"), AccentColor);
            Tint(sgo.transform.Find("Handle Slide Area/Handle"), Color.white);
            var slider = sgo.GetComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 1f;
            slider.SetValueWithoutNotify(Mathf.Clamp01(entry.Value));
            slider.onValueChanged.AddListener(v => entry.Value = Mathf.Round(v * 100f) / 100f);
            _plainSliders.Add((slider, entry, text, label));
        }

        private static void Tint(Transform t, Color c) { var img = t != null ? t.GetComponent<Image>() : null; if (img != null) img.color = c; }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayerRecursive(c, layer);
        }

        private void SongList(Transform parent)
        {
            var go = NewUI("SongList", parent);
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.04f); // also the raycast target for mouse-wheel scrolling
            var le = go.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f; le.minHeight = 160f; le.preferredHeight = 300f;
            var scroll = go.AddComponent<ScrollRect>();

            var vp = NewUI("Viewport", go.transform);
            _viewport = (RectTransform)vp.transform;
            _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(6f, 6f); _viewport.offsetMax = new Vector2(-6f, -6f);
            vp.AddComponent<RectMask2D>();

            var content = NewUI("Content", vp.transform);
            _content = (RectTransform)content.transform;
            _content.anchorMin = new Vector2(0f, 1f); _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = Vector2.zero; _content.anchoredPosition = Vector2.zero;
            var cvl = content.AddComponent<VerticalLayoutGroup>();
            cvl.spacing = 2f;
            cvl.childControlWidth = true; cvl.childControlHeight = true;
            cvl.childForceExpandWidth = true; cvl.childForceExpandHeight = false;
            var fit = content.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = _viewport;
            scroll.content = _content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 35f;
        }

        private void RebuildList(List<TrackMeta> tracks)
        {
            _listSource = tracks;
            _highlightHex = null;
            _rows.Clear();
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);

            int n = Math.Min(tracks.Count, MaxRows);
            for (int i = 0; i < n; i++)
            {
                var track = tracks[i];
                var row = NewUI("Song" + i, _content);
                var img = row.AddComponent<Image>();
                img.color = Color.white;
                var b = row.AddComponent<Button>();
                b.targetGraphic = img;
                var cb = ColorBlock.defaultColorBlock;
                cb.normalColor = new Color(1f, 1f, 1f, 0.03f);
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.16f);
                cb.selectedColor = new Color(1f, 0.85f, 0.45f, 0.22f);
                cb.pressedColor = new Color(1f, 0.85f, 0.45f, 0.35f);
                cb.disabledColor = new Color(1f, 1f, 1f, 0.02f);
                cb.colorMultiplier = 1f; cb.fadeDuration = 0.05f;
                b.colors = cb;
                b.navigation = new Navigation { mode = Navigation.Mode.Vertical };
                b.onClick.AddListener(() =>
                {
                    try { RadioActions.PlayTrack(track); Refresh(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Radio page song click: {e.Message}"); }
                });
                var le = row.AddComponent<LayoutElement>();
                le.preferredHeight = 36f; le.minHeight = 36f;

                var tgo = NewUI("Text", row.transform);
                var trt = (RectTransform)tgo.transform;
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(12f, 0f); trt.offsetMax = new Vector2(-12f, 0f);
                var t = tgo.AddComponent<TextMeshProUGUI>();
                if (_font != null) t.font = _font;
                if (_fontMat != null) t.fontSharedMaterial = _fontMat;
                t.fontSize = 22f;
                t.alignment = TextAlignmentOptions.MidlineLeft;
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.overflowMode = TextOverflowModes.Ellipsis;
                t.raycastTarget = false;
                t.color = TextColor;
                t.text = (i + 1) + ".  " + track.Title + (track.Duration > 0f ? "   (" + Util.Time(track.Duration) + ")" : "");
                _rows.Add((track, t));
            }
            if (tracks.Count > MaxRows) Text(_content, "More", $"... and {tracks.Count - MaxRows} more (use Next/Prev)", 20f, TextAlignmentOptions.Left, 30f, DimColor);
            _content.anchoredPosition = Vector2.zero;
        }

        // ==================================================================
        // Page plumbing
        // ==================================================================

        internal void HookPauseAction(GUIManager gui)
        {
            _gui = gui;
            gui.PauseAction += OnPauseAction;
        }

        private void OnEnable() => Showing = true;
        private void OnDisable() => Showing = false;

        private void OnDestroy()
        {
            Showing = false;
            if (_gui != null) _gui.PauseAction -= OnPauseAction;
            foreach (var v in _volumes) v.Dispose();
        }

        /// <summary>GUIManager.PauseAction: the pause menu was just opened. Bring the page's data up to date before it is shown.</summary>
        private void OnPauseAction()
        {
            try { Refresh(); }
            catch (Exception e) { Plugin.Verbose($"Radio page refresh on pause: {e.Message}"); }
        }

        internal void Open()
        {
            if (_handler == null || _handler.currentPage == this) return;
            _handler.TransistionToPage(this, new SetActivePageTransistion());
        }

        private void Back()
        {
            if (_handler == null || _main == null) return;
            _handler.TransistionToPage(_main, new SetActivePageTransistion());
        }

        public (UIPage, PageTransistion) GetParentPage() => (_main, new SetActivePageTransistion());

        public GameObject GetFirstSelectedGameObject() => _playBtn != null ? _playBtn.gameObject : null;

        public override void OnPageEnter()
        {
            base.OnPageEnter();
            foreach (var c in _cells) if (c != null) c.FadeIn();
            _lastFollowed = null;
            _nextRefresh = 0f;
            try { Refresh(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Radio page: {e.Message}"); }
        }

        private void Update()
        {
            try
            {
                if (Time.unscaledTime >= _nextRefresh)
                {
                    _nextRefresh = Time.unscaledTime + 0.2f;
                    Refresh();
                }
                FollowSelection();
            }
            catch (Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; Plugin.Log.LogWarning($"Radio page update: {e}"); }
            }
        }

        private void Refresh()
        {
            var s = RadioSession.Instance;
            if (s == null || s.Mine == null)
            {
                _nowTitle.text = "Player Radio is not running";
                _nowTime.text = "";
                return;
            }
            var mine = s.Mine;
            var st = mine.State;
            if (st.Current != null)
            {
                _nowTitle.text = (st.Playing ? "Playing:  " : "Paused:  ") + st.Current.Title;
                string blocker = mine.Blocker(s);
                float dur = mine.Duration;
                float pos = Mathf.Min(st.PositionAt(RadioSession.Now), dur > 0 ? dur : float.MaxValue);
                _nowTime.text = blocker ?? (Util.Time(pos) + " / " + Util.Time(dur));
            }
            else
            {
                _nowTitle.text = "Your radio is off";
                _nowTime.text = s.MyTracks.Count == 0
                    ? "No songs yet: press OPEN MUSIC FOLDER, copy mp3/wav/ogg files in, they show up below."
                    : "Press PLAY or pick a song below. Everyone near you hears it.";
            }

            SetLabel(_playBtn, st.Playing ? "PAUSE" : "PLAY");
            SetLabel(_muteBtn, s.OthersMuted ? "UNMUTE OTHER RADIOS" : "MUTE OTHER RADIOS");

            string scan = s.Library != null ? s.Library.ScanStatus : null;
            _songsHeader.text = $"YOUR SONGS ({s.MyTracks.Count})" + (string.IsNullOrEmpty(scan) ? "" : "  -  " + scan);

            if (!ReferenceEquals(_listSource, s.MyTracks)) RebuildList(s.MyTracks);
            string hex = st.CurrentHex ?? "";
            if (hex != _highlightHex)
            {
                _highlightHex = hex;
                foreach (var (track, text) in _rows)
                    text.color = track.Hex == hex ? AccentColor : TextColor;
            }

            foreach (var v in _volumes) v.SyncFromConfig();
            foreach (var p in _plainSliders)
            {
                float v = Mathf.Clamp01(p.entry.Value);
                if (Mathf.Abs(p.slider.value - v) > 0.001f) p.slider.SetValueWithoutNotify(v);
                p.label.text = p.name + "  " + Mathf.RoundToInt(v * 100f) + "%";
            }
        }

        private static void SetLabel(Button b, string label)
        {
            if (b == null) return;
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null && t.text != label) t.text = label;
        }

        /// <summary>Gamepad/keyboard navigation in the song list: keep the selected row inside the visible area.</summary>
        private void FollowSelection()
        {
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            if (sel == null || sel == _lastFollowed) return;
            _lastFollowed = sel;
            if (sel.transform.parent != _content) return;

            var item = (RectTransform)sel.transform;
            var corners = new Vector3[4];
            item.GetWorldCorners(corners);
            float bottom = _viewport.InverseTransformPoint(corners[0]).y;
            float top = _viewport.InverseTransformPoint(corners[1]).y;
            var vr = _viewport.rect;
            var pos = _content.anchoredPosition;
            if (top > vr.yMax) pos.y -= top - vr.yMax;
            else if (bottom < vr.yMin) pos.y += vr.yMin - bottom;
            else return;
            float maxY = Mathf.Max(0f, _content.rect.height - vr.height);
            pos.y = Mathf.Clamp(pos.y, 0f, maxY);
            _content.anchoredPosition = pos;
        }
    }

    /// <summary>
    /// Wraps one of our 0-1 volume config entries as a Zorro FloatSetting so the game's own slider row can drive it.
    /// Never registered with the game's SettingsHandler: Load/Save are no-ops, the value lives in our BepInEx config.
    /// (FloatSetting: Value/MinValue/MaxValue protected set, SetValue -> ApplyValue + handler.SaveSetting.)
    /// </summary>
    internal sealed class RadioVolumeSetting : FloatSetting
    {
        private readonly ConfigEntry<float> _entry;

        public RadioVolumeSetting(ConfigEntry<float> entry)
        {
            _entry = entry;
            MinValue = 0f;
            MaxValue = 1f;
            Value = Mathf.Clamp01(entry.Value);
        }

        public override void Load(ISettingsSaveLoad loader) { }
        public override void Save(ISettingsSaveLoad saver) { }

        public override void ApplyValue()
        {
            float v = Mathf.Clamp01(Value);
            if (Mathf.Abs(_entry.Value - v) > 0.0001f) _entry.Value = v;
        }

        protected override float GetDefaultValue() => (float)_entry.DefaultValue;
        protected override float2 GetMinMaxValue() => new float2(0f, 1f);

        /// <summary>Pull a value changed elsewhere (hotkeys, config manager) into the slider.</summary>
        public void SyncFromConfig()
        {
            float v = Mathf.Clamp01(_entry.Value);
            if (Mathf.Abs(v - Value) <= 0.0001f) return;
            Value = v;
            OnSettingChangedExternal();
        }
    }
}
