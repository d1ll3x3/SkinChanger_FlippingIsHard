using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

namespace CharmReplacer;

[BepInPlugin("com.dani.charmreplacer", "CharmReplacer", "1.5.1")]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    internal static string PluginDirectory;
    private Harmony _harmony;

    public override void Load()
    {
        Log = base.Log;
        PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // --- Config: remote skins backend ---
        var enableRemote = Config.Bind("RemoteSkins", "Enabled", true,
            "Automatically download skins/charms from the web backend.");
        var backendUrl = Config.Bind("RemoteSkins", "BackendBaseUrl",
            "https://raw.githubusercontent.com/d1ll3x3/charm-skins-data/main",
            "Backend base URL (raw GitHub base of the skins data repo). Empty = disabled.");
        RemoteSkinService.Enabled = enableRemote.Value && !string.IsNullOrWhiteSpace(backendUrl.Value);
        RemoteSkinService.BaseUrl = backendUrl.Value?.Trim();

        // --- Config: toggled from the "Custom" entries of the game's customization screen ---
        var customCover = Config.Bind("NativeMenu", "CustomCoverEnabled", true,
            "Show your custom skin (Skin.png) to yourself and to players with the mod. Players without the mod see the cover selected in the game's menu.");
        var customCharm = Config.Bind("NativeMenu", "CustomCharmEnabled", true,
            "Show your custom charm (hatbundle) to yourself and to players with the mod. Players without the mod see the charm selected in the game's menu.");
        SkinOverride.Init(customCover, customCharm);

        ClassInjector.RegisterTypeInIl2Cpp<CharmReplacerBehavior>();
        AddComponent<CharmReplacerBehavior>();

        InstallPatches();

        Log.LogInfo($"CharmReplacer Plugin loaded from: {PluginDirectory}");
    }

    private void InstallPatches()
    {
        try
        {
            _harmony = new Harmony("com.dani.charmreplacer");
            var applied = new List<string>();
            var skipped = new List<string>();

            // --- PlayerCharms ---
            var playerCharmsType = FindGameType("EHS.PlayerCharms") ?? FindGameType("PlayerCharms");
            if (playerCharmsType != null)
            {
                var method = playerCharmsType.GetMethod("ChangeCharmClient",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? playerCharmsType.GetMethod("ChangeCharmClient",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (method != null)
                {
                    _harmony.Patch(method,
                        postfix: new HarmonyMethod(typeof(CharmPatches), nameof(CharmPatches.ChangeCharmPostfix)));
                    applied.Add("ChangeCharmClient");
                }
                else
                {
                    skipped.Add("ChangeCharmClient");
                }
            }
            else
            {
                skipped.Add("PlayerCharms type");
            }

            // --- PlayerSkins ---
            var playerSkinsType = FindGameType("EHS.PlayerSkins") ?? FindGameType("PlayerSkins");
            if (playerSkinsType != null)
            {
                var methodSkin = playerSkinsType.GetMethod("ChangeSkinClient",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? playerSkinsType.GetMethod("ChangeSkinClient",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (methodSkin != null)
                {
                    _harmony.Patch(methodSkin,
                        postfix: new HarmonyMethod(typeof(CharmPatches), nameof(CharmPatches.ChangeSkinPostfix)));
                    applied.Add("ChangeSkinClient");
                }
                else
                {
                    skipped.Add("ChangeSkinClient");
                }

                var methodStart = playerSkinsType.GetMethod("Start",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? playerSkinsType.GetMethod("OnNetworkSpawn",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? playerSkinsType.GetMethod("OnStartClient",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (methodStart != null)
                {
                    _harmony.Patch(methodStart,
                        postfix: new HarmonyMethod(typeof(PlayerSkinsPatches), nameof(PlayerSkinsPatches.StartPostfix)));
                    applied.Add("PlayerSkins.Spawn");
                }
                // Not an error: just means this game version uses a different spawn hook.
            }
            else
            {
                skipped.Add("PlayerSkins type");
            }

            // --- v1.7+ unified skins/charms: EHS.Skins.PlayerSkins ---
            // The old PlayerCharms/PlayerSkins classes were deleted in the v1.7 rework and
            // merged into EHS.Skins.PlayerSkins, where a charm is just SkinCategory.Charm.
            // Both apply paths (local + network) drive the same re-scan trigger.
            var skinsPlayerType = FindGameType("EHS.Skins.PlayerSkins");
            if (skinsPlayerType != null)
            {
                // ApplyLocalSkin is overloaded ((SkinUISO) vs (SkinCategory, string)); pick the
                // (category, string) overload by shape to avoid AmbiguousMatchException.
                var applyLocal = FindMethodBySecondStringParam(skinsPlayerType, "ApplyLocalSkin");
                if (applyLocal != null)
                {
                    _harmony.Patch(applyLocal,
                        postfix: new HarmonyMethod(typeof(CharmPatches), nameof(CharmPatches.SkinAppliedPostfix)));
                    applied.Add("Skins.ApplyLocalSkin");
                }
                else
                {
                    skipped.Add("Skins.ApplyLocalSkin");
                }

                // Remote players: ApplySkinFromNetwork(SkinCategory, string) -> UniTaskVoid.
                var applyNetwork = FindMethodBySecondStringParam(skinsPlayerType, "ApplySkinFromNetwork");
                if (applyNetwork != null)
                {
                    _harmony.Patch(applyNetwork,
                        postfix: new HarmonyMethod(typeof(CharmPatches), nameof(CharmPatches.SkinAppliedPostfix)));
                    applied.Add("Skins.ApplySkinFromNetwork");
                }
                else
                {
                    skipped.Add("Skins.ApplySkinFromNetwork");
                }

                // Spawn hook: Init(PlayerRef) initializes a player's skins component (local or remote).
                var initMethod = skinsPlayerType.GetMethod("Init",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod != null)
                {
                    _harmony.Patch(initMethod,
                        postfix: new HarmonyMethod(typeof(PlayerSkinsPatches), nameof(PlayerSkinsPatches.StartPostfix)));
                    applied.Add("Skins.PlayerSkins.Init");
                }
                // Not an error if absent: older versions use PlayerCharms/PlayerSkins above.
            }

            // --- Native customization menu + per-viewer skin override ---
            try
            {
                InstallNativeMenuPatches(applied, skipped);
            }
            catch (Exception ex)
            {
                // Type load failures land here: the game no longer has the types this code binds to.
                Log.LogError($"[Patch] Native menu patches unavailable: {ex.Message}");
                skipped.Add("NativeMenu");
            }

            // --- DestroyedPhoneEffectHandler ---
            var destroyedPhoneType = FindGameType("EHS.DestroyedPhoneEffectHandler") ?? FindGameType("DestroyedPhoneEffectHandler");
            if (destroyedPhoneType != null)
            {
                var initMethod = destroyedPhoneType.GetMethod("Init",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod != null)
                {
                    _harmony.Patch(initMethod,
                        postfix: new HarmonyMethod(typeof(DestroyedPhoneEffectHandlerPatches), nameof(DestroyedPhoneEffectHandlerPatches.InitPostfix)));
                    applied.Add("DestroyedPhone.Init");
                }
                else
                {
                    skipped.Add("DestroyedPhone.Init");
                }

                var setToNormalMethod = destroyedPhoneType.GetMethod("SetToNormal",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (setToNormalMethod != null)
                {
                    _harmony.Patch(setToNormalMethod,
                        postfix: new HarmonyMethod(typeof(DestroyedPhoneEffectHandlerPatches), nameof(DestroyedPhoneEffectHandlerPatches.SetToNormalPostfix)));
                    applied.Add("DestroyedPhone.SetToNormal");
                }
                else
                {
                    skipped.Add("DestroyedPhone.SetToNormal");
                }

                var setToDestroyedMethod = destroyedPhoneType.GetMethod("SetToDestroyed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (setToDestroyedMethod != null)
                {
                    _harmony.Patch(setToDestroyedMethod,
                        postfix: new HarmonyMethod(typeof(DestroyedPhoneEffectHandlerPatches), nameof(DestroyedPhoneEffectHandlerPatches.SetToDestroyedPostfix)));
                    applied.Add("DestroyedPhone.SetToDestroyed");
                }
                else
                {
                    skipped.Add("DestroyedPhone.SetToDestroyed");
                }
            }
            else
            {
                skipped.Add("DestroyedPhoneEffectHandler type");
            }

            // --- Summary ---
            Log.LogInfo($"✓ {applied.Count} patches applied ({string.Join(", ", applied)})");

            foreach (var skip in skipped)
            {
                Log.LogWarning($"⚠ Patch skipped: {skip} (not available in this game version)");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"[Patch] Error: {ex}");
        }
    }

    // Binds statically to the release build's interop types; kept in its own method so a missing
    // type fails here (caught by the caller) instead of breaking the rest of InstallPatches.
    private void InstallNativeMenuPatches(List<string> applied, List<string> skipped)
    {
        void Patch(Type type, string name, Type[] args, string prefix = null, string postfix = null, Type patches = null)
        {
            var method = AccessTools.Method(type, name, args);
            if (method == null)
            {
                skipped.Add($"{type.Name}.{name}");
                return;
            }
            _harmony.Patch(method,
                prefix: prefix != null ? new HarmonyMethod(patches, prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(patches, postfix) : null);
            applied.Add($"{type.Name}.{name}");
        }

        var menu = typeof(NativeMenuPatches);
        var screen = typeof(EHS.CustomizationScreenController);
        Patch(screen, nameof(EHS.CustomizationScreenController.ApplyEntries), null,
            prefix: nameof(NativeMenuPatches.ApplyEntriesPrefix), patches: menu);
        Patch(screen, nameof(EHS.CustomizationScreenController.IsSelectedSkin), null,
            postfix: nameof(NativeMenuPatches.IsSelectedSkinPostfix), patches: menu);
        Patch(screen, nameof(EHS.CustomizationScreenController.CompareSkinsByRarity), null,
            postfix: nameof(NativeMenuPatches.CompareSkinsByRarityPostfix), patches: menu);
        Patch(screen, nameof(EHS.CustomizationScreenController.OnSkinSelected), null,
            prefix: nameof(NativeMenuPatches.OnSkinSelectedPrefix), patches: menu);

        var manager = typeof(EHS.Skins.SkinsManager);
        Patch(manager, nameof(EHS.Skins.SkinsManager.IsSkinUnlocked), new[] { typeof(string) },
            postfix: nameof(NativeMenuPatches.IsSkinUnlockedPostfix), patches: menu);
        Patch(manager, nameof(EHS.Skins.SkinsManager.IsNotSeenSkin), new[] { typeof(string) },
            postfix: nameof(NativeMenuPatches.IsNotSeenSkinPostfix), patches: menu);

        var overrides = typeof(SkinOverridePatches);
        Patch(typeof(EHS.Skins.PlayerSkins), nameof(EHS.Skins.PlayerSkins.ApplySkinFromNetwork), null,
            prefix: nameof(SkinOverridePatches.ApplySkinFromNetworkPrefix), patches: overrides);
        Patch(typeof(EHS.PlayerVisualsDummy), nameof(EHS.PlayerVisualsDummy.OnSkinSyncChanged), null,
            prefix: nameof(SkinOverridePatches.DummySkinSyncPrefix), patches: overrides);
    }

    // Finds an instance method by name whose signature is (something, string) — used to
    // disambiguate the ApplyLocalSkin / ApplySkinFromNetwork (SkinCategory, string) overloads
    // without hard-referencing the Il2Cpp enum type or risking AmbiguousMatchException.
    private static MethodInfo FindMethodBySecondStringParam(Type type, string name)
    {
        if (type == null) return null;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[1].ParameterType == typeof(string))
                return m;
        }
        return null;
    }

    private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

    internal static Type FindGameType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached))
            return cached;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null)
                {
                    _typeCache[fullName] = t;
                    return t;
                }
            }
            catch { }
        }
        _typeCache[fullName] = null; // cache "not found" to skip future scans
        return null;
    }
}
