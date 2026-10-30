using System;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CharmReplacer
{
    internal static class DebrisSystem
    {
        // Cached reflection handles — populated on first phone-break, reused forever.
        private static System.Reflection.PropertyInfo _piDefaultVisual;
        private static System.Reflection.PropertyInfo _piDestroyedVisual;
        private static System.Reflection.PropertyInfo _piLastSpawnedParts;
        private static System.Reflection.PropertyInfo _piDestroyedPartsPrefabs;

        /// <summary>Get (and cache) a PropertyInfo from the handler's type.</summary>
        private static System.Reflection.PropertyInfo GetProp(
            object handler, string name, ref System.Reflection.PropertyInfo cache)
        {
            if (cache != null) return cache;
            cache = handler.GetType().GetProperty(name);
            return cache;
        }

        private static int SkinRendererArray(Il2CppArrayBase<Renderer> renderers, Texture2D targetTex, Texture2D targetEmission)
        {
            if (renderers == null || targetTex == null) return 0;
            int count = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                var ren = renderers[i];
                if (ren == null) continue;
                if (ren.gameObject.name.IndexOf("charm", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                count += SkinSystem.PaintRenderer(ren, targetTex, targetEmission);
            }
            return count;
        }

        public static void SkinDestroyedPhoneHandler(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase handler, Texture2D targetTex, Texture2D targetEmission)
        {
            if (handler == null || targetTex == null) return;

            try
            {
                // Paint the shell visuals. Internal components (M_Phone_Insides) are filtered
                // out by SkinSystem.IsMaterialPhoneDefault so they keep their original textures.
                // PropertyInfo is cached statically — reflection only runs once per session.
                var piDV  = GetProp(handler, "defaultVisual",   ref _piDefaultVisual);
                var piDVs = GetProp(handler, "destroyedVisual", ref _piDestroyedVisual);

                for (int v = 0; v < 2; v++)
                {
                    var pi = v == 0 ? piDV : piDVs;
                    if (pi == null) continue;
                    var visualGo = pi.GetValue(handler) as GameObject;
                    if (visualGo == null) continue;
                    var visualRenderers = visualGo.GetComponentsInChildren<Renderer>(true);
                    int cnt = SkinRendererArray(visualRenderers, targetTex, targetEmission);
                    if (cnt > 0)
                        Plugin.Log.LogInfo($"[Skin-Pre] ✓ Visual '{pi.Name}' painted ({cnt} materiales).");
                }

                // Paint parts that just instantiated while flying (shell fragments).
                // Internal pieces (M_Phone_Insides) are filtered out by IsMaterialPhoneDefault.
                var piSpawned = GetProp(handler, "lastSpawnedParts", ref _piLastSpawnedParts);
                var spawnedParts = piSpawned?.GetValue(handler) as Il2CppSystem.Collections.Generic.List<GameObject>;
                if (spawnedParts != null)
                {
                    int count = 0;
                    for (int i = 0; i < spawnedParts.Count; i++)
                    {
                        var part = spawnedParts[i];
                        if (part == null) continue;
                        var renderers = part.GetComponentsInChildren<Renderer>(true);
                        count += SkinRendererArray(renderers, targetTex, targetEmission);
                    }
                    if (count > 0)
                        Plugin.Log.LogInfo($"[Skin-Pre] ✓ {count} materials applied to flying debris instantly.");
                }

                // Pre-paint prefabs so future spawns are already skinned.
                var piPrefabs = GetProp(handler, "destroyedPartsPrefabs", ref _piDestroyedPartsPrefabs);
                var prefabsList = piPrefabs?.GetValue(handler) as Il2CppSystem.Collections.Generic.List<GameObject>;
                if (prefabsList != null)
                {
                    for (int i = 0; i < prefabsList.Count; i++)
                    {
                        var prefab = prefabsList[i];
                        if (prefab == null) continue;
                        var prefabRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                        SkinRendererArray(prefabRenderers, targetTex, targetEmission);
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
