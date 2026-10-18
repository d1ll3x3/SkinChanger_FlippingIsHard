using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EHS;
using EHS.Skins;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Decides who sees the custom cover/charm. The cover/charm picked in the game's own menu is what
    /// SkinSync carries, so players without the mod always see that one. Players with the mod see the
    /// custom one while its owner has it enabled: for them the base skin the custom asset is authored
    /// against is loaded in place of the synced key, and SkinSystem / CharmSystem paint over it.
    /// </summary>
    internal static class SkinOverride
    {
        // The enabled state rides the game's own skin sync, in SkinSync[SkinCategory.None]: the server
        // only checks Enum.IsDefined(category) and that the sender owns the player, so a host without
        // the mod relays it too. Clients without the mod find no handler for None and log one error.
        private const string FlagPrefix = "cupcharm:v1:";
        // Phone charm key in the release build. Used until a SkinsManager library can confirm it (the
        // main menu has none).
        private const string FallbackCharmBaseKey = "chm_phone";
        private const float TickInterval = 0.5f;
        private const float FlagResendDelay = 3f;
        // ApplyAsync is async; give a base reload this long to land before asking again.
        private const float EnsureBaseCooldown = 2f;

        private static ConfigEntry<bool> _coverEnabled;
        private static ConfigEntry<bool> _charmEnabled;

        private static float _nextTick;
        private static float _nextFlagSend;
        private static string _charmBaseKey;
        private static bool _charmBaseResolved;

        // Last evaluated "custom visible" state per (PlayerSkins, category): a true -> false transition
        // means a painted base instance has to be reloaded clean.
        private static readonly Dictionary<(IntPtr, SkinCategory), bool> _lastVisible =
            new Dictionary<(IntPtr, SkinCategory), bool>();
        private static readonly Dictionary<IntPtr, float> _lastEnsure = new Dictionary<IntPtr, float>();

        private static readonly SkinCategory[] s_categories = { SkinCategory.Cover, SkinCategory.Charm };

        public static void Init(ConfigEntry<bool> coverEnabled, ConfigEntry<bool> charmEnabled)
        {
            _coverEnabled = coverEnabled;
            _charmEnabled = charmEnabled;
        }

        public static bool HasLocalAsset(SkinCategory category) => category switch
        {
            SkinCategory.Cover => CharmState.CustomSkinTexture != null,
            SkinCategory.Charm => CharmState.CustomPrefab != null || CharmState.CustomMesh != null,
            _ => false,
        };

        public static bool IsLocalEnabled(SkinCategory category) => category switch
        {
            SkinCategory.Cover => _coverEnabled == null || _coverEnabled.Value,
            SkinCategory.Charm => _charmEnabled == null || _charmEnabled.Value,
            _ => false,
        };

        /// <summary>The local player shows its custom asset, to itself and to players with the mod.</summary>
        public static bool IsLocalActive(SkinCategory category) => IsLocalEnabled(category) && HasLocalAsset(category);

        public static void Toggle(SkinCategory category)
        {
            var entry = category == SkinCategory.Cover ? _coverEnabled : _charmEnabled;
            if (entry == null) return;
            entry.Value = !entry.Value;
            Plugin.Log.LogInfo($"[NativeMenu] Custom {category} {(entry.Value ? "enabled" : "disabled")}.");
            // Evaluate now rather than on the next tick so the preview and the player react on click.
            _nextTick = 0f;
            _nextFlagSend = 0f;
        }

        public static void ClearSceneState()
        {
            _lastVisible.Clear();
            _lastEnsure.Clear();
        }

        // ── Remote state ──────────────────────────────────────────────────────

        public static string ReadSync(PlayerNetworked networked, SkinCategory category)
        {
            var collection = networked?.SkinSync?.Collection;
            if (collection == null || !collection.ContainsKey(category)) return null;
            return collection[category];
        }

        // null = the player publishes no flag (no mod, or a mod version from before the flag).
        private static bool? RemoteFlag(PlayerNetworked networked, SkinCategory category)
        {
            string flag = ReadSync(networked, SkinCategory.None);
            if (flag == null || !flag.StartsWith(FlagPrefix, StringComparison.Ordinal)) return null;
            string token = (category == SkinCategory.Cover ? "cover" : "charm") + "=1";
            return Array.IndexOf(flag.Substring(FlagPrefix.Length).Split(';'), token) >= 0;
        }

        /// <summary>
        /// Whether a remote player's custom asset may be painted over their base visual. Players that
        /// publish no flag keep the behaviour from before the flag existed: the custom asset shows
        /// whenever they have the base skin equipped.
        /// </summary>
        public static bool AllowsRemoteCustom(GameObject player, SkinCategory category)
        {
            var networked = player != null ? player.GetComponent<PlayerNetworked>() : null;
            return RemoteFlag(networked, category) ?? true;
        }

        private static bool HasRemoteAsset(GameObject player, SkinCategory category)
        {
            string nick = PlayerIdentity.GetPlayerNickname(player);
            if (string.IsNullOrEmpty(nick)) return false;
            return category == SkinCategory.Cover
                ? CharmState.PlayerBaseTextures.ContainsKey(nick)
                : CharmState.PlayerCharmPrefabs.ContainsKey(nick) || CharmState.PlayerCharmMeshes.ContainsKey(nick);
        }

        private static bool CustomVisible(PlayerNetworked networked, SkinCategory category)
        {
            if (networked.IsOwner) return IsLocalActive(category);
            return RemoteFlag(networked, category) ?? true;
        }

        private static bool ShouldForceBase(PlayerNetworked networked, SkinCategory category)
        {
            if (networked == null) return false;
            if (networked.IsOwner) return IsLocalActive(category);
            return RemoteFlag(networked, category) == true && HasRemoteAsset(networked.gameObject, category);
        }

        // ── Base skin ─────────────────────────────────────────────────────────

        /// <summary>
        /// Key loaded instead of the synced one. "" makes PlayerSkins fall back to the handler's default
        /// (M_Skin_Cover_Default for the cover). The custom charm replaces the phone charm mesh
        /// (CharmSystem.IsCharmTarget), so the Phone charm is used when the skin library has one.
        /// </summary>
        internal static string BaseKey(SkinCategory category, SkinsManager manager)
        {
            if (category != SkinCategory.Charm) return "";
            if (!_charmBaseResolved) ResolveCharmBaseKey(manager);
            return _charmBaseResolved ? _charmBaseKey ?? "" : FallbackCharmBaseKey;
        }

        private static void ResolveCharmBaseKey(SkinsManager manager)
        {
            var refs = manager?.skinAddressableLibrary?.allLoadedSkinRefs;
            if (refs == null || refs.Count == 0) return; // library not loaded yet; retry next call

            _charmBaseResolved = true;
            for (int i = 0; i < refs.Count; i++)
            {
                var skin = refs[i];
                if (skin == null || skin.SkinCategory != SkinCategory.Charm) continue;
                string key = skin.AddressableKey ?? "";
                if (key.IndexOf("phone", StringComparison.OrdinalIgnoreCase) < 0
                    && (skin.name ?? "").IndexOf("phone", StringComparison.OrdinalIgnoreCase) < 0) continue;
                _charmBaseKey = key;
                Plugin.Log.LogInfo($"[NativeMenu] ✓ Custom charm base: '{key}'");
                return;
            }
            Plugin.Log.LogWarning("[NativeMenu] No Phone charm in the skin library; the custom charm uses the default charm as base.");
        }

        internal static bool IsShowingBase(SkinPrefabHandler handler, string baseKey)
        {
            string expected = string.IsNullOrEmpty(baseKey) ? handler.DefaultSkinUISO?.AddressableKey : baseKey;
            return !string.IsNullOrEmpty(expected) && handler.CurrentSkinKey == expected;
        }

        internal static SkinPrefabHandler GetHandler(Il2CppSystem.Collections.Generic.Dictionary<SkinCategory, SkinPrefabHandler> map,
            SkinCategory category)
        {
            if (map == null || !map.ContainsKey(category)) return null;
            return map[category];
        }

        internal static bool EnsureCooldownElapsed(SkinPrefabHandler handler)
        {
            if (_lastEnsure.TryGetValue(handler.Pointer, out float t) && Time.time - t < EnsureBaseCooldown) return false;
            _lastEnsure[handler.Pointer] = Time.time;
            return true;
        }

        // ── Patch entry points ────────────────────────────────────────────────

        /// <summary>PlayerSkins.ApplySkinFromNetwork prefix (also reached by ApplyLocalSkin). False skips it.</summary>
        public static bool BeforeApplySkin(PlayerSkins skins, SkinCategory category, ref string skinUISOKey)
        {
            // The game has no handler for None (it would only log an error); it is our flag. A changed
            // flag is picked up by Tick, which reads SkinSync directly.
            if (category == SkinCategory.None) return false;

            if ((category == SkinCategory.Cover || category == SkinCategory.Charm)
                && ShouldForceBase(skins?.playerRef?.playerNetworked, category))
                skinUISOKey = BaseKey(category, skins.skinsManager);
            return true;
        }

        // ── Per-frame work ────────────────────────────────────────────────────

        public static void Tick()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;

            try
            {
                var allSkins = UnityEngine.Object.FindObjectsOfType<PlayerSkins>();
                if (allSkins != null)
                {
                    foreach (var skins in allSkins)
                    {
                        var networked = skins?.playerRef?.playerNetworked;
                        if (networked == null || !skins.isInitialized) continue;
                        if (networked.IsOwner) PushLocalFlag(networked);
                        foreach (var category in s_categories)
                            EvaluatePlayer(skins, networked, category);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NativeMenu] Player override tick error: {ex.Message}");
            }
        }

        private static string BuildLocalFlag()
        {
            if (!HasLocalAsset(SkinCategory.Cover) && !HasLocalAsset(SkinCategory.Charm)) return "";
            return FlagPrefix
                + "cover=" + (IsLocalActive(SkinCategory.Cover) ? 1 : 0)
                + ";charm=" + (IsLocalActive(SkinCategory.Charm) ? 1 : 0);
        }

        private static void PushLocalFlag(PlayerNetworked networked)
        {
            if (!networked.IsSpawned || !networked.IsClientInitialized) return;
            string desired = BuildLocalFlag();
            string current = ReadSync(networked, SkinCategory.None) ?? "";
            if (desired == current || Time.time < _nextFlagSend) return;

            _nextFlagSend = Time.time + FlagResendDelay;
            // An empty key makes the server remove the entry.
            networked.RequestSkinChangeServerRpc(SkinCategory.None, desired);
            Plugin.Log.LogInfo($"[NativeMenu] Published custom state '{(desired.Length == 0 ? "<none>" : desired)}'.");
        }

        private static void EvaluatePlayer(PlayerSkins skins, PlayerNetworked networked, SkinCategory category)
        {
            var handler = GetHandler(skins.skinsHandlerMap, category);
            if (handler == null) return;

            var key = (skins.Pointer, category);
            bool visible = CustomVisible(networked, category);
            bool wasVisible = _lastVisible.TryGetValue(key, out bool last) && last;
            if (wasVisible && !visible)
                RestorePlayer(skins, networked, handler, category);
            // A base skin that is already loaded triggers no apply (and so no repaint scan) of its own.
            else if (!wasVisible && visible)
                CharmReplacerBehavior.Instance?.RequestGameFastPoll();
            _lastVisible[key] = visible;

            if (!ShouldForceBase(networked, category) || handler.IsLoading) return;
            string baseKey = BaseKey(category, skins.skinsManager);
            if (IsShowingBase(handler, baseKey) || !EnsureCooldownElapsed(handler)) return;

            Plugin.Log.LogDebug($"[NativeMenu] Loading base {category} for '{PlayerIdentity.GetPlayerNickname(networked.gameObject)}'.");
            skins.ApplySkinFromNetwork(category, baseKey);
        }

        // The base instance carries our painted material clones / custom charm; reload it from scratch
        // (Clear defeats SkinPrefabHandler.IsAlreadyApplied when the synced key is the base one).
        private static void RestorePlayer(PlayerSkins skins, PlayerNetworked networked, SkinPrefabHandler handler, SkinCategory category)
        {
            string key = networked.IsOwner
                ? skins.skinsManager?.GetEquippedCategorySkin(category)
                : ReadSync(networked, category);
            CharmSystem.RemoveCustomCharms(handler.socket);
            handler.Clear();
            skins.ApplySkinFromNetwork(category, key ?? "");
            Plugin.Log.LogInfo($"[NativeMenu] Restored {category} '{key}' on '{PlayerIdentity.GetPlayerNickname(networked.gameObject)}'.");
        }
    }
}
