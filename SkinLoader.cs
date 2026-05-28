using System;
using System.IO;
using UnityEngine;

namespace CharmReplacer
{
    internal static class SkinLoader
    {
        public static Texture2D LoadTextureFromFile(string path, string texName)
        {
            if (!File.Exists(path)) return null;
            try
            {
#pragma warning disable CA1416
                using var bmp = new System.Drawing.Bitmap(path);
                int w = bmp.Width;
                int h = bmp.Height;

                var pixels = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    int srcY = h - 1 - y;
                    for (int x = 0; x < w; x++)
                    {
                        var px = bmp.GetPixel(x, srcY);
                        pixels[(y * w) + x] = new Color32(px.R, px.G, px.B, px.A);
                    }
                }
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
