using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CharmReplacer
{
    internal static class SkinLoader
    {
        /// <summary>
        /// Load a PNG/JPG into a Unity Texture2D using LockBits for fast pixel access.
        /// LockBits is 5-10x faster than GetPixel() because it reads the entire
        /// bitmap buffer in one native call instead of crossing the managed boundary
        /// for every single pixel.
        /// </summary>
        public static Texture2D LoadTextureFromFile(string path, string texName)
        {
            if (!File.Exists(path)) return null;
            try
            {
#pragma warning disable CA1416
                using var bmp = new System.Drawing.Bitmap(path);
                int w = bmp.Width;
                int h = bmp.Height;

                // Lock the bitmap's bits for direct memory access
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                var pixels = new Color32[w * h];
                int stride = bmpData.Stride;

                // Copy rows. Bitmap is stored bottom-up; Unity expects top-down.
                // ARGB in memory = [B, G, R, A] on little-endian, Color32 is [r, g, b, a].
                byte[] row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    int srcY = h - 1 - y; // flip vertically
                    IntPtr srcRow = bmpData.Scan0 + srcY * stride;
                    Marshal.Copy(srcRow, row, 0, stride);

                    int destRowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        pixels[destRowBase + x] = new Color32(row[i + 2], row[i + 1], row[i + 0], row[i + 3]);
                    }
                }

                bmp.UnlockBits(bmpData);
#pragma warning restore CA1416

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                tex.name = texName;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return tex;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Skin] Error loading {path}: {ex.Message}");
                return null;
            }
        }

        public static void LoadCustomSkin()
        {
            string skinPath = Path.Combine(Plugin.PluginDirectory, "Skin.png");
            string emissionPath = Path.Combine(Plugin.PluginDirectory, "Skin_Emission.png");
            
            var mainTex = LoadTextureFromFile(skinPath, "CustomPlayerSkin");
            if (mainTex != null)
            {
                CharmState.CustomSkinTexture = mainTex;
                Plugin.Log.LogInfo($"[Skin] ✓ Main Skin.png loaded via System.Drawing");
                
                var emissionTex = LoadTextureFromFile(emissionPath, "CustomPlayerSkinEmission");
                if (emissionTex != null)
                {
                    CharmState.CustomSkinEmissionTexture = emissionTex;
                    Plugin.Log.LogInfo($"[Skin] ✓ Skin_Emission.png loaded via System.Drawing");
                }
            }

            string skinsDir = Path.Combine(Plugin.PluginDirectory, "Skins");
            if (Directory.Exists(skinsDir))
            {
                foreach (var file in Directory.GetFiles(skinsDir, "*.png", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.EndsWith("_emission", StringComparison.OrdinalIgnoreCase)) continue;

                    var tex = LoadTextureFromFile(file, $"skin_{fileName}");
                    if (tex != null)
                    {
                        CharmState.PlayerBaseTextures[fileName] = tex;
                        
                        string emFile = Path.Combine(Path.GetDirectoryName(file), fileName + "_emission.png");
                        var emTex = LoadTextureFromFile(emFile, $"skinemission_{fileName}");
                        if (emTex != null)
                        {
                            CharmState.PlayerEmissionTextures[fileName] = emTex;
                            Plugin.Log.LogInfo($"[Skin] ✓ Multiplayer skin loaded for: {fileName} (+ emission)");
                        }
                        else
                        {
                            Plugin.Log.LogInfo($"[Skin] ✓ Multiplayer skin loaded for: {fileName} (no emission file)");
                        }
                    }
                }
            }
            
            if (CharmState.CustomSkinTexture == null && CharmState.PlayerBaseTextures.Count == 0)
            {
                Plugin.Log.LogWarning($"[Skin] No custom skins found in Plugin directory or Skins/ folder.");
            }
        }
    }
}
