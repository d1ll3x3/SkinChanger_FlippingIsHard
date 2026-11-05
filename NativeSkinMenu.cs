using System;
using System.Collections.Generic;
using System.Linq;
using EHS;
using EHS.Skins;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// The mod's entries in the customization screen, all in its own tab (ModMenuTab). Each is a runtime
    /// SkinUISO that never reaches SkinsManager or the network: "Skin" / "Charm" / "Face" toggle the custom
    /// asset (SkinOverride) while the native entry that stays selected is what players without the mod
    /// see; "Jump" toggles the jump glow (CoverExtras); "Model", "Jump style" and the colours open ModPanel.
    /// Each entry carries a caption (ModMenuTab), and is listed only where it means something: the model
    /// with the custom skin on, the glow's style and colour with the glow on, the tint for a cover that
    /// takes one and the Wizard's colours for the Wizard.
    /// </summary>
    internal static class NativeSkinMenu
    {
        private const string CoverKey = "cupcharm.custom.cover";
        private const string ModelKey = "cupcharm.custom.model";
        private const string CharmKey = "cupcharm.custom.charm";
        private const string FaceKey = "cupcharm.custom.face";
        private const string GlowKey = "cupcharm.extra.jumpglow";
        private const string StyleKey = "cupcharm.extra.jumpstyle";
        private const string ColorKeyPrefix = "cupcharm.color.";

        private sealed class EntryInfo
        {
            public string Key;
            public string Caption;
            // Shown instead of an icon (the style entry's is the current style).
            public string Label;
        }

        // In tab order: the custom assets, the jump glow, then the colours.
        private static readonly EntryInfo[] s_entries =
        {
            new EntryInfo { Key = CoverKey, Caption = "Skin" },
            new EntryInfo { Key = ModelKey, Caption = "Model" },
            new EntryInfo { Key = CharmKey, Caption = "Charm" },
            new EntryInfo { Key = FaceKey, Caption = "Face" },
            new EntryInfo { Key = GlowKey, Label = "Jump" },
            new EntryInfo { Key = StyleKey, Caption = "Jump style" },
            new EntryInfo { Key = ColorKey(CoverExtras.ColorTarget.Glow), Caption = "Jump colour" },
            new EntryInfo { Key = ColorKey(CoverExtras.ColorTarget.Cover), Caption = "Colour" },
            new EntryInfo { Key = ColorKey(CoverExtras.ColorTarget.WizardTop), Caption = "Wizard top" },
            new EntryInfo { Key = ColorKey(CoverExtras.ColorTarget.WizardBottom), Caption = "Wizard bottom" },
            new EntryInfo { Key = ColorKey(CoverExtras.ColorTarget.WizardSwirl), Caption = "Wizard swirl" },
        };

        private static readonly Dictionary<string, SkinUISO> _entries = new Dictionary<string, SkinUISO>(StringComparer.Ordinal);
        private static CustomizationScreenController _controller;
        // Keys listed the last time the tab was filled; a change in them means listing it again.
        private static readonly List<string> _listed = new List<string>();

        private static string ColorKey(CoverExtras.ColorTarget target) => ColorKeyPrefix + target;

        private static bool TryGetColorTarget(string key, out CoverExtras.ColorTarget target)
        {
            target = default;
            return key != null && key.StartsWith(ColorKeyPrefix, StringComparison.Ordinal)
                && Enum.TryParse(key.Substring(ColorKeyPrefix.Length), out target);
        }

        private static EntryInfo Info(string key) => Array.Find(s_entries, e => e.Key == key);

        public static bool IsCustomKey(string key) => Info(key) != null;

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

            _listed.Clear();
            if (!ModMenuTab.IsModCategory(shown))
            {
                ModPanel.Close();
                return;
            }

            _listed.AddRange(AvailableKeys());
            foreach (var key in _listed)
            {
                skins.Add(GetOrCreateEntry(key));
                if (key == CoverKey) IconStudio.Request(SkinCategory.Cover);
                else if (key == CharmKey) IconStudio.Request(SkinCategory.Charm);
            }
        }

        private static List<string> AvailableKeys()
        {
            string cover = ShownCoverKey();
            var keys = new List<string>();
            foreach (var info in s_entries)
                if (IsAvailable(info.Key, cover)) keys.Add(info.Key);
            return keys;
        }

        private static bool IsAvailable(string key, string shownCover)
        {
            bool customCover = SkinOverride.IsLocalActive(SkinCategory.Cover);
            if (TryGetColorTarget(key, out var target))
            {
                return target switch
                {
                    CoverExtras.ColorTarget.Glow => CoverExtras.GlowEnabled,
                    // A custom skin is painted as drawn, untinted; the Wizard has its own three colours.
                    CoverExtras.ColorTarget.Cover => !customCover && !CoverExtras.IsWizard(shownCover),
                    _ => !customCover && CoverExtras.IsWizard(shownCover),
                };
            }
            return key switch
            {
                CoverKey => SkinOverride.HasLocalAsset(SkinCategory.Cover),
                ModelKey => customCover,
                CharmKey => SkinOverride.HasLocalAsset(SkinCategory.Charm),
                FaceKey => CustomFace.HasAsset,
                StyleKey => CoverExtras.GlowEnabled,
                _ => true,
            };
        }

        /// <summary>The cover the local phone shows: the custom skin's model while it is on, else the equipped one.</summary>
        private static string ShownCoverKey()
        {
            var manager = _controller != null ? _controller.skinsManager : null;
            if (SkinOverride.IsLocalActive(SkinCategory.Cover)) return SkinOverride.BaseKey(SkinCategory.Cover, manager);
            return manager != null ? manager.GetEquippedCategorySkin(SkinCategory.Cover) ?? "" : "";
        }

        /// <summary>Text shown instead of the entry's icon, or null to keep the icon.</summary>
        public static string LabelFor(SkinUISO skin)
        {
            if (skin == null) return null;
            if (skin.addressableKey == StyleKey) return CoverExtras.StyleName(CoverExtras.Style);
            return Info(skin.addressableKey)?.Label;
        }

        /// <summary>Caption along the bottom of the entry, or null for none.</summary>
        public static string CaptionFor(SkinUISO skin) => skin != null ? Info(skin.addressableKey)?.Caption : null;

        private static SkinUISO GetOrCreateEntry(string key)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry == null)
            {
                entry = ScriptableObject.CreateInstance(Il2CppType.Of<SkinUISO>()).Cast<SkinUISO>();
                entry.name = "SkinUI_CharmReplacer_" + key;
                entry.hideFlags = HideFlags.DontUnloadUnusedAsset;
                entry.addressableKey = key;
                entry.skinCategory = key switch
                {
                    CharmKey => SkinCategory.Charm,
                    FaceKey => SkinCategory.Face,
                    _ => SkinCategory.Cover,
                };
                // Any rarity but None (the screen logs an error) or Default (hidden from tab availability).
                entry.skinRarity = SkinRarity.Mythical;
                _entries[key] = entry;
            }
            // Text entries keep a sprite only for when no font was found for their label.
            entry.sprite = IconFor(key) ?? entry.sprite ?? BaseSkinSprite(SkinCategory.Cover);
            return entry;
        }

        private static Sprite IconFor(string key)
        {
            if (TryGetColorTarget(key, out var target)) return CoverExtras.ColorSwatch(target);
            return key switch
            {
                // The base skin's own icon stands in until IconStudio has rendered ours.
                CoverKey => IconStudio.GetSprite(SkinCategory.Cover) ?? BaseSkinSprite(SkinCategory.Cover),
                CharmKey => IconStudio.GetSprite(SkinCategory.Charm) ?? BaseSkinSprite(SkinCategory.Charm),
                FaceKey => CustomFace.Icon ?? BaseSkinSprite(SkinCategory.Face),
                ModelKey => ModelSprite(SkinOverride.CoverModel),
                _ => null,
            };
        }

        /// <summary>Icon of a cover by key; "" is the default cover.</summary>
        internal static Sprite ModelSprite(string key)
        {
            if (string.IsNullOrEmpty(key) || _controller == null) return BaseSkinSprite(SkinCategory.Cover);
            var skins = _controller.allSkins;
            if (skins != null)
                for (int i = 0; i < skins.Count; i++)
                    if (skins[i] != null && skins[i].addressableKey == key) return skins[i].sprite;
            return BaseSkinSprite(SkinCategory.Cover);
        }

        internal static Sprite BaseSkinSprite(SkinCategory category)
        {
            var map = LocalVisuals.ActiveCustomizationDummy?.skinsHandlerMap;
            if (map == null || !map.ContainsKey(category)) return null;
            return map[category]?.DefaultSkinUISO?.Sprite;
        }

        /// <summary>Called by IconStudio when an icon finishes rendering.</summary>
        public static void SetIcon(SkinCategory category, Sprite sprite)
        {
            if (_entries.TryGetValue(category == SkinCategory.Cover ? CoverKey : CharmKey, out var entry))
                SetEntryIcon(entry, sprite);
        }

        /// <summary>
        /// Brings the tab in line with the settings after one changed: lists it again when other entries
        /// should show, otherwise updates icons, labels and selected states in place.
        /// </summary>
        public static void RefreshEntries()
        {
            if (_controller == null || _listed.Count == 0) return;
            var keys = AvailableKeys();
            if (!keys.SequenceEqual(_listed))
            {
                // Our ApplyEntries prefix fills the empty list with the tab's entries.
                _controller.ApplyEntries(new Il2CppSystem.Collections.Generic.List<SkinUISO>());
                return;
            }

            foreach (var kv in _entries)
            {
                var icon = IconFor(kv.Key);
                if (icon != null) SetEntryIcon(kv.Value, icon);
            }
            var pool = _controller.pool;
            if (pool != null)
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i] != null && IsCustom(pool[i].skin)) ModMenuTab.AfterEntrySetup(pool[i], pool[i].skin);
            _controller.RefreshVisibleEntryStates();
        }

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
            _controller = controller;
            string key = skin.addressableKey;

            if (TryGetColorTarget(key, out var target))
            {
                ModPanel.OpenColor(controller, target);
                return false;
            }
            switch (key)
            {
                case ModelKey:
                    ModPanel.OpenModels(controller);
                    return false;
                case StyleKey:
                    ModPanel.OpenJumpStyles(controller);
                    return false;
                case GlowKey:
                    CoverExtras.ToggleGlow();
                    break;
                case FaceKey:
                    CustomFace.Toggle();
                    break;
                default:
                    SkinOverride.Toggle(skin.skinCategory);
                    LocalVisuals.OnCustomToggled(skin.skinCategory);
                    break;
            }
            SkinOverride.RequestRefresh();
            RefreshEntries();
            return false;
        }

        public static bool IsSelected(SkinUISO skin)
        {
            string key = skin.addressableKey;
            // The glow's colour and style only show while it is on: a check mark there would say nothing.
            if (TryGetColorTarget(key, out var target)) return target != CoverExtras.ColorTarget.Glow && CoverExtras.IsColorSet(target);
            return key switch
            {
                GlowKey => CoverExtras.GlowEnabled,
                StyleKey => false,
                FaceKey => CustomFace.IsActive,
                ModelKey => SkinOverride.CoverModel.Length > 0,
                _ => SkinOverride.IsLocalActive(skin.skinCategory),
            };
        }

        /// <summary>CompareSkinsByRarity postfix: our entries first, in s_entries order.</summary>
        public static int Compare(SkinUISO left, SkinUISO right, int nativeResult)
        {
            bool leftCustom = IsCustom(left);
            bool rightCustom = IsCustom(right);
            if (leftCustom && rightCustom)
                return Array.FindIndex(s_entries, e => e.Key == left.addressableKey)
                    .CompareTo(Array.FindIndex(s_entries, e => e.Key == right.addressableKey));
            if (leftCustom == rightCustom) return nativeResult;
            return leftCustom ? -1 : 1;
        }
    }
}
