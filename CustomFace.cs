using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using EHS.Skins;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Shows Face.png on the local player's screen, whatever face skin is equipped. The game's face
    /// shader (SG_PlayerNPCScreen) reads a 4x4 atlas and its animations (idle, blink, talking, caller,
    /// broken) only pick the cell through _Row/_Column, so the image is repeated in every cell: any cell
    /// they pick shows it, and the face stays still. The screen base is set to white because the shader
    /// multiplies the face over it, so the image's own colours come through.
    /// Local only: there is no face asset on the backend for other players to download.
    /// </summary>
    internal static class CustomFace
    {
        private const string FileName = "Face.png";
        private const int AtlasCells = 4;
        private const int MaxCellSize = 512;

        private static readonly int FaceTextureId = Shader.PropertyToID("_FaceTexture");
        private static readonly int BaseTextureId = Shader.PropertyToID("_BaseTexture");

        private static ConfigEntry<bool> _enabled;
        private static bool _loadAttempted;
        private static Texture2D _atlas;
        private static Texture2D _white;
        private static Sprite _icon;

        private sealed class Swap
        {
            public Renderer Renderer;
            public int Slot;
            public Material Original;
        }

        // One clone per face material (LOD renderers share it); bounded by the number of face skins.
        private static readonly Dictionary<IntPtr, Material> _clones = new Dictionary<IntPtr, Material>();
        private static readonly List<Swap> _swaps = new List<Swap>();

        public static void Init(ConfigEntry<bool> enabled) => _enabled = enabled;

        public static bool HasAsset => EnsureLoaded();

        public static bool IsActive => _enabled != null && _enabled.Value && HasAsset;

        public static Sprite Icon => EnsureLoaded() ? _icon : null;

        public static void Toggle()
        {
            if (_enabled == null) return;
            _enabled.Value = !_enabled.Value;
            Plugin.Log.LogInfo($"[NativeMenu] Custom face {(_enabled.Value ? "enabled" : "disabled")}.");
        }

        /// <summary>Puts Face.png on a face handler's instance, or takes it back off when disabled.</summary>
        public static void Apply(SkinPrefabHandler handler)
        {
            try
            {
                if (handler == null || handler.IsLoading) return;
                SkinInstanceRef instance;
                if (!handler.TryGetInstance(out instance) || instance == null) return;
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) return;

                bool active = IsActive;
                for (int i = 0; i < renderers.Count; i++)
                {
                    var renderer = renderers[i];
                    var materials = renderer != null ? renderer.sharedMaterials : null;
                    if (materials == null) continue;
                    bool changed = false;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        var current = materials[slot];
                        var swap = FindSwap(renderer, slot);
                        if (swap != null && (current == null || !IsOurClone(current)))
                        {
                            // The game replaced the slot (a new face); track it afresh.
                            _swaps.Remove(swap);
                            swap = null;
                        }

                        if (!active)
                        {
                            if (swap == null) continue;
                            materials[slot] = swap.Original;
                            _swaps.Remove(swap);
                            changed = true;
                            continue;
                        }

                        if (swap != null || current == null || !current.HasProperty(FaceTextureId)) continue;
                        materials[slot] = CloneFor(current);
                        _swaps.Add(new Swap { Renderer = renderer, Slot = slot, Original = current });
                        changed = true;
                    }
                    if (changed) renderer.sharedMaterials = materials;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Custom face error: {ex.Message}");
            }
        }

        private static Material CloneFor(Material original)
        {
            if (_clones.TryGetValue(original.Pointer, out var clone) && clone != null) return clone;
            clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name + "_CharmReplacerFace";
            clone.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clone.SetTexture(FaceTextureId, _atlas);
            if (clone.HasProperty(BaseTextureId)) clone.SetTexture(BaseTextureId, _white);
            _clones[original.Pointer] = clone;
            return clone;
        }

        private static bool IsOurClone(Material material)
        {
            foreach (var clone in _clones.Values)
                if (clone != null && clone.Pointer == material.Pointer) return true;
            return false;
        }

        private static Swap FindSwap(Renderer renderer, int slot)
        {
            for (int i = _swaps.Count - 1; i >= 0; i--)
            {
                var swap = _swaps[i];
                if (swap.Renderer == null) { _swaps.RemoveAt(i); continue; }
                if (swap.Slot == slot && swap.Renderer.Pointer == renderer.Pointer) return swap;
            }
            return null;
        }

        // ── Loading ───────────────────────────────────────────────────────────

        private static bool EnsureLoaded()
        {
            if (_loadAttempted) return _atlas != null;
            _loadAttempted = true;

            string path = Path.Combine(Plugin.PluginDirectory, FileName);
            if (!File.Exists(path)) return false;
            try
            {
                var source = SkinLoader.LoadPixels(path, out int w, out int h);
                int cell = Math.Min(MaxCellSize, Math.Max(w, h));
                var cellPixels = Resample(source, w, h, cell, cell);

                int size = cell * AtlasCells;
                var atlasPixels = new Color32[size * size];
                for (int cy = 0; cy < AtlasCells; cy++)
                for (int cx = 0; cx < AtlasCells; cx++)
                for (int y = 0; y < cell; y++)
                    Array.Copy(cellPixels, y * cell, atlasPixels, (cy * cell + y) * size + cx * cell, cell);

                _atlas = new Texture2D(size, size, TextureFormat.RGBA32, true);
                _atlas.name = "CharmReplacer_FaceAtlas";
                _atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;
                // Clamp keeps each cell's edge from bleeding into the next one.
                _atlas.wrapMode = TextureWrapMode.Clamp;
                _atlas.SetPixels32(atlasPixels);
                _atlas.Apply(true, true);

                _white = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                _white.name = "CharmReplacer_FaceBase";
                _white.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var white = new Color32[16];
                for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
                _white.SetPixels32(white);
                _white.Apply(false, true);

                var iconTexture = new Texture2D(cell, cell, TextureFormat.RGBA32, false);
                iconTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                iconTexture.SetPixels32(cellPixels);
                iconTexture.Apply(false, true);
                _icon = Sprite.Create(iconTexture, new Rect(0, 0, cell, cell), new Vector2(0.5f, 0.5f), 100f);
                _icon.hideFlags = HideFlags.DontUnloadUnusedAsset;

                Plugin.Log.LogInfo($"[Skin] ✓ Face.png loaded ({w}x{h}, atlas {size}x{size}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Skin] Could not load {FileName}: {ex.Message}");
                _atlas = null;
                return false;
            }
        }

        // Bilinear resample; the image is stretched to the cell, as the screen shows one cell whole.
        private static Color32[] Resample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                float fy = Math.Max(0f, (y + 0.5f) * sh / dh - 0.5f);
                int y0 = Math.Min((int)fy, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
                float ty = fy - y0;
                for (int x = 0; x < dw; x++)
                {
                    float fx = Math.Max(0f, (x + 0.5f) * sw / dw - 0.5f);
                    int x0 = Math.Min((int)fx, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                    float tx = fx - x0;
                    dst[y * dw + x] = Lerp(Lerp(src[y0 * sw + x0], src[y0 * sw + x1], tx),
                                           Lerp(src[y1 * sw + x0], src[y1 * sw + x1], tx), ty);
                }
            }
            return dst;
        }

        private static Color32 Lerp(Color32 a, Color32 b, float t) => new Color32(
            (byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));
    }
}
