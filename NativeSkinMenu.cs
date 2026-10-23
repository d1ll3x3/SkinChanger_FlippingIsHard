using System;
using EHS;
using EHS.Skins;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// The mod's entries in the customization screen, all in its own tab (ModMenuTab). Each is a runtime
    /// SkinUISO that never reaches SkinsManager or the network: "Custom" Cover/Charm toggle the custom
    /// asset (SkinOverride) while the native entry that stays selected is what players without the mod
    /// see; then the CoverExtras entries: cover colour and glow colour step through a palette on each
    /// click, Jump Glow toggles the glow.
    /// </summary>
    internal static class NativeSkinMenu
    {
        private const string CoverKey = "cupcharm.custom.cover";
        private const string CharmKey = "cupcharm.custom.charm";
        private const string GlowKey = "cupcharm.extra.jumpglow";
        private const string CoverColorKey = "cupcharm.extra.covercolor";
        private const string GlowColorKey = "cupcharm.extra.glowcolor";
        // Order in the tab.
        private static readonly string[] s_entryOrder = { CoverKey, CharmKey, CoverColorKey, GlowKey, GlowColorKey };

        private static SkinUISO _coverEntry;
        private static SkinUISO _charmEntry;
        private static SkinUISO _glowEntry;
        private static SkinUISO _coverColorEntry;
        private static SkinUISO _glowColorEntry;
        private static CustomizationScreenController _controller;

        public static bool IsCustomKey(string key) =>
            key == CoverKey || key == CharmKey || key == GlowKey || key == CoverColorKey || key == GlowColorKey;

        private static bool IsGlow(SkinUISO skin) => skin != null && skin.addressableKey == GlowKey;

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

            if (!ModMenuTab.IsModCategory(shown)) return;

            foreach (var category in new[] { SkinCategory.Cover, SkinCategory.Charm })
            {
                if (!SkinOverride.HasLocalAsset(category)) continue;
                skins.Add(GetOrCreateEntry(category));
                IconStudio.Request(category);
            }
            skins.Add(GetOrCreateColorEntry(ref _coverColorEntry, CoverColorKey, CoverExtras.CoverColorSwatch()));
            skins.Add(GetOrCreateGlowEntry());
            skins.Add(GetOrCreateColorEntry(ref _glowColorEntry, GlowColorKey, CoverExtras.GlowColorSwatch()));
        }

        /// <summary>Text shown instead of the entry's icon, or null to keep the icon.</summary>
        public static string LabelFor(SkinUISO skin) => IsGlow(skin) ? "Jump" : null;

        private static SkinUISO GetOrCreateColorEntry(ref SkinUISO entry, string key, Sprite swatch)
        {
            if (entry == null)
            {
                entry = ScriptableObject.CreateInstance(Il2CppType.Of<SkinUISO>()).Cast<SkinUISO>();
                entry.name = "SkinUI_CharmReplacer_" + key;
                entry.hideFlags = HideFlags.DontUnloadUnusedAsset;
                entry.addressableKey = key;
                entry.skinCategory = SkinCategory.Cover;
                entry.skinRarity = SkinRarity.Mythical;
            }
            entry.sprite = swatch;
            return entry;
        }

        private static SkinUISO GetOrCreateGlowEntry()
        {
            if (_glowEntry == null)
            {
                _glowEntry = ScriptableObject.CreateInstance(Il2CppType.Of<SkinUISO>()).Cast<SkinUISO>();
                _glowEntry.name = "SkinUI_CharmReplacer_JumpGlow";
                _glowEntry.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _glowEntry.addressableKey = GlowKey;
                _glowEntry.skinCategory = SkinCategory.Cover;
                _glowEntry.skinRarity = SkinRarity.Mythical;
            }
            // Shown only if the "Jump" label can't be made (no font found in the screen).
            if (_glowEntry.sprite == null) _glowEntry.sprite = BaseSkinSprite(SkinCategory.Cover);
            return _glowEntry;
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
        public static void SetIcon(SkinCategory category, Sprite sprite) =>
            SetEntryIcon(category == SkinCategory.Cover ? _coverEntry : _charmEntry, sprite);

        private static void SetEntryIcon(SkinUISO entry, Sprite sprite)
        {
            if (entry == null || sprite == null) return;
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
            if (IsGlow(skin) || skin.addressableKey == CoverColorKey || skin.addressableKey == GlowColorKey)
            {
                if (IsGlow(skin)) CoverExtras.ToggleGlow();
                else if (skin.addressableKey == CoverColorKey) CoverExtras.CycleCoverColor();
                else CoverExtras.CycleGlowColor();
                SetEntryIcon(_coverColorEntry, CoverExtras.CoverColorSwatch());
                SetEntryIcon(_glowColorEntry, CoverExtras.GlowColorSwatch());
                SkinOverride.RequestRefresh();
                controller.RefreshVisibleEntryStates();
                return false;
            }
            SkinOverride.Toggle(skin.skinCategory);
            LocalVisuals.OnCustomToggled(skin.skinCategory);
            controller.RefreshVisibleEntryStates();
            return false;
        }

        public static bool IsSelected(SkinUISO skin) => skin.addressableKey switch
        {
            GlowKey or GlowColorKey => CoverExtras.GlowEnabled,
            CoverColorKey => CoverExtras.CoverColored,
            _ => SkinOverride.IsLocalActive(skin.skinCategory),
        };

        /// <summary>CompareSkinsByRarity postfix: our entries first, in s_entryOrder.</summary>
        public static int Compare(SkinUISO left, SkinUISO right, int nativeResult)
        {
            bool leftCustom = IsCustom(left);
            bool rightCustom = IsCustom(right);
            if (leftCustom && rightCustom)
                return Array.IndexOf(s_entryOrder, left.addressableKey).CompareTo(Array.IndexOf(s_entryOrder, right.addressableKey));
            if (leftCustom == rightCustom) return nativeResult;
            return leftCustom ? -1 : 1;
        }
    }
}
