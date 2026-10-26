using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;

namespace PlayerRadio
{
    public static class Diagnostics
    {
        /// <summary>Logs every binding of the game's project-wide InputActionAsset and flags clashes with our hotkeys.</summary>
        public static void DumpInputBindings(IEnumerable<Hotkey> ours, string when)
        {
            try
            {
                var asset = InputSystem.actions;
                if (asset == null)
                {
                    Plugin.Log.LogWarning($"[Bindings/{when}] InputSystem.actions is null; cannot list game bindings.");
                    return;
                }
                var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                int count = 0;
                Plugin.Log.LogInfo($"[Bindings/{when}] Game Input System bindings (asset '{asset.name}'):");
                foreach (var map in asset.actionMaps)
                {
                    foreach (var action in map.actions)
                    {
                        var paths = new List<string>();
                        foreach (var b in action.bindings)
                        {
                            if (b.isComposite) continue;
                            var p = b.effectivePath;
                            if (string.IsNullOrEmpty(p)) continue;
                            string label = b.isPartOfComposite ? $"{p}({b.name})" : p;
                            if (!string.IsNullOrEmpty(b.groups)) label += $"[{b.groups}]";
                            paths.Add(label);
                            if (!used.TryGetValue(p, out var l)) used[p] = l = new List<string>();
                            l.Add($"{map.name}/{action.name}");
                            count++;
                        }
                        Plugin.Log.LogInfo($"  {map.name}/{action.name}: {(paths.Count == 0 ? "(unbound)" : string.Join(", ", paths))}");
                    }
                }
                Plugin.Log.LogInfo($"[Bindings/{when}] {count} binding(s) listed.");
                foreach (var k in ours)
                {
                    if (k.Path != null && used.TryGetValue(k.Path, out var actions))
                        Plugin.Log.LogWarning($"Hotkey {k.Name} ({k.Path}) is ALSO bound in the game to: {string.Join(", ", actions.Distinct())}. Change it in the config if that's a problem.");
                    else
                        Plugin.Log.LogInfo($"Hotkey {k.Name} = {k.Path} (no clash with game bindings).");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Binding dump failed: {e.Message}");
            }
        }
    }
}
