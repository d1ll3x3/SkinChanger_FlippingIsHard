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
    /// Also puts text, in the screen's own font, where an entry or tab would show an icon.
    /// </summary>
    internal static class ModMenuTab
    {
        public const SkinCategory Category = (SkinCategory)99;
        private const string TabObjectName = "CharmReplacer_ModTab";
        private const string TabLabel = "Mod";
        private const string LabelObjectName = "cupcharm_label";

        private static TMP_FontAsset _font;
        private static Color _textColor = Color.white;
        // Entry buttons whose icon we hid for a label; the pool reuses them for native skins.
        private static readonly HashSet<IntPtr> _labelledButtons = new HashSet<IntPtr>();

        public static bool IsModCategory(SkinCategory category) => category == Category;

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

        /// <summary>SkinEntryButton.Setup / SetupLocked prefix: undo our label before the game sets the button up.</summary>
        public static void BeforeEntrySetup(SkinEntryButton button)
        {
            if (button == null || !_labelledButtons.Remove(button.Pointer)) return;
            var icon = button.iconImage;
            if (icon == null) return;
            HideLabel(icon.transform);
            icon.enabled = true;
        }

        /// <summary>SkinEntryButton.Setup postfix: entries with a text label show it instead of their icon.</summary>
        public static void AfterEntrySetup(SkinEntryButton button, SkinUISO skin)
        {
            string label = NativeSkinMenu.LabelFor(skin);
            var icon = button != null ? button.iconImage : null;
            if (label == null || icon == null || _font == null) return;
            icon.enabled = false;
            SetLabel(icon.transform, label);
            _labelledButtons.Add(button.Pointer);
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
                var go = new GameObject(LabelObjectName);
                go.layer = host.gameObject.layer;
                var rect = go.AddComponent<RectTransform>();
                rect.SetParent(host, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                label = go.AddComponent<TextMeshProUGUI>();
                label.font = _font;
                label.color = _textColor;
                label.alignment = TextAlignmentOptions.Center;
                label.enableAutoSizing = true;
                label.fontSizeMin = 8f;
                label.fontSizeMax = 72f;
                label.enableWordWrapping = false;
                label.raycastTarget = false;
            }
            if (label != null) label.text = text;
        }

        private static void HideLabel(Transform host)
        {
            var existing = host.Find(LabelObjectName);
            if (existing != null) existing.gameObject.SetActive(false);
        }
    }
}
