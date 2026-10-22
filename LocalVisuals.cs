using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Shows the local custom cover/charm on the visuals that only mirror the local player: the
    /// customization screen preview (PlayerVisualsDummy) and the rotatable phone of the main menu
    /// (EquippedPlayerSkins). Both load their skins straight into SkinPrefabHandlers, inside async code
    /// with the key lookups inlined, so instead of intercepting them the base skin is reloaded into the
    /// handler once they settle and painted over.
    /// </summary>
    internal static class LocalVisuals
    {
        private const float TickInterval = 0.1f;
        private const float ScanInterval = 1f;

        private static float _nextTick;
        private static float _nextScan;
        private static PlayerVisualsDummy[] _dummies = Array.Empty<PlayerVisualsDummy>();
        private static EquippedPlayerSkins[] _menuVisuals = Array.Empty<EquippedPlayerSkins>();

        // Per (visual, category): whether the custom asset was showing, so a true -> false transition
        // reloads the painted base instance clean.
        private static readonly Dictionary<(IntPtr, SkinCategory), bool> _lastVisible =
            new Dictionary<(IntPtr, SkinCategory), bool>();
        // Charm bases the main menu library has been asked to load (it only preloads equipped keys).
        private static readonly HashSet<string> _requestedLoads = new HashSet<string>(StringComparer.Ordinal);

        // While the customization screen is open, clicking a native skin previews that skin (what
        // players without the mod will see) even if the custom one is enabled; clicking Custom shows it.
        private static readonly HashSet<SkinCategory> _previewNative = new HashSet<SkinCategory>();
        private static bool _screenWasActive;

        private static readonly SkinCategory[] s_categories = { SkinCategory.Cover, SkinCategory.Charm };

        /// <summary>The customization preview while its screen is open, else null.</summary>
        public static PlayerVisualsDummy ActiveCustomizationDummy
        {
            get
            {
                foreach (var dummy in _dummies)
                {
                    if (dummy != null && dummy.controller != null && dummy.controller.IsActive) return dummy;
                }
                return null;
            }
        }

        public static void OnNativeSkinSelected(SkinCategory category)
        {
            if (_previewNative.Add(category)) _nextTick = 0f;
        }

        public static void OnCustomToggled(SkinCategory category)
        {
            _previewNative.Remove(category);
            _nextTick = 0f;
        }

        public static void ClearSceneState()
        {
            _lastVisible.Clear();
            _requestedLoads.Clear();
            _previewNative.Clear();
            _dummies = Array.Empty<PlayerVisualsDummy>();
            _menuVisuals = Array.Empty<EquippedPlayerSkins>();
            _nextScan = 0f;
        }

        private static bool ShowsCustom(PlayerVisualsDummy dummy, SkinCategory category) =>
            SkinOverride.IsLocalActive(category) && !(dummy.IsCustomizationDummy && _previewNative.Contains(category));

        /// <summary>PlayerVisualsDummy.OnSkinSyncChanged prefix: load the base skin straight away instead of the synced one.</summary>
        public static void BeforeDummySync(PlayerVisualsDummy dummy, SkinCategory category, ref string skinUISOKey)
        {
            if ((category != SkinCategory.Cover && category != SkinCategory.Charm) || !ShowsCustom(dummy, category)) return;
            var handler = SkinOverride.GetHandler(dummy.skinsHandlerMap, category);
            // The dummy treats "" as "no skin"; an explicit key falls back to the handler's default
            // when the library does not have it.
            string key = SkinOverride.BaseKey(category, dummy.skinsManager);
            skinUISOKey = !string.IsNullOrEmpty(key) ? key : handler?.DefaultSkinUISO?.AddressableKey ?? skinUISOKey;
        }

        public static void Tick()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + ScanInterval;
                _dummies = UnityEngine.Object.FindObjectsOfType<PlayerVisualsDummy>() ?? Array.Empty<PlayerVisualsDummy>();
                _menuVisuals = UnityEngine.Object.FindObjectsOfType<EquippedPlayerSkins>() ?? Array.Empty<EquippedPlayerSkins>();
            }

            bool screenActive = ActiveCustomizationDummy != null;
            if (screenActive && !_screenWasActive) _previewNative.Clear();
            _screenWasActive = screenActive;

            foreach (var dummy in _dummies)
            {
                if (dummy == null || dummy.visualsRoot == null || !dummy.visualsRoot.gameObject.activeInHierarchy) continue;
                foreach (var category in s_categories)
                {
                    try { TickDummy(dummy, category); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[NativeMenu] Preview {category} error: {ex.Message}"); }
                }
                CapFlip.EvaluateLocal(SkinOverride.GetHandler(dummy.skinsHandlerMap, SkinCategory.AccessoryHead));
            }

            foreach (var visual in _menuVisuals)
            {
                if (visual == null || !visual.isActiveAndEnabled) continue;
                foreach (var category in s_categories)
                {
                    try { TickMenuVisual(visual, category); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[NativeMenu] Menu phone {category} error: {ex.Message}"); }
                }
                CapFlip.EvaluateLocal(FindHandler(visual, SkinCategory.AccessoryHead));
            }
        }

        private static void TickDummy(PlayerVisualsDummy dummy, SkinCategory category)
        {
            var handler = SkinOverride.GetHandler(dummy.skinsHandlerMap, category);
            if (handler == null) return;
            var manager = dummy.skinsManager;

            SkinUISO Resolve(string key) =>
                !string.IsNullOrEmpty(key) && manager != null && manager.TryGetLoaded(key, out var skin) ? skin : null;

            string equipped = manager?.GetEquippedCategorySkin(category);
            Drive(dummy.Pointer, handler, category, ShowsCustom(dummy, category), manager, Resolve, equipped);
        }

        internal static SkinPrefabHandler FindHandler(EquippedPlayerSkins visual, SkinCategory category)
        {
            var handlers = visual.handlers;
            if (handlers != null)
                for (int i = 0; i < handlers.Count; i++)
                    if (handlers[i] != null && handlers[i].Category == category) return handlers[i];
            return null;
        }

        private static void TickMenuVisual(EquippedPlayerSkins visual, SkinCategory category)
        {
            var handler = FindHandler(visual, category);
            if (handler == null) return;

            var library = visual.skinLibrary;
            SkinUISO Resolve(string key)
            {
                if (string.IsNullOrEmpty(key) || library == null) return null;
                if (library.TryGetLoaded(key, out var skin, false)) return skin;
                if (_requestedLoads.Add(key)) library.LoadOneAsync(key);
                return null;
            }

            Drive(visual.Pointer, handler, category, SkinOverride.IsLocalActive(category), null, Resolve,
                EquippedKey(category));
        }

        private static string EquippedKey(SkinCategory category)
        {
            var entries = SkinsManager.GetEquippedSkinEntries();
            if (entries == null) return null;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i] != null && entries[i].Category == category) return entries[i].SkinUISOKey;
            return null;
        }

        private static void Drive(IntPtr owner, SkinPrefabHandler handler, SkinCategory category, bool showCustom,
            SkinsManager manager, Func<string, SkinUISO> resolve, string equippedKey)
        {
            // Wait for any load (ours or the game's) before acting, keeping the transition pending.
            if (handler.IsLoading) return;
            var key = (owner, category);
            bool wasCustom = _lastVisible.TryGetValue(key, out bool last) && last;
            _lastVisible[key] = showCustom;

            if (!showCustom)
            {
                if (!wasCustom) return;
                // Leave the base instance (painted / carrying the custom charm) for a clean native one.
                CharmSystem.RemoveCustomCharms(handler.socket);
                handler.Clear();
                handler.ApplyAsync(resolve(equippedKey) ?? handler.DefaultSkinUISO);
                return;
            }

            string baseKey = SkinOverride.BaseKey(category, manager);
            if (!SkinOverride.IsShowingBase(handler, baseKey))
            {
                var baseSkin = string.IsNullOrEmpty(baseKey) ? handler.DefaultSkinUISO : resolve(baseKey);
                if (baseSkin != null && SkinOverride.EnsureCooldownElapsed(handler)) handler.ApplyAsync(baseSkin);
                return;
            }

            if (!handler.TryGetInstance(out var instance) || instance == null) return;
            if (category == SkinCategory.Cover)
                SkinSystem.PaintPhoneCovers(instance.gameObject, CharmState.CustomSkinTexture, CharmState.CustomSkinEmissionTexture);
            else
                CharmSystem.ApplyLocalCharmTo(instance.gameObject);
        }
    }
}
