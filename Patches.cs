using System;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CharmReplacer;

internal static class CharmPatches
{
    // ChangeCharmClient / ChangeSkinClient run on the game thread. Doing the
    // full sync rescan here stalls the frame (visible lag on paint-bucket use).
    // Invalidate the renderer cache and let the per-frame fast poll do the work.
    public static void ChangeCharmPostfix()
    {
        try
        {
            // Charm changes do not invalidate skin renderers. Only the charm
            // mesh cache (CharmedMeshFilterIds) needs a refresh, which is
            // handled by the fast poll + TryApplyCharmToMeshFilters.
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Charm] ChangeCharm postfix error: {ex}"); }
    }

    public static void ChangeSkinPostfix()
    {
        try
        {
            CharmState.SkinnedRendererIds.Clear();
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Skin] ChangeSkin postfix error: {ex}"); }
    }
}

internal static class PlayerSkinsPatches
{
    // Fires when a new player joins mid-game.
    public static void StartPostfix()
    {
        try
        {
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
                targetTex = CharmState.CustomSkinTexture;
                targetEmission = CharmState.CustomSkinEmissionTexture;
            }
            else
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

    // Sync paint needed: pieces must be skinned before the destroy animation plays.
    public static void InitPostfix(Il2CppObjectBase __instance)
    {
        try
        {
            CharmReplacerBehavior.Instance?.TriggerImmediateReplacement();
            PrePaintDestroyedPhone(__instance);
        }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] InitPostfix error: {ex}"); }
    }

    public static void SetToNormalPostfix(Il2CppObjectBase __instance)
    {
        try { PrePaintDestroyedPhone(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] SetToNormal postfix error: {ex}"); }
    }

    public static void SetToDestroyedPostfix(Il2CppObjectBase __instance)
    {
        try { PrePaintDestroyedPhone(__instance); }
        catch (Exception ex) { Plugin.Log.LogError($"[Patch] SetToDestroyed postfix error: {ex}"); }
    }
}
