using System;
using EHS;
using EHS.Skins;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CharmReplacer;

internal static class CharmPatches
{
    // ApplyLocalSkin / ApplySkinFromNetwork run on the game thread, where a full sync rescan stalls
    // the frame. Either can change a cover or a charm (one system since v1.7) and the category isn't
    // read, so both caches are invalidated and the per-frame fast poll re-applies.
    public static void SkinAppliedPostfix()
    {
        try
        {
            CharmState.SkinnedRendererIds.Clear();
            CharmState.CharmedMeshFilterIds.Clear();
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Skin] SkinApplied postfix error: {ex}"); }
    }
}

internal static class PlayerSkinsPatches
{
    // PlayerSkins.Init: fires when a player (local or remote) spawns, also mid-game.
    public static void StartPostfix(PlayerSkins __instance)
    {
        try
        {
            SkinOverride.Register(__instance);
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Skin] PlayerSkins.Start postfix error: {ex}"); }
    }
}

internal static class DestroyedPhoneEffectHandlerPatches
{
    // Resolve which player owns this debris handler and paint its pre-spawned
    // visuals with the right texture so the break animation looks correct.
    private static void PrePaintDestroyedPhone(Il2CppObjectBase __instance)
    {
        Texture2D targetTex = null;
        Texture2D targetEmission = null;

        var comp = __instance.TryCast<Component>();
        if (comp != null)
        {
            var rootGo = comp.gameObject.transform.root.gameObject;
            if (rootGo == PlayerIdentity.GetLocalPlayerGO())
            {
                if (SkinOverride.IsLocalEnabled(SkinCategory.Cover))
                {
                    targetTex = CharmState.CustomSkinTexture;
                    targetEmission = CharmState.CustomSkinEmissionTexture;
                }
            }
            else if (SkinOverride.AllowsRemoteCustom(rootGo, SkinCategory.Cover))
            {
                string nick = PlayerIdentity.GetPlayerNickname(rootGo);
                if (!string.IsNullOrEmpty(nick) && CharmState.PlayerBaseTextures.TryGetValue(nick, out var pTex))
                {
                    targetTex = pTex;
                    if (CharmState.PlayerEmissionTextures.TryGetValue(nick, out var eTex))
                        targetEmission = eTex;
                }
            }
        }

        if (targetTex != null)
        {
            DebrisSystem.SkinDestroyedPhoneHandler(__instance, targetTex, targetEmission);
        }
    }

    // Sync paint of this phone's pieces only: they must be skinned before the destroy animation
    // plays. Init runs on every (re)spawn, so the full player rescan is left to the next frames.
    public static void InitPostfix(Il2CppObjectBase __instance)
    {
        try
        {
            PrePaintDestroyedPhone(__instance);
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] InitPostfix error: {ex}"); }
    }

    public static void SetToNormalPostfix(Il2CppObjectBase __instance)
    {
        try { PrePaintDestroyedPhone(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] SetToNormal postfix error: {ex}"); }
    }

    // Cover pieces cloned into debris must not carry the jump glow along (CoverExtras).
    public static void DebrisPostfix(Transform __result)
    {
        try { CoverExtras.StripFromDebris(__result); }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] Debris postfix error: {ex}"); }
    }

    public static void SetToDestroyedPostfix(Il2CppObjectBase __instance)
    {
        try { PrePaintDestroyedPhone(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] SetToDestroyed postfix error: {ex}"); }
    }
}

// Customization screen: the "Custom" entries (NativeSkinMenu).
internal static class NativeMenuPatches
{
    public static void ApplyEntriesPrefix(CustomizationScreenController __instance, Il2CppSystem.Collections.Generic.List<SkinUISO> skins)
    {
        try { NativeSkinMenu.AddEntries(__instance, skins); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] ApplyEntries prefix error: {ex}"); }
    }

    // The screen only lists unlocked skins and SkinsManager has never heard of ours.
    public static void IsSkinUnlockedPostfix(string skinUISOKey, ref bool __result)
    {
        if (NativeSkinMenu.IsCustomKey(skinUISOKey)) __result = true;
    }

    // Not in the save's seen list either, which would show a permanent "new" badge.
    public static void IsNotSeenSkinPostfix(string skinUISOKey, ref bool __result)
    {
        if (NativeSkinMenu.IsCustomKey(skinUISOKey)) __result = false;
    }

    public static void IsSelectedSkinPostfix(SkinUISO skin, ref bool __result)
    {
        try { if (NativeSkinMenu.IsCustom(skin)) __result = NativeSkinMenu.IsSelected(skin); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] IsSelectedSkin postfix error: {ex}"); }
    }

    public static void CompareSkinsByRarityPostfix(SkinUISO left, SkinUISO right, ref int __result)
    {
        try { __result = NativeSkinMenu.Compare(left, right, __result); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] CompareSkinsByRarity postfix error: {ex}"); }
    }

    // The Mod tab (ModMenuTab): cloned in before the screen collects its tabs, and kept available.
    public static void InitCategoryTabsPrefix(CustomizationScreenController __instance)
    {
        try { ModMenuTab.EnsureTab(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] InitCategoryTabs prefix error: {ex}"); }
    }

    public static void SetCategoryAvailablePrefix(SkinCategoryTabButton __instance, ref bool isAvailable)
    {
        try { ModMenuTab.KeepAvailable(__instance, ref isAvailable); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] SetCategoryAvailable prefix error: {ex}"); }
    }

    // Text labels on entry buttons; the pool reuses buttons, so each setup starts from the game's look.
    public static void EntrySetupPrefix(SkinEntryButton __instance)
    {
        try { ModMenuTab.BeforeEntrySetup(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] Entry setup prefix error: {ex}"); }
    }

    public static void EntrySetupPostfix(SkinEntryButton __instance, SkinUISO skin)
    {
        try { ModMenuTab.AfterEntrySetup(__instance, skin); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] Entry setup postfix error: {ex}"); }
    }

    public static bool OnSkinSelectedPrefix(CustomizationScreenController __instance, SkinUISO skin)
    {
        try { return NativeSkinMenu.OnSkinSelected(__instance, skin); }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NativeMenu] OnSkinSelected prefix error: {ex}");
            return true;
        }
    }
}

// Which skin each viewer loads (SkinOverride).
internal static class SkinOverridePatches
{
    public static bool ApplySkinFromNetworkPrefix(PlayerSkins __instance, SkinCategory category, ref string skinUISOKey)
    {
        try { return SkinOverride.BeforeApplySkin(__instance, category, ref skinUISOKey); }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NativeMenu] ApplySkinFromNetwork prefix error: {ex}");
            return true;
        }
    }

    public static void DummySkinSyncPrefix(PlayerVisualsDummy __instance, SkinCategory category, ref string skinUISOKey)
    {
        try { LocalVisuals.BeforeDummySync(__instance, category, ref skinUISOKey); }
        catch (Exception ex) { Plugin.Log.LogError($"[NativeMenu] Preview sync prefix error: {ex}"); }
    }
}
