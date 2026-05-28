using System;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CharmReplacer
{
    internal static class DebrisSystem
    {
        private static int SkinRendererArray(Il2CppArrayBase<Renderer> renderers, string label, Texture2D targetTex, Texture2D targetEmission)
        {
            if (renderers == null || targetTex == null) return 0;
            int count = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                var ren = renderers[i];
                if (ren == null) continue;
                
                var name = ren.gameObject.name;
                if (name.IndexOf("charm", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var sharedMats = ren.sharedMaterials;
                if (sharedMats == null || sharedMats.Length == 0) continue;
                
                bool updated = false;
                var newMats = new Il2CppReferenceArray<Material>(sharedMats.Length);
                for (int m = 0; m < sharedMats.Length; m++)
                {
                    var mat = sharedMats[m];
                    newMats[m] = mat;
                    if (mat == null) continue;
                    if (!SkinSystem.IsMaterialPhoneDefault(mat)) continue;
                    
                    var currentTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                    if (currentTex == targetTex) continue;
                    
                    var cloned = UnityEngine.Object.Instantiate(mat);
                    if (cloned == null) continue;
                    cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    SkinSystem.ApplySkinTexture(cloned, targetTex, targetEmission);
                    newMats[m] = cloned;
                    updated = true;
                    count++;
                }
                if (updated)
                {
                    ren.sharedMaterials = newMats;
                }
            }
            return count;
        }

        public static void SkinDestroyedPhoneHandler(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase handler, Texture2D targetTex, Texture2D targetEmission)
        {
            if (handler == null || targetTex == null) return;

            try
            {
                // Paint the shell visuals (defaultVisual / destroyedVisual). The phone's
                // internal components ("M_Phone_Insides") are filtered out by
                // SkinSystem.IsMaterialPhoneDefault so they keep their original textures.
                foreach (var fieldName in new[] { "defaultVisual", "destroyedVisual" })
                {
                    var prop = handler.GetType().GetProperty(fieldName);
                    var visualGo = prop?.GetValue(handler) as GameObject;
                    if (visualGo != null)
                    {
                        var visualRenderers = visualGo.GetComponentsInChildren<Renderer>(true);
                        int cnt = SkinRendererArray(visualRenderers, "VISUAL_" + fieldName.ToUpper(), targetTex, targetEmission);
                        if (cnt > 0)
                        {
                            Plugin.Log.LogInfo($"[Skin-Pre] ✓ Visual '{fieldName}' painted ({cnt} materiales).");
                        }
                    }
                }

                // Paint parts that just instantiated while flying (shell fragments).
                // Internal pieces (M_Phone_Insides) are filtered out by IsMaterialPhoneDefault.
                var spawnedPartsProp = handler.GetType().GetProperty("lastSpawnedParts");
                var spawnedParts = spawnedPartsProp?.GetValue(handler) as Il2CppSystem.Collections.Generic.List<GameObject>;
                if (spawnedParts != null)
                {
                    int count = 0;
                    for (int i = 0; i < spawnedParts.Count; i++)
                    {
                        var part = spawnedParts[i];
                        if (part == null) continue;
                        var renderers = part.GetComponentsInChildren<Renderer>(true);
                        count += SkinRendererArray(renderers, $"SPAWNED_PART_{i}", targetTex, targetEmission);
                    }
                    if (count > 0)
                    {
                        Plugin.Log.LogInfo($"[Skin-Pre] ✓ {count} materials applied to flying debris instantly.");
                    }
                }

                // Pre-paint prefabs so future spawns are already skinned.
                var prefabsProp = handler.GetType().GetProperty("destroyedPartsPrefabs");
                var prefabsList = prefabsProp?.GetValue(handler) as Il2CppSystem.Collections.Generic.List<GameObject>;
                if (prefabsList != null)
                {
                    for (int i = 0; i < prefabsList.Count; i++)
                    {
                        var prefab = prefabsList[i];
                        if (prefab == null) continue;
                        var prefabRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                        SkinRendererArray(prefabRenderers, $"PREFAB_DEBRIS_{i}", targetTex, targetEmission);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin-Pre] Error painting DestroyedPhoneEffectHandler: {ex.Message}");
            }
        }
    }
}
