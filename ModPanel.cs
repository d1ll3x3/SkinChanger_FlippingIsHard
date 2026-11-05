using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CharmReplacer
{
    /// <summary>
    /// The floating panel the Mod tab opens over its own entry list, leaving the preview phone in view: a
    /// colour picker (saturation/value square, hue bar, intensity bar), the cover model picker or the jump
    /// style picker. Built
    /// from plain UGUI on the screen's root canvas; a dim layer behind it closes it on any outside click.
    /// Colours apply live while dragged (LocalVisuals repaints the preview from the config) and are
    /// written to disk and published to other players on release.
    /// </summary>
    internal static class ModPanel
    {
        private const float Padding = 24f;
        private const float TitleHeight = 44f;
        private const float FooterHeight = 56f;
        private const float Gap = 16f;
        private const float BarWidth = 34f;
        private const float FooterButtonWidth = 130f;
        private const float MinSize = 420f;
        private const float MaxWidth = 680f;
        private const float MaxHeight = 620f;
        private const float ModelCellTarget = 130f;
        private const int SquareTextureSize = 64;
        private const int BarTextureSize = 128;

        private static readonly Color PanelColor = new Color(0.07f, 0.07f, 0.09f, 0.97f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.18f);
        private static readonly Color CellColor = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color ButtonColor = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color AccentColor = new Color(1f, 0.8f, 0.25f, 1f);
        private static readonly Color DimColor = new Color(0f, 0f, 0f, 0.15f);

        private static GameObject _root;
        private static CustomizationScreenController _controller;
        private static Sprite _rounded, _disc, _ring;
        private static Texture2D _squareTexture, _hueTexture, _intensityTexture;

        // Colour picker.
        private static CoverExtras.ColorTarget _target;
        private static float _hue, _saturation, _value, _intensity;
        private static float _squareHue = -1f;
        private static RectTransform _square, _hueBar, _intensityBar, _squareKnob, _hueKnob, _intensityKnob;
        private static Image _hueKnobFill, _intensityKnobFill, _preview;
        private static TextMeshProUGUI _valueText;

        // Card pickers (cover model, jump style).
        private static readonly List<(string Key, string Name, Image Background)> _cells = new List<(string, string, Image)>();
        private static TextMeshProUGUI _cellsText;

        public static Sprite RoundedSprite => _rounded != null ? _rounded : _rounded = MakeRoundedSprite();

        private static Sprite Disc => _disc != null ? _disc : _disc = MakeCircleSprite(0f);

        private static Sprite Ring => _ring != null ? _ring : _ring = MakeCircleSprite(0.22f);

        /// <summary>Closes the panel once its screen is gone (closed, or the level unloaded).</summary>
        public static void Tick()
        {
            if (_root == null) return;
            try
            {
                if (_controller == null || !_controller.IsActive) Close();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NativeMenu] Mod panel tick error: {ex.Message}");
                Close();
            }
        }

        public static void Close()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _cells.Clear();
        }

        // ── Colour picker ─────────────────────────────────────────────────────

        public static void OpenColor(CustomizationScreenController controller, CoverExtras.ColorTarget target)
        {
            try
            {
                var panel = Begin(controller, CoverExtras.TargetName(target), out var size);
                if (panel == null) return;
                _target = target;
                LoadColor();

                float top = Padding + TitleHeight + Gap;
                float middle = size.y - top - FooterHeight - Padding - Gap;
                float side = Mathf.Min(middle, size.x - 2f * Padding - 2f * (BarWidth + Gap));
                float x = (size.x - side - 2f * (Gap + BarWidth)) / 2f;

                _squareTexture ??= MakeTexture(SquareTextureSize, SquareTextureSize, "CharmReplacer_PickerSquare");
                _hueTexture ??= MakeHueTexture();
                _intensityTexture ??= MakeTexture(1, BarTextureSize, "CharmReplacer_PickerIntensity");
                _squareHue = -1f;

                _square = AddRawImage(panel, "square", _squareTexture, x, top, side, side);
                _hueBar = AddRawImage(panel, "hue", _hueTexture, x + side + Gap, top, BarWidth, side);
                _intensityBar = AddRawImage(panel, "intensity", _intensityTexture, x + side + 2f * Gap + BarWidth, top, BarWidth, side);

                _squareKnob = AddImage(_square, "knob", Ring, Color.white).rectTransform;
                SetKnob(_squareKnob, Vector2.zero, 30f);
                _hueKnob = AddKnob(_hueBar, out _hueKnobFill);
                _intensityKnob = AddKnob(_intensityBar, out _intensityKnobFill);

                OnPointer(_square.gameObject, data =>
                {
                    var p = Normalized(_square, data);
                    _saturation = p.x;
                    _value = p.y;
                    PushColor();
                });
                OnPointer(_hueBar.gameObject, data =>
                {
                    _hue = Mathf.Min(Normalized(_hueBar, data).y, 0.999f);
                    PushColor();
                });
                OnPointer(_intensityBar.gameObject, data =>
                {
                    _intensity = Normalized(_intensityBar, data).y * CoverExtras.MaxIntensityPercent / 100f;
                    PushColor();
                });

                float footer = size.y - Padding - FooterHeight;
                _preview = AddImage(panel, "preview", Disc, Color.white);
                Place(_preview.rectTransform, Padding, footer, FooterHeight, FooterHeight);
                float textX = Padding + FooterHeight + 12f;
                _valueText = AddText(panel, "value", textX, footer, size.x - textX - Padding - 2f * FooterButtonWidth - 20f, FooterHeight, 26f);
                _valueText.alignment = TextAlignmentOptions.Left;
                AddButton(panel, "Reset", size.x - Padding - 2f * FooterButtonWidth - 10f, footer, FooterButtonWidth, FooterHeight, false, () =>
                {
                    CoverExtras.ResetColor(_target);
                    LoadColor();
                    Commit();
                    RefreshColor();
                });
                AddButton(panel, "Done", size.x - Padding - FooterButtonWidth, footer, FooterButtonWidth, FooterHeight, true, Close);

                RefreshColor();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NativeMenu] Could not open the colour picker: {ex}");
                Close();
            }
        }

        private static void LoadColor()
        {
            CoverExtras.GetColor(_target, out var rgb, out _intensity);
            Color.RGBToHSV(rgb, out _hue, out _saturation, out _value);
        }

        private static Color CurrentRgb => Color.HSVToRGB(_hue, _saturation, _value);

        // While dragging: live in memory only; the file and the network get it on release (Commit).
        private static void PushColor()
        {
            CoverExtras.SetColor(_target, CurrentRgb, _intensity, false);
            RefreshColor();
        }

        private static void Commit()
        {
            CoverExtras.CommitColors();
            SkinOverride.RequestRefresh();
            NativeSkinMenu.RefreshEntries();
        }

        private static void RefreshColor()
        {
            if (_square == null) return;
            var rgb = CurrentRgb;
            if (_squareHue != _hue)
            {
                _squareHue = _hue;
                var pixels = new Color32[SquareTextureSize * SquareTextureSize];
                for (int y = 0; y < SquareTextureSize; y++)
                for (int x = 0; x < SquareTextureSize; x++)
                    pixels[y * SquareTextureSize + x] = Color.HSVToRGB(_hue, x / (SquareTextureSize - 1f), y / (SquareTextureSize - 1f));
                _squareTexture.SetPixels32(pixels);
                _squareTexture.Apply(false);
            }

            // Black to the colour, then on past it towards white: what more intensity does to a glow.
            var bar = new Color32[BarTextureSize];
            float max = CoverExtras.MaxIntensityPercent / 100f;
            for (int y = 0; y < BarTextureSize; y++)
            {
                float k = y / (BarTextureSize - 1f) * max;
                bar[y] = k <= 1f ? rgb * k : Color.Lerp(rgb, Color.white, (k - 1f) / Mathf.Max(0.01f, max - 1f) * 0.6f);
            }
            _intensityTexture.SetPixels32(bar);
            _intensityTexture.Apply(false);

            float side = _square.rect.height;
            SetKnob(_squareKnob, new Vector2(_saturation * _square.rect.width, _value * side), 30f);
            _hueKnob.anchoredPosition = new Vector2(0f, _hue * _hueBar.rect.height);
            _intensityKnob.anchoredPosition = new Vector2(0f, _intensity / max * _intensityBar.rect.height);
            _hueKnobFill.color = Color.HSVToRGB(_hue, 1f, 1f);
            _intensityKnobFill.color = rgb;
            _preview.color = rgb;

            bool set = CoverExtras.IsColorSet(_target);
            _valueText.text = set
                ? $"#{ColorUtility.ToHtmlStringRGB(rgb)}   Intensity {Mathf.RoundToInt(_intensity * 100f)}%"
                : "Game colour";
        }

        // ── Model picker ──────────────────────────────────────────────────────

        public static void OpenModels(CustomizationScreenController controller)
        {
            try
            {
                var panel = Begin(controller, "Cover model", out var size);
                if (panel == null) return;
                var content = AddGrid(panel, size, ModelCellTarget, 3);
                foreach (var model in CoverModels(controller))
                {
                    string key = model.Key;
                    AddCard(content, key, model.Name, null, model.Sprite, true, () => SelectModel(key));
                }
                AddCardsFooter(panel, size);
                RefreshCards();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NativeMenu] Could not open the model picker: {ex}");
                Close();
            }
        }

        /// <summary>
        /// The default cover, then every other cover the custom skin can go on: unlocked or owned as the
        /// game's own menu decides (paid and progression covers alike), and with a texture to paint.
        /// </summary>
        private static List<(string Key, string Name, Sprite Sprite)> CoverModels(CustomizationScreenController controller)
        {
            var models = new List<(string, string, Sprite)> { ("", "Default", NativeSkinMenu.BaseSkinSprite(SkinCategory.Cover)) };
            var dummy = LocalVisuals.ActiveCustomizationDummy;
            var handler = dummy != null ? SkinOverride.GetHandler(dummy.skinsHandlerMap, SkinCategory.Cover) : null;
            string defaultKey = handler?.DefaultSkinUISO?.AddressableKey;
            var manager = controller.skinsManager;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skins = controller.allSkins;
            if (skins == null) return models;
            for (int i = 0; i < skins.Count; i++)
            {
                var skin = skins[i];
                if (skin == null || skin.skinCategory != SkinCategory.Cover || NativeSkinMenu.IsCustom(skin)) continue;
                string key = skin.addressableKey;
                if (string.IsNullOrEmpty(key) || key == defaultKey || !seen.Add(key)) continue;
                if (!controller.IsUnlockedSkin(skin) || !SkinOverride.CanUseCoverModel(key, manager)) continue;
                models.Add((key, DisplayName(key), skin.sprite));
            }
            return models;
        }

        // "cvr_hangeris" -> "Hangeris".
        private static string DisplayName(string key)
        {
            string name = key.StartsWith("cvr_", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : key;
            var words = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        private static void SelectModel(string key)
        {
            SkinOverride.SetCoverModel(key);
            LocalVisuals.OnCustomToggled(SkinCategory.Cover);
            NativeSkinMenu.RefreshEntries();
            RefreshCards();
        }

        // ── Jump style picker ─────────────────────────────────────────────────

        public static void OpenJumpStyles(CustomizationScreenController controller)
        {
            try
            {
                var panel = Begin(controller, "Jump style", out var size);
                if (panel == null) return;

                // Bare covers (Wizard...) only take the outline; unknown until the glow mask is built.
                var dummy = LocalVisuals.ActiveCustomizationDummy;
                var handler = dummy != null ? SkinOverride.GetHandler(dummy.skinsHandlerMap, SkinCategory.Cover) : null;
                CoverExtras.PrepareMask(dummy?.skinsManager?.skinAddressableLibrary);
                bool takesLines = CoverExtras.TakesLines(handler) ?? true;

                var content = AddGrid(panel, size, size.x, 3);
                foreach (CoverExtras.JumpStyle style in Enum.GetValues(typeof(CoverExtras.JumpStyle)))
                {
                    bool usable = takesLines || style == CoverExtras.JumpStyle.Outline;
                    var chosen = style;
                    AddCard(content, style.ToString(), CoverExtras.StyleName(style),
                        usable ? CoverExtras.StyleDescription(style) : "Not on this cover", null, usable, () =>
                        {
                            CoverExtras.SetStyle(chosen);
                            SkinOverride.RequestRefresh();
                            NativeSkinMenu.RefreshEntries();
                            RefreshCards();
                        });
                }
                AddCardsFooter(panel, size);
                RefreshCards();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NativeMenu] Could not open the jump style picker: {ex}");
                Close();
            }
        }

        // ── Card grids ────────────────────────────────────────────────────────

        // A scrolling grid filling the panel between title and footer; cells about cellTarget wide.
        private static RectTransform AddGrid(RectTransform panel, Vector2 size, float cellTarget, int minColumns)
        {
            float top = Padding + TitleHeight + Gap;
            float width = size.x - 2f * Padding;
            float height = size.y - top - FooterHeight - Padding - Gap;
            var viewport = NewRect(panel, "viewport");
            Place(viewport, Padding, top, width, height);
            viewport.gameObject.AddComponent<RectMask2D>();
            // Catches the wheel and drags between cells.
            AddImage(viewport, "hit", null, new Color(0f, 0f, 0f, 0f)).rectTransform.Stretch();

            var content = NewRect(viewport, "content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            const float spacing = 12f;
            int columns = Mathf.Max(minColumns, Mathf.FloorToInt((width + spacing) / (cellTarget + spacing)));
            float cell = (width - (columns - 1) * spacing) / columns;
            var grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(cell, Mathf.Min(cell, height));
            grid.spacing = new Vector2(spacing, spacing);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            _cells.Clear();
            return content;
        }

        // An icon over its name, or (without icon) a big name over a description. Disabled cards are dimmed and inert.
        private static void AddCard(RectTransform content, string key, string name, string subtitle, Sprite sprite, bool enabled, Action onClick)
        {
            var background = AddImage(content, key.Length == 0 ? "default" : key, RoundedSprite, CellColor);
            background.type = Image.Type.Sliced;
            var group = background.gameObject.AddComponent<CanvasGroup>();
            group.alpha = enabled ? 1f : 0.35f;
            group.interactable = enabled;
            if (enabled)
            {
                var button = background.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
                {
                    try { onClick(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[NativeMenu] Mod panel '{name}' error: {ex.Message}"); }
                })));
            }

            if (sprite != null)
            {
                var icon = AddImage(background.rectTransform, "icon", sprite, Color.white);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                Anchor(icon.rectTransform, 0.1f, 0.26f, 0.9f, 0.94f);
                var label = ModMenuTab.CreateText(background.transform, "name", 22f);
                Anchor(label.rectTransform, 0.04f, 0.03f, 0.96f, 0.24f);
                label.text = name;
            }
            else
            {
                var title = ModMenuTab.CreateText(background.transform, "name", 40f);
                Anchor(title.rectTransform, 0.06f, 0.42f, 0.94f, 0.8f);
                title.text = name;
                var detail = ModMenuTab.CreateText(background.transform, "detail", 22f);
                Anchor(detail.rectTransform, 0.06f, 0.18f, 0.94f, 0.38f);
                detail.text = subtitle ?? "";
            }
            _cells.Add((key, name, background));
        }

        private static void AddCardsFooter(RectTransform panel, Vector2 size)
        {
            float footer = size.y - Padding - FooterHeight;
            _cellsText = AddText(panel, "current", Padding, footer, size.x - 2f * Padding - FooterButtonWidth - 12f, FooterHeight, 26f);
            _cellsText.alignment = TextAlignmentOptions.Left;
            AddButton(panel, "Done", size.x - Padding - FooterButtonWidth, footer, FooterButtonWidth, FooterHeight, true, Close);
        }

        // Highlights the card of the current setting (model or jump style) and names it in the footer.
        private static void RefreshCards()
        {
            bool styles = _cells.Count > 0 && Enum.TryParse(_cells[0].Key, out CoverExtras.JumpStyle _);
            string current = styles ? CoverExtras.Style.ToString() : SkinOverride.CoverModel;
            string currentName = styles ? CoverExtras.StyleName(CoverExtras.Style) : "Default";
            foreach (var cell in _cells)
            {
                bool selected = cell.Key == current;
                if (selected) currentName = cell.Name;
                if (cell.Background == null) continue;
                cell.Background.color = selected ? AccentColor : CellColor;
                // Dark text on the accent card.
                var texts = cell.Background.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Count; i++)
                    texts[i].color = selected ? new Color(0.08f, 0.08f, 0.08f, 1f) : Color.white;
            }
            if (_cellsText != null) _cellsText.text = styles ? $"Jump light: {currentName}" : $"Custom skin on: {currentName}";
        }

        private static void Anchor(RectTransform rect, float minX, float minY, float maxX, float maxY)
        {
            rect.anchorMin = new Vector2(minX, minY);
            rect.anchorMax = new Vector2(maxX, maxY);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // ── Building ──────────────────────────────────────────────────────────

        /// <summary>A fresh panel over the screen's entry list, with its title. Null if the screen has no canvas.</summary>
        private static RectTransform Begin(CustomizationScreenController controller, string title, out Vector2 size)
        {
            Close();
            size = default;
            _controller = controller;
            var anchor = controller.PanelRect ?? controller.transform.TryCast<RectTransform>();
            var canvas = anchor != null ? anchor.GetComponentInParent<Canvas>() : null;
            if (canvas == null)
            {
                Plugin.Log.LogWarning("[NativeMenu] Mod panel: the customization screen has no canvas.");
                return null;
            }
            canvas = canvas.rootCanvas;

            _root = new GameObject("CharmReplacer_ModPanel");
            _root.layer = canvas.gameObject.layer;
            var root = _root.AddComponent<RectTransform>();
            root.SetParent(canvas.transform, false);
            root.Stretch();
            root.SetAsLastSibling();

            var dim = AddImage(root, "dim", null, DimColor);
            dim.rectTransform.Stretch();
            var dimButton = dim.gameObject.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(Close)));

            // Over the entry list, in the root's space (the root is stretched with its pivot centred).
            var corners = new Il2CppStructArray<Vector3>(4);
            anchor.GetWorldCorners(corners);
            Vector2 min = root.InverseTransformPoint(corners[0]);
            Vector2 max = root.InverseTransformPoint(corners[2]);
            var available = root.rect.size;
            size = new Vector2(
                Mathf.Min(Mathf.Clamp((max.x - min.x) * 0.94f, MinSize, MaxWidth), available.x),
                Mathf.Min(Mathf.Clamp((max.y - min.y) * 0.94f, MinSize, MaxHeight), available.y));

            var border = AddImage(root, "panel", RoundedSprite, BorderColor);
            border.type = Image.Type.Sliced;
            var panel = border.rectTransform;
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = size;
            panel.anchoredPosition = (min + max) / 2f;

            var inner = AddImage(panel, "background", RoundedSprite, PanelColor);
            inner.type = Image.Type.Sliced;
            inner.rectTransform.Stretch();
            inner.rectTransform.offsetMin = new Vector2(2f, 2f);
            inner.rectTransform.offsetMax = new Vector2(-2f, -2f);

            var heading = AddText(panel, "title", Padding, Padding, size.x - 2f * Padding, TitleHeight, 38f);
            heading.alignment = TextAlignmentOptions.Left;
            heading.text = title;
            heading.color = AccentColor;
            return panel;
        }

        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image AddImage(Transform parent, string name, Sprite sprite, Color color)
        {
            var image = NewRect(parent, name).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            return image;
        }

        private static RectTransform AddRawImage(Transform parent, string name, Texture texture, float x, float y, float w, float h)
        {
            var image = NewRect(parent, name).gameObject.AddComponent<RawImage>();
            image.texture = texture;
            return Place(image.rectTransform, x, y, w, h);
        }

        private static TextMeshProUGUI AddText(Transform parent, string name, float x, float y, float w, float h, float maxSize)
        {
            var host = NewRect(parent, name);
            Place(host, x, y, w, h);
            var text = ModMenuTab.CreateText(host, "text", maxSize);
            text.color = Color.white;
            return text;
        }

        private static void AddButton(Transform parent, string label, float x, float y, float w, float h, bool accent, Action onClick)
        {
            var image = AddImage(parent, label, RoundedSprite, accent ? AccentColor : ButtonColor);
            image.type = Image.Type.Sliced;
            Place(image.rectTransform, x, y, w, h);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
            {
                try { onClick(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[NativeMenu] Mod panel '{label}' error: {ex.Message}"); }
            })));
            var text = ModMenuTab.CreateText(image.transform, "text", 28f);
            text.margin = new Vector4(8f, 4f, 8f, 4f);
            text.color = accent ? new Color(0.08f, 0.08f, 0.08f, 1f) : Color.white;
            text.text = label;
        }

        // A disc of the current colour inside a white ring, sliding along a bar.
        private static RectTransform AddKnob(RectTransform bar, out Image fill)
        {
            var ring = AddImage(bar, "knob", Ring, Color.white);
            ring.raycastTarget = false;
            var rect = ring.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(BarWidth + 12f, BarWidth + 12f);
            fill = AddImage(rect, "fill", Disc, Color.white);
            fill.raycastTarget = false;
            fill.rectTransform.Stretch();
            fill.rectTransform.offsetMin = new Vector2(6f, 6f);
            fill.rectTransform.offsetMax = new Vector2(-6f, -6f);
            return rect;
        }

        private static void SetKnob(RectTransform knob, Vector2 position, float size)
        {
            knob.GetComponent<Image>().raycastTarget = false;
            knob.anchorMin = knob.anchorMax = Vector2.zero;
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(size, size);
            knob.anchoredPosition = position;
        }

        // Top-left based placement inside a panel.
        private static RectTransform Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        private static void Stretch(this RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Press and drag over <paramref name="target"/> call <paramref name="onMove"/>; release commits.</summary>
        private static void OnPointer(GameObject target, Action<PointerEventData> onMove)
        {
            var trigger = target.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, onMove);
            AddTrigger(trigger, EventTriggerType.Drag, onMove);
            AddTrigger(trigger, EventTriggerType.PointerUp, _ => Commit());
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, Action<PointerEventData> action)
        {
            var entry = new EventTrigger.Entry();
            entry.eventID = type;
            entry.callback.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BaseEventData>>(new Action<BaseEventData>(data =>
            {
                try
                {
                    var pointer = data != null ? data.TryCast<PointerEventData>() : null;
                    if (pointer != null) action(pointer);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[NativeMenu] Colour picker error: {ex.Message}");
                }
            })));
            trigger.triggers.Add(entry);
        }

        private static Vector2 Normalized(RectTransform rect, PointerEventData data)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, data.position, data.pressEventCamera, out var local);
            var r = rect.rect;
            return new Vector2(Mathf.Clamp01((local.x - r.x) / r.width), Mathf.Clamp01((local.y - r.y) / r.height));
        }

        // ── Textures ──────────────────────────────────────────────────────────

        private static Texture2D MakeTexture(int w, int h, string name)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.name = name;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }

        // Red at the bottom, round the wheel to red again at the top.
        private static Texture2D MakeHueTexture()
        {
            var texture = MakeTexture(1, BarTextureSize, "CharmReplacer_PickerHue");
            var pixels = new Color32[BarTextureSize];
            for (int y = 0; y < BarTextureSize; y++) pixels[y] = Color.HSVToRGB(y / (BarTextureSize - 1f) * 0.999f, 1f, 1f);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        // White, rounded corners, nine-sliced so one sprite fits every box.
        private static Sprite MakeRoundedSprite()
        {
            const int size = 48;
            const float radius = 16f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
            }
            var texture = MakeTexture(size, size, "CharmReplacer_Rounded");
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        // A white disc, or a ring <paramref name="hole"/> of whose radius is hollow, with a dark rim to read on light colours.
        private static Sprite MakeCircleSprite(float hole)
        {
            const int size = 64;
            const float radius = 31f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size / 2f, size / 2f));
                float alpha = Mathf.Clamp01(radius - d + 0.5f);
                if (hole > 0f) alpha *= Mathf.Clamp01(d - radius * (1f - hole) + 0.5f);
                float shade = hole > 0f && d > radius - 2.5f ? 0.15f : 1f;
                pixels[y * size + x] = new Color(shade, shade, shade, alpha);
            }
            var texture = MakeTexture(size, size, "CharmReplacer_Circle");
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
    }
}
