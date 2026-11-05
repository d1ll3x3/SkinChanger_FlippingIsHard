using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CharmReplacer
{
    /// <summary>
    /// A "Mod" tab in the customization screen holding every entry of the mod (NativeSkinMenu). The screen
    /// collects its tabs with GetComponentsInChildren in InitCategoryTabs, so a clone of the Cover tab made
    /// just before is registered and wired like a native one; its category is one the game doesn't have,
    /// so the tab lists no native skin and only the mod's entries are added to it.
    /// Also puts text, in the screen's own font, on the buttons: a label where an entry or tab would show
    /// an icon, and a caption strip along the bottom of an entry saying what it is.
    /// </summary>
    internal static class ModMenuTab
    {
        public const SkinCategory Category = (SkinCategory)99;
        private const string TabObjectName = "CharmReplacer_ModTab";
        private const string TabLabel = "Mod";
        private const string LabelObjectName = "cupcharm_label";
        private const string CaptionObjectName = "cupcharm_caption";
        // Of the entry's height.
        private const float CaptionHeight = 0.27f;

        private static TMP_FontAsset _font;
        private static Color _textColor = Color.white;
        // Entry buttons whose icon we hid for a label; the pool reuses them for native skins.
        private static readonly HashSet<IntPtr> _labelledButtons = new HashSet<IntPtr>();

        public static bool IsModCategory(SkinCategory category) => category == Category;

        /// <summary>The screen's font, once a screen has been opened; null before.</summary>
        public static TMP_FontAsset Font => _font;

        public static Color TextColor => _textColor;

        /// <summary>InitCategoryTabs prefix: adds the tab once per screen.</summary>
        public static void EnsureTab(CustomizationScreenController controller)
        {
            CaptureFont(controller);
            var tabs = controller.GetComponentsInChildren<SkinCategoryTabButton>(true);
            if (tabs == null || tabs.Count == 0) return;

            SkinCategoryTabButton template = null;
            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                if (tab.category == Category) return;
                if (template == null || tab.category == SkinCategory.Cover) template = tab;
            }

            var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            clone.name = TabObjectName;
            clone.transform.SetAsLastSibling();
            var modTab = clone.GetComponent<SkinCategoryTabButton>();
            modTab.category = Category;
            modTab.overrideCategory = Category;

            // The category icon is the image named after the template's category; the frame, arrow and
            // badge stay.
            string iconName = template.category.ToString();
            Image icon = null;
            var images = clone.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Count; i++)
            {
                var sprite = images[i] != null ? images[i].sprite : null;
                if (sprite == null || sprite.name.IndexOf(iconName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                icon = images[i];
                break;
            }
            if (icon != null && _font != null)
            {
                icon.enabled = false;
                SetLabel(icon.transform, TabLabel);
            }
            else
            {
                Plugin.Log.LogWarning($"[NativeMenu] Mod tab keeps the {iconName} icon (icon found: {icon != null}, font found: {_font != null}).");
            }
            Plugin.Log.LogInfo("[NativeMenu] ✓ Mod tab added.");
        }

        /// <summary>SetCategoryAvailable prefix: the tab has no unlocked native skin, but always has entries.</summary>
        public static void KeepAvailable(SkinCategoryTabButton tab, ref bool isAvailable)
        {
            if (tab != null && tab.category == Category) isAvailable = true;
        }

        /// <summary>SkinEntryButton.Setup / SetupLocked prefix: undo our label and caption before the game sets the button up.</summary>
        public static void BeforeEntrySetup(SkinEntryButton button)
        {
            if (button == null || !_labelledButtons.Remove(button.Pointer)) return;
            HideChild(button.transform, CaptionObjectName);
            var icon = button.iconImage;
            if (icon == null) return;
            HideChild(icon.transform, LabelObjectName);
            icon.enabled = true;
        }

        /// <summary>SkinEntryButton.Setup postfix: the mod's entries get their caption, and a text label instead of the icon if they have one.</summary>
        public static void AfterEntrySetup(SkinEntryButton button, SkinUISO skin)
        {
            if (button == null || _font == null || !NativeSkinMenu.IsCustom(skin)) return;
            string label = NativeSkinMenu.LabelFor(skin);
            string caption = NativeSkinMenu.CaptionFor(skin);
            var icon = button.iconImage;
            if (label != null && icon != null)
            {
                icon.enabled = false;
                SetLabel(icon.transform, label);
            }
            if (caption != null) SetCaption(button.transform, caption);
            _labelledButtons.Add(button.Pointer);
        }

        /// <summary>A text in the screen's font filling <paramref name="parent"/>, not catching clicks.</summary>
        public static TextMeshProUGUI CreateText(Transform parent, string name, float maxSize)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.color = _textColor;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true;
            text.fontSizeMin = 8f;
            text.fontSizeMax = maxSize;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            return text;
        }

        private static void CaptureFont(Component root)
        {
            if (_font != null) return;
            var texts = root.GetComponentsInChildren<TMP_Text>(true);
            if (texts == null) return;
            for (int i = 0; i < texts.Count; i++)
            {
                if (texts[i] == null || texts[i].font == null) continue;
                _font = texts[i].font;
                _textColor = texts[i].color;
                return;
            }
        }

        private static void SetLabel(Transform host, string text)
        {
            var existing = host.Find(LabelObjectName);
            TextMeshProUGUI label;
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                label = existing.GetComponent<TextMeshProUGUI>();
            }
            else
            {
                label = CreateText(host, LabelObjectName, 72f);
            }
            if (label != null) label.text = text;
        }

        // A dark strip over the bottom of the entry, so the caption reads on any icon.
        private static void SetCaption(Transform host, string text)
        {
            var existing = host.Find(CaptionObjectName);
            GameObject strip;
            if (existing != null)
            {
                strip = existing.gameObject;
                strip.SetActive(true);
            }
            else
            {
                strip = new GameObject(CaptionObjectName);
                strip.layer = host.gameObject.layer;
                var rect = strip.AddComponent<RectTransform>();
                rect.SetParent(host, false);
                rect.anchorMin = new Vector2(0.06f, 0.06f);
                rect.anchorMax = new Vector2(0.94f, 0.06f + CaptionHeight);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                var background = strip.AddComponent<Image>();
                background.sprite = ModPanel.RoundedSprite;
                background.type = Image.Type.Sliced;
                background.color = new Color(0f, 0f, 0f, 0.6f);
                background.raycastTarget = false;
                var caption = CreateText(strip.transform, "text", 26f);
                caption.margin = new Vector4(4f, 1f, 4f, 1f);
                caption.color = Color.white;
            }
            var label = strip.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = text;
        }

        private static void HideChild(Transform host, string name)
        {
            var existing = host.Find(name);
            if (existing != null) existing.gameObject.SetActive(false);
        }
    }
}
