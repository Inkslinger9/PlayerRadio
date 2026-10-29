using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zorro.UI;

namespace PlayerRadio
{
    /// <summary>
    /// Adds a RADIO button to the pause menu (a clone of the Settings button) and our own <see cref="RadioPausePage"/>.
    /// Game members used (all verified in the decompiled game, PEAK 3.0.1):
    ///  - PauseMenuMainPage.m_settingsButton: private [SerializeField] Button (AC/PauseMenuMainPage.cs:18), read by reflection.
    ///  - UIPageHandler (Zorro.UI): the page handler above the main page; TransistionToPage(UIPage, PageTransistion) is public.
    ///  - PauseMenuSettingsMenuPage.sharedSettingsMenu -> SharedSettingsMenu.m_settingsCellPrefab (both public) for the
    ///    game's own slider rows (SettingsUICell).
    ///  - LocalizedText (AC/LocalizedText.cs): removed from every clone so it can't overwrite our labels.
    ///  - GUIManager.instance / GUIManager.PauseAction (public, invoked when the pause menu opens, AC/GUIManager.cs:429).
    /// Anything missing -> warning + no RADIO button; the hotkeys keep working.
    /// </summary>
    internal static class RadioPauseMenu
    {
        internal const string ButtonName = "PlayerRadio_RadioButton";
        internal const string PageName = "PlayerRadio_RadioPage";
        private static bool _dumped;

        internal static void Install(PauseMenuMainPage main)
        {
            if (main == null || !Plugin.PauseMenuPage.Value) return;

            var handler = main.GetComponentInParent<UIPageHandler>();
            var settingsField = AccessTools.Field(typeof(PauseMenuMainPage), "m_settingsButton");
            var settingsBtn = settingsField?.GetValue(main) as Button;
            if (handler == null || settingsBtn == null || main.transform.parent == null)
            {
                Plugin.Log.LogWarning($"Pause menu: {(handler == null ? "page handler" : settingsBtn == null ? "PauseMenuMainPage.m_settingsButton" : "page parent")} not found (game update?). No RADIO button; hotkeys still work.");
                return;
            }
            if (main.transform.parent.Find(PageName) != null) return; // already installed for this menu

            // RADIO button on the main page, next to Settings.
            var column = settingsBtn.transform.parent;
            var radioBtn = CloneButton(settingsBtn.gameObject, column, ButtonName, "RADIO");
            radioBtn.transform.SetSiblingIndex(settingsBtn.transform.GetSiblingIndex() + 1);
            var above = PlaceButton(radioBtn, settingsBtn, column);
            LinkNavigation(radioBtn, above);

            // Our page, a sibling of the game's pages (inactive until opened).
            var page = RadioPausePage.Create(handler, main, settingsBtn);
            radioBtn.onClick.AddListener(page.Open);

            var gui = GUIManager.instance;
            if (gui != null) page.HookPauseAction(gui);

            Plugin.Log.LogInfo($"Pause menu: RADIO button and page added (button at {((RectTransform)radioBtn.transform).anchoredPosition}, under '{column.name}').");

            if (Plugin.DumpPauseMenu.Value && !_dumped)
            {
                _dumped = true;
                DumpHierarchy(handler.transform);
            }
        }

        /// <summary>Clone a game button: no localisation, no click listeners, our label, explicit/automatic navigation reset.</summary>
        internal static Button CloneButton(GameObject template, Transform parent, string name, string label)
        {
            var go = UnityEngine.Object.Instantiate(template, parent, false);
            go.name = name;
            StripLocalization(go);
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent(); // drops persistent listeners too (runtime ones are never cloned)
            b.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            SetLabel(go, label);
            return b;
        }

        internal static void StripLocalization(GameObject go)
        {
            foreach (var lt in go.GetComponentsInChildren<LocalizedText>(true)) UnityEngine.Object.DestroyImmediate(lt);
        }

        internal static void SetLabel(GameObject go, string label)
        {
            var t = go.GetComponentInChildren<TMP_Text>(true);
            if (t != null) t.text = label;
        }

        /// <summary>Position the RADIO button; returns the button it sits under (for navigation).</summary>
        private static Button PlaceButton(Button radio, Button settings, Transform column)
        {
            var rt = (RectTransform)radio.transform;
            var srt = (RectTransform)settings.transform;

            string custom = Plugin.PauseMenuButtonPosition.Value?.Trim();
            if (!string.IsNullOrEmpty(custom))
            {
                var parts = custom.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    rt.anchoredPosition = new Vector2(x, y);
                    return settings;
                }
                Plugin.Log.LogWarning($"PauseMenuButtonPosition '{custom}' is not x,y - placing the RADIO button automatically.");
            }

