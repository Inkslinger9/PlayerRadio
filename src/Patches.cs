using System;
using HarmonyLib;

namespace PlayerRadio
{
    // Character.Awake is private (verified: AC/Character.cs:314) -> patch by name.
    [HarmonyPatch(typeof(Character), "Awake")]
    internal static class Patch_CharacterAwake
    {
        private static void Postfix(Character __instance)
        {
            try { RadioSession.Instance?.OnCharacterAwake(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"Character.Awake postfix: {e.Message}"); }
        }
    }

    // [PunRPC] public void RPCA_Die() (AC/Character.cs:733) warps the body to DeathPos; capture the position first.
    [HarmonyPatch(typeof(Character), nameof(Character.RPCA_Die))]
    internal static class Patch_CharacterDie
    {
        private static void Prefix(Character __instance)
        {
            try { RadioSession.Instance?.OnCharacterDying(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"Character.RPCA_Die prefix: {e.Message}"); }
        }
    }

    // public void FinishZombifying() (AC/Character.cs:814) sets dead and warps to DeathPos without RPCA_Die.
    [HarmonyPatch(typeof(Character), nameof(Character.FinishZombifying))]
    internal static class Patch_CharacterZombify
    {
        private static void Prefix(Character __instance)
        {
            try { RadioSession.Instance?.OnCharacterDying(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"Character.FinishZombifying prefix: {e.Message}"); }
        }
    }

    // public string CharacterInteractible.GetName() => character.characterName (AC/CharacterInteractible.cs:63)
    [HarmonyPatch(typeof(CharacterInteractible), nameof(CharacterInteractible.GetName))]
    internal static class Patch_CharacterInteractibleName
    {
        private static void Postfix(CharacterInteractible __instance, ref string __result)
        {
            try
            {
                if (!Plugin.MarkSourcePlayerName.Value) return;
                var s = RadioSession.Instance;
                if (s != null && s.IsRadioCharacter(__instance.character)) __result += " [Radio]";
            }
            catch { /* cosmetic only */ }
        }
    }
}
