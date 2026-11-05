using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using EHS;
using EHS.Skins;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace CharmReplacer
{
    /// <summary>
    /// Extras on top of whatever cover a player wears: a colour for the cover (the Wizard cover's three
    /// colours, a tint on the others) and a jump glow (the side lines of the Hangeris cover lighting up
    /// while the jump is available).
    /// The glow is driven by the game's own PlayerJumpEnabledShader, which writes _JumpEnabled into the
    /// player's materials, but only SG_Player_MSAO reacts to it and the Wizard and Galactic covers use
    /// other shaders. So the glow is a separate renderer over the cover body, using the Hangeris material
    /// with an alpha-clipped mask, and this class mirrors the game's jump value onto it every frame.
    /// That mask is laid out on the full phone body's UVs; covers built on the "Bare" body (Wizard,
    /// Galactic...) can't take it, so on those the phone's outline lights up instead.
    /// The settings ride the flag SkinOverride publishes, so players with the mod see them too.
    /// </summary>
    internal static class CoverExtras
    {
        private const string WizardKey = "cvr_wizard";
        private const string WizardShader = "Shader Graphs/SG_Wizard";
        private const string HangerisKey = "cvr_hangeris";
        private const string GlowObjectName = "cupcharm_jump_glow";
        private const string GlowToken = "glow=";
        private const string TintToken = "tint=";
        private const string WizardToken = "wiz=";
        // Covers painted with an uploaded skin (SkinLoader / RemoteSkinService names) are left untinted.
        private static readonly string[] s_customTexturePrefixes = { "CustomPlayerSkin", "skin_" };

        // Hangeris lights its lines at this HDR intensity (_ColorJump ≈ 4).
        private const float GlowIntensity = 4f;
        // In the Hangeris emission map, RGB is the always-on print (keypad symbols, name, logo) and alpha
        // is what lights on jump. A key is a small unlit hole with a symbol in it; its lit ring is dropped.
        private const float KeyHoleMaxSize = 0.13f;   // of the texture size
        private const float KeyRingWidth = 0.02f;     // of the texture size
        private const float MinIslandArea = 0.0004f;  // of the texture area: crumbs left between keys
        // Drawn after the opaque cover (2000) and alpha-tested geometry (2450), so it wins the equal depth.
        private const int GlowRenderQueue = 2460;
        private const float LoadTimeout = 20f;
        private static readonly Vector3 LoadPosition = new Vector3(0f, -10000f, 0f);

        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int EmissiveTexId = Shader.PropertyToID("_EmissiveTex");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int ColorJumpId = Shader.PropertyToID("_ColorJump");
        private static readonly int JumpEnabledId = Shader.PropertyToID("_JumpEnabled");
        // Shader Graph keyword toggles are also a float property of the same name.
        private static readonly int JumpLightId = Shader.PropertyToID(JumpLightKeyword);
        private static readonly int AlphaClipFloatId = Shader.PropertyToID("_AlphaClipFloat");
        private static readonly int NormalTexId = Shader.PropertyToID("_NormalTex");
        private static readonly int MsaoTexId = Shader.PropertyToID("_MSAOTex");
        private static readonly int MetallicId = Shader.PropertyToID("_MetallicStrength");
        private static readonly int SmoothnessId = Shader.PropertyToID("_SmoothnessStrength");
        private static readonly int FresnelOnId = Shader.PropertyToID("_FresnelOn");
        private static readonly int[] WizardColorIds =
        {
            Shader.PropertyToID("_ColorTop"), Shader.PropertyToID("_ColorBottom"), Shader.PropertyToID("_ColorSwirl"),
        };

        private const string StyleToken = "jstyle=";
        private const string DefaultGlowHex = "FF0000";
        // Covers that light up on jump by themselves (Hangeris) have this on in their material.
        private const string JumpLightKeyword = "_JUMPLIGHTENABLED";
        // Colour values are "RRGGBB" or "RRGGBB:NNN", NNN being the intensity in percent.
        public const int MaxIntensityPercent = 200;

        /// <summary>The colours set from the Mod tab's colour picker.</summary>
        internal enum ColorTarget { Cover, Glow, WizardTop, WizardBottom, WizardSwirl }

        /// <summary>
        /// Which jump light a cover gets. Lines: the Hangeris side lines without the keypad. Keypad: the
        /// Hangeris light whole. Outline: the phone's outline, what the Wizard and other "Bare" covers show.
        /// Bare covers can't take the first two (the mask is laid out on the full body's UVs).
        /// </summary>
        internal enum JumpStyle { Lines, Keypad, Outline }

        /// <summary>What to add to one cover. Null colours keep the game's look; colours carry their intensity.</summary>
        internal sealed class Look
        {
            public Color? Glow;
            public Color? Tint;
            public readonly Color?[] Wizard = new Color?[3];
            public JumpStyle Style;

            public string Key => $"{Glow}|{Tint}|{Wizard[0]}|{Wizard[1]}|{Wizard[2]}|{Style}";
        }

        private static ConfigEntry<bool> _glowEnabled;
        private static ConfigEntry<JumpStyle> _style;
        // Indexed by ColorTarget.
        private static ConfigEntry<string>[] _colors;

        private enum MaskState { Idle, Requested, Loading, Ready, Failed }
        private static MaskState _maskState;
        private static AsyncOperationHandle<GameObject> _loadHandle;
        private static float _loadStart;
        // Overlay materials, one per mask: the side lines, and the Hangeris light whole.
        private static Material _linesTemplate;
        private static Material _keypadTemplate;
        private static string _bodyMeshName;
        private static int _bodyVertexCount;

        private sealed class Overlay
        {
            public Renderer Cover;
            public GameObject Root;
            public Renderer Renderer;
            public Material Material;
            public PlayerJumpEnabledShader Jump;
            public Color Color;
            public Material Template;
            // Last jump value written to Material (the material is ours alone).
            public float Value = -1f;
        }

        private sealed class OutlineGlow
        {
            public Renderer Renderer;
            public int Slot;
            public Material Original;
            public Material Clone;
            public int[] ColorIds;
            public Color[] OriginalColors;
            public PlayerJumpEnabledShader Jump;
            public Color Color;
            // Jump value the clone's colours were last blended for; NaN forces a rewrite.
            public float Value = float.NaN;
        }

        private sealed class RecolorClone
        {
            public Renderer Renderer;
            public int Slot;
            public Material Original;
            public Material Clone;
        }

        private static readonly Dictionary<IntPtr, Overlay> _overlays = new Dictionary<IntPtr, Overlay>();
        private static readonly List<RecolorClone> _recolors = new List<RecolorClone>();
        private static readonly List<OutlineGlow> _outlineGlows = new List<OutlineGlow>();
        private static readonly HashSet<string> _loggedMeshMisses = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loggedGlowModes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Sprite> _swatches = new Dictionary<string, Sprite>(StringComparer.Ordinal);

        // Last finished pass per cover handler. Apply runs for every player twice a second; once a cover
        // matches its look, repeating the pass only re-reads its renderers and materials (garbage the
        // game's GC then has to collect). _version moves when something else rewrites cover materials.
        private static readonly Dictionary<IntPtr, string> _settled = new Dictionary<IntPtr, string>();
        private static int _version;

        /// <summary>Cover materials were replaced (a repaint): the next pass looks again.</summary>
        public static void Invalidate() => _version++;

        public static void ClearSceneState() => _settled.Clear();

        public static void Init(ConfigEntry<bool> glowEnabled, ConfigEntry<JumpStyle> style, ConfigEntry<string>[] colors)
        {
            _glowEnabled = glowEnabled;
            _style = style;
            _colors = colors;
        }

        public static bool IsWizard(string coverKey) => coverKey == WizardKey;

        // ── Menu ──────────────────────────────────────────────────────────────

        public static bool GlowEnabled => _glowEnabled != null && _glowEnabled.Value;

        public static JumpStyle Style => _style != null ? _style.Value : JumpStyle.Lines;

        public static string StyleName(JumpStyle style) => style.ToString();

        public static string StyleDescription(JumpStyle style) => style switch
        {
            JumpStyle.Lines => "Side lines",
            JumpStyle.Keypad => "Lines and keypad",
            _ => "Phone outline",
        };

        public static void ToggleGlow()
        {
            if (_glowEnabled == null) return;
            _glowEnabled.Value = !_glowEnabled.Value;
            Invalidate();
            Plugin.Log.LogInfo($"[NativeMenu] Jump glow {(_glowEnabled.Value ? "enabled" : "disabled")}.");
        }

        public static void SetStyle(JumpStyle style)
        {
            if (_style == null) return;
            _style.Value = style;
            Invalidate();
            Plugin.Log.LogInfo($"[NativeMenu] Jump glow style {style}.");
        }

        /// <summary>
        /// Whether the cover <paramref name="handler"/> shows has the full phone body, so it can take the
        /// Lines and Keypad styles; null while that can't be told yet (the Hangeris mask isn't built).
        /// </summary>
        public static bool? TakesLines(SkinPrefabHandler handler)
        {
            if (_maskState != MaskState.Ready || handler == null || handler.IsLoading) return null;
            if (!handler.TryGetInstance(out var instance) || instance == null) return null;
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Count; i++)
                if (renderers[i] != null && renderers[i].gameObject.name != GlowObjectName && IsCoverBody(renderers[i])) return true;
            return false;
        }

        /// <summary>Starts building the glow mask (from the menu, so TakesLines can answer).</summary>
        public static void PrepareMask(SkinAddressableLibrary library) => EnsureMask(library);

        public static string TargetName(ColorTarget target) => target switch
        {
            ColorTarget.Cover => "Cover colour",
            ColorTarget.Glow => "Jump glow colour",
            ColorTarget.WizardTop => "Wizard top",
            ColorTarget.WizardBottom => "Wizard bottom",
            _ => "Wizard swirl",
        };

        /// <summary>Whether the colour replaces the game's own (the glow always has one).</summary>
        public static bool IsColorSet(ColorTarget target) =>
            target == ColorTarget.Glow ? GlowEnabled : TryParseColor(ColorEntry(target)?.Value, out _, out _);

        /// <summary>The colour the picker opens on; white for an unset one.</summary>
        public static void GetColor(ColorTarget target, out Color rgb, out float intensity)
        {
            if (TryParseColor(ColorEntry(target)?.Value, out rgb, out intensity)) return;
            TryParseHex(target == ColorTarget.Glow ? DefaultGlowHex : "FFFFFF", out rgb);
            intensity = 1f;
        }

        /// <summary>
        /// Sets a colour. While the picker is being dragged <paramref name="save"/> is false: the value is
        /// live in memory and CommitColors writes the file once on release.
        /// </summary>
        public static void SetColor(ColorTarget target, Color rgb, float intensity, bool save)
        {
            var entry = ColorEntry(target);
            if (entry == null) return;
            var file = entry.ConfigFile;
            bool saveOnSet = file.SaveOnConfigSet;
            file.SaveOnConfigSet = save;
            try
            {
                entry.Value = FormatColor(rgb, intensity);
                // Picking a glow colour means wanting the glow.
                if (target == ColorTarget.Glow && _glowEnabled != null) _glowEnabled.Value = true;
            }
            finally
            {
                file.SaveOnConfigSet = saveOnSet;
            }
        }

        public static void CommitColors()
        {
            _colors?[0]?.ConfigFile.Save();
            Plugin.Log.LogInfo($"[NativeMenu] Colours: {string.Join(", ", Array.ConvertAll(_colors ?? Array.Empty<ConfigEntry<string>>(), e => e.Definition.Key + "=" + e.Value))}.");
        }

        /// <summary>Back to the game's colour (red for the glow, which has none).</summary>
        public static void ResetColor(ColorTarget target)
        {
            var entry = ColorEntry(target);
            if (entry != null) entry.Value = target == ColorTarget.Glow ? DefaultGlowHex : "";
        }

        /// <summary>Icon of a colour entry: a disc of the colour, or a struck-through one for the game's own.</summary>
        public static Sprite ColorSwatch(ColorTarget target)
        {
            if (target != ColorTarget.Glow && !IsColorSet(target)) return Swatch(null);
            GetColor(target, out var rgb, out _);
            return Swatch(rgb);
        }

        private static ConfigEntry<string> ColorEntry(ColorTarget target) =>
            _colors != null && (int)target < _colors.Length ? _colors[(int)target] : null;

        private static Sprite Swatch(Color? color)
        {
            string key = color.HasValue ? ToHex(color.Value) : "none";
            if (_swatches.TryGetValue(key, out var cached) && cached != null) return cached;

            const int size = 96;
            const float radius = 44f, ring = 5f;
            var fill = color ?? new Color(0.22f, 0.22f, 0.24f, 1f);
            var slash = new Color(0.9f, 0.2f, 0.2f, 1f);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size / 2f, dy = y + 0.5f - size / 2f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // One pixel of antialiasing on each edge.
                float alpha = Mathf.Clamp01(radius - d + 0.5f);
                Color c = d > radius - ring ? Color.white : fill;
                if (!color.HasValue && d <= radius - ring && Mathf.Abs(dx + dy) < 4f) c = slash;
                c.a = alpha;
                pixels[y * size + x] = c;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _swatches[key] = sprite;
            return sprite;
        }

        // ── Looks ─────────────────────────────────────────────────────────────

        public static Look LocalLook()
        {
            var look = new Look();
            if (GlowEnabled) look.Glow = Effective(ColorEntry(ColorTarget.Glow)?.Value) ?? Effective(DefaultGlowHex);
            look.Style = Style;
            look.Tint = Effective(ColorEntry(ColorTarget.Cover)?.Value);
            for (int i = 0; i < look.Wizard.Length; i++)
                look.Wizard[i] = Effective(ColorEntry(ColorTarget.WizardTop + i)?.Value);
            return look;
        }

        public static Look RemoteLook(PlayerNetworked networked)
        {
            var look = new Look();
            foreach (var token in SkinOverride.RemoteFlagTokens(networked))
            {
                if (token.StartsWith(GlowToken, StringComparison.Ordinal))
                    look.Glow = Effective(token.Substring(GlowToken.Length));
                else if (token.StartsWith(TintToken, StringComparison.Ordinal))
                    look.Tint = Effective(token.Substring(TintToken.Length));
                else if (token.StartsWith(StyleToken, StringComparison.Ordinal))
                {
                    if (Enum.TryParse(token.Substring(StyleToken.Length), true, out JumpStyle style)) look.Style = style;
                }
                else if (token.StartsWith(WizardToken, StringComparison.Ordinal))
                {
                    var parts = token.Substring(WizardToken.Length).Split('.');
                    for (int i = 0; i < look.Wizard.Length && i < parts.Length; i++)
                        look.Wizard[i] = Effective(parts[i]);
                }
            }
            return look;
        }

        /// <summary>
        /// Appended to the flag SkinOverride publishes, so other clients rebuild this look. Colours go as
        /// the config writes them; a version without intensities reads plain "RRGGBB" values only.
        /// </summary>
        public static string LocalFlagTokens()
        {
            string tokens = "";
            if (GlowEnabled)
            {
                tokens += ";" + GlowToken + (ValidOrNull(ColorEntry(ColorTarget.Glow)?.Value) ?? DefaultGlowHex);
                if (Style != JumpStyle.Lines) tokens += ";" + StyleToken + Style.ToString().ToLowerInvariant();
            }
            string tint = ValidOrNull(ColorEntry(ColorTarget.Cover)?.Value);
            if (tint != null) tokens += ";" + TintToken + tint;

            var wizard = new string[3];
            for (int i = 0; i < wizard.Length; i++) wizard[i] = ValidOrNull(ColorEntry(ColorTarget.WizardTop + i)?.Value);
            if (wizard[0] != null || wizard[1] != null || wizard[2] != null)
                tokens += ";" + WizardToken + string.Join(".", Array.ConvertAll(wizard, w => w ?? ""));
            return tokens;
        }

        private static string ValidOrNull(string value) => TryParseColor(value, out _, out _) ? Normalize(value) : null;

        /// <summary>The colour a value stands for, intensity applied, or null for the game's own.</summary>
        private static Color? Effective(string value)
        {
            if (!TryParseColor(value, out var rgb, out float intensity)) return null;
            return new Color(rgb.r * intensity, rgb.g * intensity, rgb.b * intensity, 1f);
        }

        // ── Applying ──────────────────────────────────────────────────────────

        /// <summary>
        /// Brings one cover in line with <paramref name="look"/>. <paramref name="jump"/> is the player's jump
        /// shader driver, or null for the menu visuals (lit, as the Hangeris cover shows there).
        /// </summary>
        public static void Apply(SkinPrefabHandler handler, SkinAddressableLibrary library, Look look, PlayerJumpEnabledShader jump)
        {
            try
            {
                if (handler == null || handler.IsLoading) return;
                SkinInstanceRef instance;
                if (!handler.TryGetInstance(out instance) || instance == null) return;
                // The instance changes whenever the handler loads another skin.
                string state = $"{instance.Pointer}|{look.Key}|{_version}";
                if (_settled.TryGetValue(handler.Pointer, out var last) && last == state) return;

                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) return;

                Recolor(renderers, look, handler.CurrentSkinKey == WizardKey);

                if (!look.Glow.HasValue)
                {
                    ClearGlow(renderers, true, true);
                    _settled[handler.Pointer] = state;
                    return;
                }

                // Keypad on a cover with a light of its own (Hangeris) is that light, in the glow's colour
                // (Recolor). Any other style turns such a light off (Recolor) and adds its own.
                if (look.Style == JumpStyle.Keypad && HasNativeJumpLight(renderers))
                {
                    ClearGlow(renderers, true, true);
                    LogGlowMode(handler.CurrentSkinKey, "its own light, recoloured");
                    _settled[handler.Pointer] = state;
                    return;
                }

                // Outline where it was asked for, and on covers without the full body; the side lines
                // stand in for it on a full-body cover with no outline mesh.
                bool placed = false;
                if (look.Style != JumpStyle.Outline)
                {
                    if (!EnsureMask(library)) return;
                    placed = PlaceOverlays(renderers, look, jump);
                    if (placed) LogGlowMode(handler.CurrentSkinKey, look.Style.ToString());
                }
                if (!placed)
                {
                    placed = PlaceOutlines(renderers, look.Glow.Value, jump);
                    if (placed) LogGlowMode(handler.CurrentSkinKey, "outline");
                }
                if (!placed && look.Style == JumpStyle.Outline)
                {
                    if (!EnsureMask(library)) return;
                    placed = PlaceOverlays(renderers, look, jump);
                    if (placed) LogGlowMode(handler.CurrentSkinKey, "side lines (it has no outline)");
                }
                if (!placed) LogMeshMiss(handler.CurrentSkinKey, renderers);
                _settled[handler.Pointer] = state;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Cover extras error: {ex.Message}");
            }
        }

        // The overlay of the look's style on every full-body renderer; false if the cover has none.
        private static bool PlaceOverlays(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, Look look,
            PlayerJumpEnabledShader jump)
        {
            var template = look.Style == JumpStyle.Keypad ? _keypadTemplate : _linesTemplate;
            bool placed = false;
            for (int i = 0; i < renderers.Count; i++)
            {
                var cover = renderers[i];
                if (cover == null || cover.gameObject.name == GlowObjectName || !IsCoverBody(cover)) continue;
                EnsureOverlay(cover, look.Glow.Value, jump, template);
                placed = true;
            }
            if (placed) ClearGlow(renderers, false, true);
            return placed;
        }

        private static bool PlaceOutlines(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, Color color,
            PlayerJumpEnabledShader jump)
        {
            bool placed = false;
            for (int i = 0; i < renderers.Count; i++)
            {
                var outline = renderers[i];
                var mesh = outline != null ? MeshOf(outline) : null;
                if (mesh == null || mesh.name.IndexOf("_Outline", StringComparison.Ordinal) < 0) continue;
                EnsureOutlineGlow(outline, color, jump);
                placed = true;
            }
            if (placed) ClearGlow(renderers, true, false);
            return placed;
        }

        private static void ClearGlow(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, bool overlays, bool outlines)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] == null) continue;
                if (overlays) RemoveOverlay(renderers[i].Pointer);
                if (outlines) RemoveOutlineGlows(renderers[i]);
            }
        }

        /// <summary>Per frame, after the game wrote this frame's jump value. Call from LateUpdate.</summary>
        public static void TickFrame()
        {
            try { TickMaskLoad(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Jump glow mask failed: {ex.Message}");
                _maskState = MaskState.Failed;
                ReleaseLoad();
            }

            if (_overlays.Count > 0)
            {
                List<IntPtr> dead = null;
                foreach (var kv in _overlays)
                {
                    var o = kv.Value;
                    if (o.Cover == null || o.Root == null || o.Renderer == null)
                    {
                        (dead ??= new List<IntPtr>()).Add(kv.Key);
                        continue;
                    }
                    // The game writes the eased value it shows (owner: CanJump; observers: a blink per jump).
                    float value = o.Jump != null ? o.Jump.lastAppliedJumpEnabled : 1f;
                    bool on = value > 0.02f && o.Cover.enabled;
                    o.Renderer.enabled = on;
                    if (on && value != o.Value)
                    {
                        o.Material.SetFloat(JumpEnabledId, value);
                        o.Value = value;
                    }
                }
                if (dead != null) foreach (var key in dead) RemoveOverlay(key);
            }

            for (int i = _recolors.Count - 1; i >= 0; i--)
            {
                if (_recolors[i].Renderer != null) continue;
                UnityEngine.Object.Destroy(_recolors[i].Clone);
                _recolors.RemoveAt(i);
            }

            for (int i = _outlineGlows.Count - 1; i >= 0; i--)
            {
                var g = _outlineGlows[i];
                if (g.Renderer == null)
                {
                    UnityEngine.Object.Destroy(g.Clone);
                    _outlineGlows.RemoveAt(i);
                    continue;
                }
                float value = g.Jump != null ? g.Jump.lastAppliedJumpEnabled : 1f;
                // Most frames the jump is steadily on or off: nothing to rewrite.
                if (value == g.Value) continue;
                g.Value = value;
                for (int c = 0; c < g.ColorIds.Length; c++)
                {
                    var target = new Color(g.Color.r * GlowIntensity, g.Color.g * GlowIntensity, g.Color.b * GlowIntensity,
                        g.OriginalColors[c].a);
                    g.Clone.SetColor(g.ColorIds[c], Color.Lerp(g.OriginalColors[c], target, value));
                }
            }
        }

        /// <summary>
        /// Swaps the outline's material for a clone whose colour properties TickFrame blends towards the glow
        /// colour with the jump value. The outline shader's property names aren't known up front, so every
        /// Color property it declares is driven.
        /// </summary>
        private static void EnsureOutlineGlow(Renderer renderer, Color color, PlayerJumpEnabledShader jump)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null) return;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                var tracked = FindOutlineGlow(renderer, slot);
                if (tracked != null && materials[slot] != null && materials[slot].Pointer == tracked.Clone.Pointer)
                {
                    tracked.Jump = jump;
                    if (tracked.Color != color)
                    {
                        tracked.Color = color;
                        tracked.Value = float.NaN;
                    }
                    continue;
                }
                if (tracked != null)
                {
                    UnityEngine.Object.Destroy(tracked.Clone);
                    _outlineGlows.Remove(tracked);
                }

                var original = materials[slot];
                if (original == null || original.shader == null) continue;
                var shader = original.shader;
                var ids = new List<int>();
                var colors = new List<Color>();
                int count = shader.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    if (shader.GetPropertyType(p) != ShaderPropertyType.Color) continue;
                    int id = shader.GetPropertyNameId(p);
                    ids.Add(id);
                    colors.Add(original.GetColor(id));
                }
                if (ids.Count == 0) continue;

                var glow = new OutlineGlow
                {
                    Renderer = renderer, Slot = slot, Original = original,
                    Clone = UnityEngine.Object.Instantiate(original),
                    ColorIds = ids.ToArray(), OriginalColors = colors.ToArray(), Jump = jump, Color = color,
                };
                _outlineGlows.Add(glow);
                materials[slot] = glow.Clone;
                renderer.sharedMaterials = materials;
            }
        }

        private static OutlineGlow FindOutlineGlow(Renderer renderer, int slot)
        {
            foreach (var g in _outlineGlows)
                if (g.Slot == slot && g.Renderer != null && g.Renderer.Pointer == renderer.Pointer) return g;
            return null;
        }

        private static void RemoveOutlineGlows(Renderer renderer)
        {
            for (int i = _outlineGlows.Count - 1; i >= 0; i--)
            {
                var g = _outlineGlows[i];
                if (g.Renderer == null || g.Renderer.Pointer != renderer.Pointer) continue;
                var materials = renderer.sharedMaterials;
                if (materials != null && g.Slot < materials.Length && materials[g.Slot] != null
                    && materials[g.Slot].Pointer == g.Clone.Pointer)
                {
                    materials[g.Slot] = g.Original;
                    renderer.sharedMaterials = materials;
                }
                UnityEngine.Object.Destroy(g.Clone);
                _outlineGlows.RemoveAt(i);
            }
        }

        /// <summary>
        /// The phone's break effect clones the cover pieces into debris; a glow copied along would stay
        /// frozen in whatever state it had, so it is stripped from the clone.
        /// </summary>
        public static void StripFromDebris(Transform debris)
        {
            if (debris == null) return;
            var children = debris.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Count; i++)
                if (children[i] != null && children[i].name == GlowObjectName)
                    UnityEngine.Object.Destroy(children[i].gameObject);
        }

        private static bool IsCoverBody(Renderer renderer)
        {
            var mesh = MeshOf(renderer);
            if (mesh == null) return false;
            // The mask is laid out on the Hangeris body's UVs; a cover with a different body mesh but the
            // same vertex count is an export of the same model.
            return mesh.name == _bodyMeshName || mesh.vertexCount == _bodyVertexCount;
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned != null) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static void EnsureOverlay(Renderer cover, Color color, PlayerJumpEnabledShader jump, Material template)
        {
            if (_overlays.TryGetValue(cover.Pointer, out var existing) && existing.Root != null)
            {
                if (existing.Template == template)
                {
                    existing.Jump = jump;
                    if (existing.Color != color) SetGlowColor(existing, color);
                    return;
                }
                RemoveOverlay(cover.Pointer);
            }

            var root = new GameObject(GlowObjectName);
            root.layer = cover.gameObject.layer;
            root.transform.SetParent(cover.transform, false);

            Renderer renderer;
            var skinned = cover.TryCast<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                var copy = root.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = skinned.sharedMesh;
                copy.bones = skinned.bones;
                copy.rootBone = skinned.rootBone;
                renderer = copy;
            }
            else
            {
                root.AddComponent<MeshFilter>().sharedMesh = MeshOf(cover);
                renderer = root.AddComponent<MeshRenderer>();
            }

            var material = UnityEngine.Object.Instantiate(template);
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            var overlay = new Overlay { Cover = cover, Root = root, Renderer = renderer, Material = material, Jump = jump, Template = template };
            SetGlowColor(overlay, color);
            _overlays[cover.Pointer] = overlay;
        }

        private static void SetGlowColor(Overlay overlay, Color color)
        {
            overlay.Color = color;
            var hdr = new Color(color.r * GlowIntensity, color.g * GlowIntensity, color.b * GlowIntensity, 1f);
            // Whether the shader blends towards _ColorJump or adds it, both hold the same colour.
            overlay.Material.SetColor(EmissionColorId, hdr);
            overlay.Material.SetColor(ColorJumpId, hdr);
        }

        private static void RemoveOverlay(IntPtr coverKey)
        {
            if (!_overlays.TryGetValue(coverKey, out var overlay)) return;
            _overlays.Remove(coverKey);
            if (overlay.Root != null) UnityEngine.Object.Destroy(overlay.Root);
            if (overlay.Material != null) UnityEngine.Object.Destroy(overlay.Material);
        }

        private static void LogGlowMode(string key, string mode)
        {
            if (!string.IsNullOrEmpty(key) && _loggedGlowModes.Add(key + "|" + mode))
                Plugin.Log.LogInfo($"[Skin] ✓ Jump glow on '{key}': {mode}.");
        }

        private static void LogMeshMiss(string key, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers)
        {
            if (string.IsNullOrEmpty(key) || !_loggedMeshMisses.Add(key)) return;
            var names = new List<string>();
            for (int i = 0; i < renderers.Count; i++)
            {
                var mesh = renderers[i] != null ? MeshOf(renderers[i]) : null;
                if (mesh != null) names.Add($"{mesh.name} ({mesh.vertexCount} verts)");
            }
            Plugin.Log.LogWarning($"[Skin] Jump glow: no body like '{_bodyMeshName}' ({_bodyVertexCount} verts) on '{key}': {string.Join(", ", names)}");
        }

        // ── Cover colour ──────────────────────────────────────────────────────

        private static bool HasNativeJumpLight(Material m) =>
            m != null && m.HasProperty(ColorJumpId) && m.HasProperty(EmissiveTexId) && m.GetTexture(EmissiveTexId) != null
            && (m.IsKeywordEnabled(JumpLightKeyword) || m.HasProperty(JumpLightId) && m.GetFloat(JumpLightId) > 0.5f);

        private static bool HasNativeJumpLight(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer.gameObject.name == GlowObjectName) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;
                for (int slot = 0; slot < materials.Length; slot++)
                    if (HasNativeJumpLight(materials[slot])) return true;
            }
            return false;
        }

        /// <summary>
        /// Wizard: its three colours (the swirl keeps moving, only the palette changes). Any other cover
        /// on SG_Player_MSAO: _Tint. A cover's own jump light: the glow colour for the Keypad style, black
        /// for the others (they bring their own). Each changed slot gets its own material clone, restored when the look goes back
        /// to the game's.
        /// </summary>
        private static void Recolor(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, Look look, bool isWizard)
        {
            bool wizard = isWizard && (look.Wizard[0].HasValue || look.Wizard[1].HasValue || look.Wizard[2].HasValue);
            Color? jump = !look.Glow.HasValue ? null
                : look.Style != JumpStyle.Keypad ? Color.black
                : new Color(look.Glow.Value.r * GlowIntensity, look.Glow.Value.g * GlowIntensity, look.Glow.Value.b * GlowIntensity, 1f);
            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer.gameObject.name == GlowObjectName) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (FindOutlineGlow(renderer, slot) != null) continue;
                    var tracked = FindRecolor(renderer, slot);
                    if (tracked != null && (materials[slot] == null || materials[slot].Pointer != tracked.Clone.Pointer))
                    {
                        // Someone else (a repaint) replaced the slot; start over from what is there now.
                        UnityEngine.Object.Destroy(tracked.Clone);
                        _recolors.Remove(tracked);
                        tracked = null;
                    }

                    var original = tracked != null ? tracked.Original : materials[slot];
                    if (original == null || original.shader == null) continue;
                    bool isWizardMaterial = original.shader.name == WizardShader;
                    bool wantWizard = wizard && isWizardMaterial;
                    bool wantTint = !isWizardMaterial && look.Tint.HasValue && original.HasProperty(TintId) && !IsCustomPainted(original);
                    bool wantJump = jump.HasValue && HasNativeJumpLight(original);

                    if (!wantWizard && !wantTint && !wantJump)
                    {
                        if (tracked == null) continue;
                        materials[slot] = tracked.Original;
                        renderer.sharedMaterials = materials;
                        UnityEngine.Object.Destroy(tracked.Clone);
                        _recolors.Remove(tracked);
                        continue;
                    }

                    if (tracked == null)
                    {
                        tracked = new RecolorClone
                        {
                            Renderer = renderer, Slot = slot, Original = original,
                            Clone = UnityEngine.Object.Instantiate(original),
                        };
                        _recolors.Add(tracked);
                        materials[slot] = tracked.Clone;
                        renderer.sharedMaterials = materials;
                    }

                    // The clone outlives look changes, so every property it could hold is rewritten, back to
                    // the original's value when no longer wanted.
                    if (isWizardMaterial)
                    {
                        for (int c = 0; c < WizardColorIds.Length; c++)
                        {
                            // The game's alpha is kept: the shader authors it per colour (0 for top/bottom).
                            var source = original.GetColor(WizardColorIds[c]);
                            var target = wantWizard && look.Wizard[c].HasValue ? look.Wizard[c].Value : source;
                            tracked.Clone.SetColor(WizardColorIds[c], new Color(target.r, target.g, target.b, source.a));
                        }
                    }
                    else if (original.HasProperty(TintId))
                    {
                        var source = original.GetColor(TintId);
                        var t = wantTint ? look.Tint.Value : Color.white;
                        tracked.Clone.SetColor(TintId, new Color(source.r * t.r, source.g * t.g, source.b * t.b, source.a));
                    }
                    if (original.HasProperty(ColorJumpId))
                        tracked.Clone.SetColor(ColorJumpId, wantJump ? jump.Value : original.GetColor(ColorJumpId));
                }
            }
        }

        private static bool IsCustomPainted(Material material)
        {
            var texture = material.HasProperty(MainTexId) ? material.GetTexture(MainTexId) : null;
            if (texture == null) return false;
            string name = texture.name ?? "";
            foreach (var prefix in s_customTexturePrefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static RecolorClone FindRecolor(Renderer renderer, int slot)
        {
            foreach (var clone in _recolors)
                if (clone.Slot == slot && clone.Renderer != null && clone.Renderer.Pointer == renderer.Pointer) return clone;
            return null;
        }

        // ── Mask (from the Hangeris cover) ────────────────────────────────────

        /// <summary>True once the glow material is built; otherwise starts or continues building it.</summary>
        private static bool EnsureMask(SkinAddressableLibrary library)
        {
            if (_maskState == MaskState.Ready) return true;
            if (_maskState != MaskState.Idle && _maskState != MaskState.Requested) return false;
            if (library == null) return false;

            if (!library.TryGetLoaded(HangerisKey, out var skin, false) || skin == null)
            {
                // The menu library only preloads equipped keys.
                if (_maskState == MaskState.Idle) library.LoadOneAsync(HangerisKey);
                _maskState = MaskState.Requested;
                return false;
            }

            var reference = skin.PrefabReference;
            if (reference == null || !reference.RuntimeKeyIsValid())
            {
                Plugin.Log.LogWarning("[Skin] Jump glow: the Hangeris cover has no prefab reference.");
                _maskState = MaskState.Failed;
                return false;
            }
            _loadHandle = reference.InstantiateAsync(LoadPosition, Quaternion.identity, null);
            _loadStart = Time.time;
            _maskState = MaskState.Loading;
            return false;
        }

        private static void TickMaskLoad()
        {
            if (_maskState != MaskState.Loading) return;
            if (!_loadHandle.IsDone)
            {
                if (Time.time - _loadStart > LoadTimeout) throw new TimeoutException("Hangeris cover never loaded");
                return;
            }
            if (_loadHandle.Status != AsyncOperationStatus.Succeeded || _loadHandle.Result == null)
                throw new InvalidOperationException("Hangeris cover failed to load");

            var renderers = _loadHandle.Result.GetComponentsInChildren<Renderer>(true);
            Material source = null;
            for (int i = 0; i < renderers.Count && source == null; i++)
            {
                var materials = renderers[i]?.sharedMaterials;
                if (materials == null) continue;
                for (int j = 0; j < materials.Length; j++)
                {
                    var m = materials[j];
                    if (m == null || !m.HasProperty(JumpEnabledId) || m.GetTexture(EmissiveTexId) == null) continue;
                    source = m;
                    var mesh = MeshOf(renderers[i]);
                    _bodyMeshName = mesh != null ? mesh.name : "";
                    _bodyVertexCount = mesh != null ? mesh.vertexCount : -1;
                    break;
                }
            }
            if (source == null) throw new InvalidOperationException("no jump-lit material on the Hangeris cover");

            BuildMasks(source.GetTexture(EmissiveTexId), out var lines, out var keypad);
            _linesTemplate = MakeGlowTemplate(source, lines, "CharmReplacer_JumpGlowLines");
            _keypadTemplate = MakeGlowTemplate(source, keypad, "CharmReplacer_JumpGlowKeypad");

            ReleaseLoad();
            _maskState = MaskState.Ready;
            Plugin.Log.LogInfo($"[Skin] ✓ Jump glow ready (body mesh '{_bodyMeshName}', {_bodyVertexCount} verts).");
        }

        private static Material MakeGlowTemplate(Material source, Texture2D mask, string name)
        {
            var template = UnityEngine.Object.Instantiate(source);
            template.name = name;
            template.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // Lines only: black base so just the emission shows, everything off the mask clipped away.
            template.SetTexture(MainTexId, mask);
            template.SetTexture(EmissiveTexId, mask);
            template.SetColor(TintId, Color.black);
            template.SetFloat(AlphaClipFloatId, 0.5f);
            // The Hangeris normal/MSAO maps go away with its bundle.
            template.SetTexture(NormalTexId, null);
            template.SetTexture(MsaoTexId, null);
            template.SetFloat(MetallicId, 0f);
            template.SetFloat(SmoothnessId, 0f);
            template.SetFloat(FresnelOnId, 0f);
            template.renderQueue = GlowRenderQueue;
            return template;
        }

        private static void ReleaseLoad()
        {
            if (_loadHandle.IsValid()) Addressables.ReleaseInstance(_loadHandle);
            _loadHandle = default;
        }

        /// <summary>
        /// The jump-lit part of the Hangeris emission map (its alpha): without the phone's keys for Lines,
        /// whole for Keypad. The texture is GPU-only, so it is read back through a RenderTexture.
        /// </summary>
        private static void BuildMasks(Texture source, out Texture2D lines, out Texture2D keypad)
        {
            int w = source.width, h = source.height;
            var pixels = SkinLoader.ReadPixels(source);

            var keep = StripKeys(pixels, w, h, out int keys, out int pieces);
            var linePixels = new Color32[w * h];
            var keypadPixels = new Color32[w * h];
            for (int i = 0; i < linePixels.Length; i++)
            {
                byte a = pixels[i].a;
                byte v = keep[i] ? a : (byte)0;
                linePixels[i] = new Color32(v, v, v, v);
                keypadPixels[i] = new Color32(a, a, a, a);
            }
            Plugin.Log.LogInfo($"[Skin] Jump glow mask {w}x{h}: removed {keys} key(s), kept {pieces} line piece(s).");
            DumpMask(pixels, linePixels, w, h);
            lines = MaskTexture(linePixels, w, h, "CharmReplacer_JumpGlowMask");
            keypad = MaskTexture(keypadPixels, w, h, "CharmReplacer_JumpGlowKeypadMask");
        }

        private static Texture2D MaskTexture(Color32[] pixels, int w, int h, string name)
        {
            var mask = new Texture2D(w, h, TextureFormat.RGBA32, true);
            mask.name = name;
            mask.hideFlags = HideFlags.DontUnloadUnusedAsset;
            mask.SetPixels32(pixels);
            mask.Apply(true, true);
            return mask;
        }

        /// <summary>
        /// Lit alpha pixels that survive: not within KeyRingWidth of a key hole (a small unlit region with a
        /// printed symbol in it), and not in a crumb smaller than MinIslandArea once the keys are gone.
        /// </summary>
        private static bool[] StripKeys(Color32[] pixels, int w, int h, out int keyCount, out int lineCount)
        {
            int n = w * h;
            var lit = new bool[n];
            var symbol = new bool[n];
            for (int i = 0; i < n; i++)
            {
                lit[i] = pixels[i].a > 128;
                symbol[i] = !lit[i] && Math.Max(pixels[i].r, Math.Max(pixels[i].g, pixels[i].b)) > 32;
            }

            // Holes: 4-connected unlit regions.
            var hole = new int[n];
            var isKey = new List<bool> { false };
            var stack = new Stack<int>();
            int keyMax = Mathf.RoundToInt(KeyHoleMaxSize * Math.Max(w, h));
            keyCount = 0;
            for (int start = 0; start < n; start++)
            {
                if (lit[start] || hole[start] != 0) continue;
                int id = isKey.Count;
                int minX = w, minY = h, maxX = -1, maxY = -1;
                bool hasSymbol = false;
                hole[start] = id;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int px = p % w, py = p / w;
                    hasSymbol |= symbol[p];
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                    if (px > 0 && !lit[p - 1] && hole[p - 1] == 0) { hole[p - 1] = id; stack.Push(p - 1); }
                    if (px < w - 1 && !lit[p + 1] && hole[p + 1] == 0) { hole[p + 1] = id; stack.Push(p + 1); }
                    if (py > 0 && !lit[p - w] && hole[p - w] == 0) { hole[p - w] = id; stack.Push(p - w); }
                    if (py < h - 1 && !lit[p + w] && hole[p + w] == 0) { hole[p + w] = id; stack.Push(p + w); }
                }
                bool key = hasSymbol && Math.Max(maxX - minX + 1, maxY - minY + 1) <= keyMax;
                isKey.Add(key);
                if (key) keyCount++;
            }

            var inKey = new bool[n];
            for (int i = 0; i < n; i++) inKey[i] = hole[i] != 0 && isKey[hole[i]];
            var nearKey = Dilate(inKey, w, h, Mathf.Max(1, Mathf.RoundToInt(KeyRingWidth * Math.Max(w, h))));

            var keep = new bool[n];
            for (int i = 0; i < n; i++) keep[i] = lit[i] && !nearKey[i];

            var seen = new bool[n];
            int minIsland = Mathf.RoundToInt(MinIslandArea * n);
            lineCount = 0;
            var members = new List<int>();
            for (int start = 0; start < n; start++)
            {
                if (!keep[start] || seen[start]) continue;
                members.Clear();
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    members.Add(p);
                    int px = p % w, py = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int q = ny * w + nx;
                        if (seen[q] || !keep[q]) continue;
                        seen[q] = true;
                        stack.Push(q);
                    }
                }
                if (members.Count < minIsland) foreach (int p in members) keep[p] = false;
                else lineCount++;
            }
            return keep;
        }

        // Square dilation by running counts, rows then columns.
        private static bool[] Dilate(bool[] source, int w, int h, int radius)
        {
            var rows = new bool[source.Length];
            for (int y = 0; y < h; y++)
            {
                int run = 0;
                for (int x = -radius; x < w; x++)
                {
                    int add = x + radius, sub = x - radius - 1;
                    if (add < w && source[y * w + add]) run++;
                    if (sub >= 0 && source[y * w + sub]) run--;
                    if (x >= 0) rows[y * w + x] = run > 0;
                }
            }
            var result = new bool[source.Length];
            for (int x = 0; x < w; x++)
            {
                int run = 0;
                for (int y = -radius; y < h; y++)
                {
                    int add = y + radius, sub = y - radius - 1;
                    if (add < h && rows[add * w + x]) run++;
                    if (sub >= 0 && rows[sub * w + x]) run--;
                    if (y >= 0) result[y * w + x] = run > 0;
                }
            }
            return result;
        }

        // The source and the result, to check by eye what the key rule removed.
        private static void DumpMask(Color32[] source, Color32[] mask, int w, int h)
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.CachePath, "CharmReplacer");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "jump_glow_source.png"), SkinLoader.EncodePng(source, w, h));
                File.WriteAllBytes(Path.Combine(dir, "jump_glow_mask.png"), SkinLoader.EncodePng(mask, w, h));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Could not write the jump glow mask: {ex.Message}");
            }
        }

        // ── Colours ───────────────────────────────────────────────────────────

        private static string Normalize(string hex) => (hex ?? "").Trim().TrimStart('#').ToUpperInvariant();

        /// <summary>"RRGGBB" (intensity 100 %) or "RRGGBB:NNN".</summary>
        private static bool TryParseColor(string text, out Color rgb, out float intensity)
        {
            intensity = 1f;
            text = Normalize(text);
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                if (!int.TryParse(text.Substring(colon + 1), out int percent)) { rgb = default; return false; }
                intensity = Mathf.Clamp(percent, 0, MaxIntensityPercent) / 100f;
                text = text.Substring(0, colon);
            }
            return TryParseHex(text, out rgb);
        }

        private static string FormatColor(Color rgb, float intensity)
        {
            int percent = Mathf.Clamp(Mathf.RoundToInt(intensity * 100f), 0, MaxIntensityPercent);
            return percent == 100 ? ToHex(rgb) : $"{ToHex(rgb)}:{percent}";
        }

        private static bool TryParseHex(string text, out Color color)
        {
            color = default;
            text = Normalize(text);
            if (text.Length != 6) return false;
            if (!int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
            color = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
            return true;
        }

        private static string ToHex(Color color) =>
            $"{Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255):X2}{Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255):X2}{Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255):X2}";
    }
}