            // A layout group positions children by sibling order: right after Settings.
            if (column.GetComponent<LayoutGroup>() != null) return settings;

            // Free layout: put it under the lowest active button of the Settings column, one row further down.
            var col = new List<Button>();
            foreach (Transform child in column)
            {
                if (child == radio.transform || !child.gameObject.activeSelf) continue;
                var b = child.GetComponent<Button>();
                if (b == null || !(child is RectTransform crt)) continue;
                if (Mathf.Abs(crt.anchoredPosition.x - srt.anchoredPosition.x) > 5f || crt.anchorMin != srt.anchorMin) continue;
                col.Add(b);
            }
            if (!col.Contains(settings)) col.Add(settings);
            col.Sort((a, b) => ((RectTransform)b.transform).anchoredPosition.y.CompareTo(((RectTransform)a.transform).anchoredPosition.y));
            float step = 0f;
            for (int i = 1; i < col.Count; i++)
            {
                float d = ((RectTransform)col[i - 1].transform).anchoredPosition.y - ((RectTransform)col[i].transform).anchoredPosition.y;
                if (d > 1f && (step == 0f || d < step)) step = d;
            }
            if (step <= 0f) step = srt.rect.height * 1.15f + 4f;
            var lowest = col[col.Count - 1];
            var lrt = (RectTransform)lowest.transform;
            rt.anchoredPosition = new Vector2(lrt.anchoredPosition.x, lrt.anchoredPosition.y - step);
            Plugin.Verbose($"Pause menu column: {string.Join(", ", col.Select(b => b.name + "@" + ((RectTransform)b.transform).anchoredPosition))}; step {step}");
            return lowest;
        }

        /// <summary>Gamepad: splice the RADIO button into the column's navigation right under <paramref name="above"/>.</summary>
        private static void LinkNavigation(Button radio, Button above)
        {
            var an = above.navigation;
            if (an.mode != Navigation.Mode.Explicit) { radio.navigation = new Navigation { mode = Navigation.Mode.Automatic }; return; }
            var below = an.selectOnDown;
            radio.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = above, selectOnDown = below };
            an.selectOnDown = radio;
            above.navigation = an;
            if (below != null && below.navigation.mode == Navigation.Mode.Explicit && below.navigation.selectOnUp == above)
            {
                var bn = below.navigation;
                bn.selectOnUp = radio;
                below.navigation = bn;
            }
        }

        // ----------------------------------------------------------------------
        // Debug: one-time hierarchy dump of the pause menu (positions + components), for layout fixes.
        // ----------------------------------------------------------------------

        private static void DumpHierarchy(Transform root)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Pause menu hierarchy (PlayerRadio {Plugin.Version}; turn off Debug.DumpPauseMenuHierarchy to stop):");
                int nodes = 0;
                Dump(root, 0, sb, ref nodes);
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Pause menu dump failed: {e.Message}"); }
        }

        private static void Dump(Transform t, int depth, StringBuilder sb, ref int nodes)
        {
            if (++nodes > 800 || depth > 12) return;
            sb.Append(' ', depth * 2).Append(t.name);
            if (!t.gameObject.activeSelf) sb.Append(" [inactive]");
            if (t is RectTransform rt)
                sb.Append($"  pos={rt.anchoredPosition} size={rt.sizeDelta} anchors={rt.anchorMin}-{rt.anchorMax} pivot={rt.pivot}");
            var comps = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name);
            sb.Append("  {").Append(string.Join(", ", comps)).Append('}');
            var txt = t.GetComponent<TMP_Text>();
            if (txt != null) sb.Append("  text=\"").Append(Util.Trunc((txt.text ?? "").Replace('\n', ' '), 40)).Append('"');
            var sel = t.GetComponent<Selectable>();
            if (sel != null) sb.Append("  nav=").Append(sel.navigation.mode);
            sb.Append('\n');
            if (t.name == PageName) { sb.Append(' ', depth * 2 + 2).Append("(Player Radio page contents skipped)\n"); return; }
            foreach (Transform c in t) Dump(c, depth + 1, sb, ref nodes);
        }
    }
}
