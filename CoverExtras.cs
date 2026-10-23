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

        // What the menu entries cycle through. Index 0 of the cover palette is the game's own look.
        private static readonly string[] s_coverPalette =
            { "", "FF2020", "FF8000", "FFE000", "30E040", "20E0E0", "2050FF", "9030FF", "FF40C0", "FFFFFF", "303030" };
        private static readonly string[] s_glowPalette =
            { "FF0000", "FF8000", "FFE000", "30FF40", "20F0FF", "2050FF", "A030FF", "FF40C0", "FFFFFF" };

        /// <summary>What to add to one cover. Null colours keep the game's look.</summary>
        internal sealed class Look
        {
            public Color? Glow;
            public Color? Tint;
            public readonly Color?[] Wizard = new Color?[3];
        }

        private static ConfigEntry<bool> _glowEnabled;
        private static ConfigEntry<string> _glowColor;
        private static ConfigEntry<string> _coverTint;
        private static ConfigEntry<string>[] _wizardColors;

        private enum MaskState { Idle, Requested, Loading, Ready, Failed }
        private static MaskState _maskState;
        private static AsyncOperationHandle<GameObject> _loadHandle;
        private static float _loadStart;
        private static Material _glowTemplate;
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

        public static void Init(ConfigEntry<bool> glowEnabled, ConfigEntry<string> glowColor, ConfigEntry<string> coverTint,
            ConfigEntry<string>[] wizardColors)
        {
            _glowEnabled = glowEnabled;
            _glowColor = glowColor;
            _coverTint = coverTint;
            _wizardColors = wizardColors;
        }

        // ── Menu ──────────────────────────────────────────────────────────────

        public static bool GlowEnabled => _glowEnabled != null && _glowEnabled.Value;

        public static bool CoverColored => TryParseHex(_coverTint?.Value, out _) || HasWizardColors();

        public static void ToggleGlow()
        {
            if (_glowEnabled == null) return;
            _glowEnabled.Value = !_glowEnabled.Value;
            Plugin.Log.LogInfo($"[NativeMenu] Jump glow {(_glowEnabled.Value ? "enabled" : "disabled")}.");
        }

        /// <summary>Next cover colour: the tint for every cover, and a matching theme for the Wizard.</summary>
        public static void CycleCoverColor()
        {
            if (_coverTint == null || _wizardColors == null) return;
            string next = NextInPalette(s_coverPalette, Normalize(_coverTint.Value));
            _coverTint.Value = next;
            if (TryParseHex(next, out var c))
            {
                // The game's Wizard runs dark at the top, mid at the bottom, the swirl in between.
                _wizardColors[0].Value = ToHex(c * 0.35f);
                _wizardColors[1].Value = ToHex(c * 0.75f);
                _wizardColors[2].Value = ToHex(Color.Lerp(c, Color.white, 0.25f) * 0.55f);
            }
            else
            {
                foreach (var entry in _wizardColors) entry.Value = "";
            }
            Plugin.Log.LogInfo($"[NativeMenu] Cover colour {(next.Length == 0 ? "original" : next)}.");
        }

        public static void CycleGlowColor()
        {
            if (_glowColor == null) return;
            _glowColor.Value = NextInPalette(s_glowPalette, Normalize(_glowColor.Value));
            if (_glowEnabled != null) _glowEnabled.Value = true;
            Plugin.Log.LogInfo($"[NativeMenu] Jump glow colour {_glowColor.Value}.");
        }

        /// <summary>Icon of the cover colour entry: the current colour, or white for the game's own.</summary>
        public static Sprite CoverColorSwatch() => Swatch(TryParseHex(_coverTint?.Value, out _) ? Normalize(_coverTint.Value) : "FFFFFF");

        public static Sprite GlowColorSwatch() => Swatch(TryParseHex(_glowColor?.Value, out _) ? Normalize(_glowColor.Value) : "FF0000");

        private static Sprite Swatch(string hex)
        {
            if (_swatches.TryGetValue(hex, out var cached) && cached != null) return cached;
            TryParseHex(hex, out var color);
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var pixels = new Color32[size * size];
            Color32 fill = color;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _swatches[hex] = sprite;
            return sprite;
        }

        private static string NextInPalette(string[] palette, string current)
        {
            int index = Array.IndexOf(palette, current);
            return palette[(index + 1) % palette.Length];
        }

        private static bool HasWizardColors()
        {
            if (_wizardColors == null) return false;
            foreach (var entry in _wizardColors) if (TryParseHex(entry?.Value, out _)) return true;
            return false;
        }

        // ── Looks ─────────────────────────────────────────────────────────────

        public static Look LocalLook()
        {
            var look = new Look();
            if (GlowEnabled && TryParseHex(_glowColor?.Value, out var glow)) look.Glow = glow;
            if (TryParseHex(_coverTint?.Value, out var tint)) look.Tint = tint;
            for (int i = 0; i < look.Wizard.Length; i++)
                if (_wizardColors != null && TryParseHex(_wizardColors[i]?.Value, out var c)) look.Wizard[i] = c;
            return look;
        }

        public static Look RemoteLook(PlayerNetworked networked)
        {
            var look = new Look();
            foreach (var token in SkinOverride.RemoteFlagTokens(networked))
            {
                if (token.StartsWith(GlowToken, StringComparison.Ordinal))
                {
                    if (TryParseHex(token.Substring(GlowToken.Length), out var glow)) look.Glow = glow;
                }
                else if (token.StartsWith(TintToken, StringComparison.Ordinal))
                {
                    if (TryParseHex(token.Substring(TintToken.Length), out var tint)) look.Tint = tint;
                }
                else if (token.StartsWith(WizardToken, StringComparison.Ordinal))
                {
                    var parts = token.Substring(WizardToken.Length).Split('.');
                    for (int i = 0; i < look.Wizard.Length && i < parts.Length; i++)
                        if (TryParseHex(parts[i], out var c)) look.Wizard[i] = c;
                }
            }
            return look;
        }

        /// <summary>Appended to the flag SkinOverride publishes, so other clients rebuild this look.</summary>
        public static string LocalFlagTokens()
        {
            var look = LocalLook();
            string tokens = look.Glow.HasValue ? ";" + GlowToken + ToHex(look.Glow.Value) : "";
            if (look.Tint.HasValue) tokens += ";" + TintToken + ToHex(look.Tint.Value);
            if (look.Wizard[0].HasValue || look.Wizard[1].HasValue || look.Wizard[2].HasValue)
            {
                tokens += ";" + WizardToken;
                for (int i = 0; i < look.Wizard.Length; i++)
                    tokens += (i > 0 ? "." : "") + (look.Wizard[i].HasValue ? ToHex(look.Wizard[i].Value) : "");
            }
            return tokens;
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
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) return;

                Recolor(renderers, look, handler.CurrentSkinKey == WizardKey);

                if (!look.Glow.HasValue)
                {
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        if (renderers[i] == null) continue;
                        RemoveOverlay(renderers[i].Pointer);
                        RemoveOutlineGlows(renderers[i]);
                    }
                    return;
                }

                if (!EnsureMask(library)) return;
                bool placed = false;
                for (int i = 0; i < renderers.Count; i++)
                {
                    var cover = renderers[i];
                    if (cover == null || cover.gameObject.name == GlowObjectName || !IsCoverBody(cover)) continue;
                    EnsureOverlay(cover, look.Glow.Value, jump);
                    placed = true;
                }
                if (placed)
                {
                    LogGlowMode(handler.CurrentSkinKey, "side lines");
                    return;
                }

                for (int i = 0; i < renderers.Count; i++)
                {
                    var outline = renderers[i];
                    var mesh = outline != null ? MeshOf(outline) : null;
                    if (mesh == null || mesh.name.IndexOf("_Outline", StringComparison.Ordinal) < 0) continue;
                    EnsureOutlineGlow(outline, look.Glow.Value, jump);
                    placed = true;
                }
                if (placed) LogGlowMode(handler.CurrentSkinKey, "outline");
                else LogMeshMiss(handler.CurrentSkinKey, renderers);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Cover extras error: {ex.Message}");
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
                    if (on) o.Material.SetFloat(JumpEnabledId, value);
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
                    tracked.Color = color;
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

        private static void EnsureOverlay(Renderer cover, Color color, PlayerJumpEnabledShader jump)
        {
            if (_overlays.TryGetValue(cover.Pointer, out var existing) && existing.Root != null)
            {
                existing.Jump = jump;
                if (existing.Color != color) SetGlowColor(existing, color);
                return;
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

            var material = UnityEngine.Object.Instantiate(_glowTemplate);
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            var overlay = new Overlay { Cover = cover, Root = root, Renderer = renderer, Material = material, Jump = jump };
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
            if (!string.IsNullOrEmpty(key) && _loggedGlowModes.Add(key))
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

        /// <summary>
        /// Wizard: its three colours (the swirl keeps moving, only the palette changes). Any other cover
        /// on SG_Player_MSAO: _Tint. Each recoloured slot gets its own material clone, restored when the
        /// look goes back to the game's colours.
        /// </summary>
        private static void Recolor(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers, Look look, bool isWizard)
        {
            bool wizard = isWizard && (look.Wizard[0].HasValue || look.Wizard[1].HasValue || look.Wizard[2].HasValue);
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

                    if (!wantWizard && !wantTint)
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

                    if (wantWizard)
                    {
                        for (int c = 0; c < WizardColorIds.Length; c++)
                        {
                            // The game's alpha is kept: the shader authors it per colour (0 for top/bottom).
                            var source = original.GetColor(WizardColorIds[c]);
                            var target = look.Wizard[c].HasValue ? look.Wizard[c].Value : source;
                            tracked.Clone.SetColor(WizardColorIds[c], new Color(target.r, target.g, target.b, source.a));
                        }
                    }
                    else
                    {
                        var source = original.GetColor(TintId);
                        var t = look.Tint.Value;
                        tracked.Clone.SetColor(TintId, new Color(source.r * t.r, source.g * t.g, source.b * t.b, source.a));
                    }
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

            var mask = BuildMask(source.GetTexture(EmissiveTexId));
            _glowTemplate = UnityEngine.Object.Instantiate(source);
            _glowTemplate.name = "CharmReplacer_JumpGlow";
            _glowTemplate.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // Lines only: black base so just the emission shows, everything off the mask clipped away.
            _glowTemplate.SetTexture(MainTexId, mask);
            _glowTemplate.SetTexture(EmissiveTexId, mask);
            _glowTemplate.SetColor(TintId, Color.black);
            _glowTemplate.SetFloat(AlphaClipFloatId, 0.5f);
            // The Hangeris normal/MSAO maps go away with its bundle.
            _glowTemplate.SetTexture(NormalTexId, null);
            _glowTemplate.SetTexture(MsaoTexId, null);
            _glowTemplate.SetFloat(MetallicId, 0f);
            _glowTemplate.SetFloat(SmoothnessId, 0f);
            _glowTemplate.SetFloat(FresnelOnId, 0f);
            _glowTemplate.renderQueue = GlowRenderQueue;

            ReleaseLoad();
            _maskState = MaskState.Ready;
            Plugin.Log.LogInfo($"[Skin] ✓ Jump glow ready (body mesh '{_bodyMeshName}', {_bodyVertexCount} verts).");
        }

        private static void ReleaseLoad()
        {
            if (_loadHandle.IsValid()) Addressables.ReleaseInstance(_loadHandle);
            _loadHandle = default;
        }

        /// <summary>
        /// The jump-lit part of the Hangeris emission map (its alpha) without the phone's keys. The texture is
        /// GPU-only, so it is read back through a RenderTexture.
        /// </summary>
        private static Texture2D BuildMask(Texture source)
        {
            int w = source.width, h = source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Color32[] pixels;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply(false, false);
                pixels = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }

            var keep = StripKeys(pixels, w, h, out int keys, out int lines);
            var maskPixels = new Color32[w * h];
            for (int i = 0; i < maskPixels.Length; i++)
            {
                byte v = keep[i] ? pixels[i].a : (byte)0;
                maskPixels[i] = new Color32(v, v, v, v);
            }
            Plugin.Log.LogInfo($"[Skin] Jump glow mask {w}x{h}: removed {keys} key(s), kept {lines} line piece(s).");
            DumpMask(pixels, maskPixels, w, h);

            var mask = new Texture2D(w, h, TextureFormat.RGBA32, true);
            mask.name = "CharmReplacer_JumpGlowMask";
            mask.hideFlags = HideFlags.DontUnloadUnusedAsset;
            mask.SetPixels32(maskPixels);
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
