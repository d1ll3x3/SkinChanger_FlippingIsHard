using EHS;
using EHS.Skins;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Adds a "Custom" entry to the Cover and Charm tabs of the game's customization screen. The entry
    /// is a runtime SkinUISO that never reaches SkinsManager or the network: clicking it toggles the
    /// custom asset (SkinOverride), while the native entry that stays selected is what players without
    /// the mod see.
    /// </summary>
    internal static class NativeSkinMenu
    {
        private const string CoverKey = "cupcharm.custom.cover";
        private const string CharmKey = "cupcharm.custom.charm";

        private static SkinUISO _coverEntry;
        private static SkinUISO _charmEntry;
        private static CustomizationScreenController _controller;

        public static bool IsCustomKey(string key) => key == CoverKey || key == CharmKey;

        public static bool IsCustom(SkinUISO skin) => skin != null && IsCustomKey(skin.addressableKey);

        /// <summary>
        /// ApplyEntries prefix. ShowCategory hands it a fresh list already filtered to the open tab, so
        /// ours is added there. (ShowCategory itself can't be patched: its Nullable&lt;SkinCategory&gt; is
        /// passed by value natively but as a pointer by Il2CppInterop, which crashes the detour.)
        /// </summary>
        public static void AddEntries(CustomizationScreenController controller, Il2CppSystem.Collections.Generic.List<SkinUISO> skins)
        {
            _controller = controller;
            if (skins == null) return;

            // No active tab = ShowCategory(null), which lists every category.
            var tab = controller.activeTab;
            var shown = tab == null ? SkinCategory.None
                : tab.OverrideCategory != SkinCategory.None ? tab.OverrideCategory : tab.Category;

            foreach (var category in new[] { SkinCategory.Cover, SkinCategory.Charm })
            {
                if (shown != SkinCategory.None && shown != category) continue;
                if (!SkinOverride.HasLocalAsset(category)) continue;
                skins.Insert(0, GetOrCreateEntry(category));
                IconStudio.Request(category);
            }
        }

        private static SkinUISO GetOrCreateEntry(SkinCategory category)
        {
            var existing = category == SkinCategory.Cover ? _coverEntry : _charmEntry;
            if (existing != null) return existing;

            var entry = ScriptableObject.CreateInstance(Il2CppType.Of<SkinUISO>()).Cast<SkinUISO>();
            entry.name = "SkinUI_CharmReplacer_" + category;
            entry.hideFlags = HideFlags.DontUnloadUnusedAsset;
            entry.addressableKey = category == SkinCategory.Cover ? CoverKey : CharmKey;
            entry.skinCategory = category;
            // Any rarity but None (the screen logs an error) or Default (hidden from tab availability).
            entry.skinRarity = SkinRarity.Mythical;
            // The base skin's own icon stands in until IconStudio has rendered ours.
            entry.sprite = IconStudio.GetSprite(category) ?? BaseSkinSprite(category);

            if (category == SkinCategory.Cover) _coverEntry = entry;
            else _charmEntry = entry;
            return entry;
        }

        private static Sprite BaseSkinSprite(SkinCategory category)
        {
            var map = LocalVisuals.ActiveCustomizationDummy?.skinsHandlerMap;
            if (map == null || !map.ContainsKey(category)) return null;
            return map[category]?.DefaultSkinUISO?.Sprite;
        }

        /// <summary>Called by IconStudio when an icon finishes rendering.</summary>
        public static void SetIcon(SkinCategory category, Sprite sprite)
        {
            var entry = category == SkinCategory.Cover ? _coverEntry : _charmEntry;
            if (entry == null) return;
            entry.sprite = sprite;

            // Buttons already showing the entry copied the old sprite in SkinEntryButton.Setup.
            var pool = _controller != null ? _controller.pool : null;
            if (pool == null) return;
            for (int i = 0; i < pool.Count; i++)
            {
                var button = pool[i];
                if (button == null || button.skin == null || button.skin.Pointer != entry.Pointer) continue;
                if (button.iconImage != null) button.iconImage.sprite = sprite;
            }
        }

        /// <summary>OnSkinSelected prefix. Returns false when the click was on our entry.</summary>
        public static bool OnSkinSelected(CustomizationScreenController controller, SkinUISO skin)
        {
            if (!IsCustom(skin))
            {
                if (skin != null) LocalVisuals.OnNativeSkinSelected(skin.skinCategory);
                return true;
            }
            SkinOverride.Toggle(skin.skinCategory);
            LocalVisuals.OnCustomToggled(skin.skinCategory);
            controller.RefreshVisibleEntryStates();
            return false;
        }

        public static bool IsSelected(SkinUISO skin) => SkinOverride.IsLocalActive(skin.skinCategory);

        /// <summary>CompareSkinsByRarity postfix: keep our entry first in its tab.</summary>
        public static int Compare(SkinUISO left, SkinUISO right, int nativeResult)
        {
            bool leftCustom = IsCustom(left);
            bool rightCustom = IsCustom(right);
            if (leftCustom == rightCustom) return nativeResult;
            return leftCustom ? -1 : 1;
        }
    }
}
