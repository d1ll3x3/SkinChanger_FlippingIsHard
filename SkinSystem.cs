using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CharmReplacer
{
    internal static class SkinSystem
    {
        private static readonly string[] s_baseMapProps = { "_MainTex", "_BaseMap", "_BaseColorMap" };
        private static readonly string[] s_tintProps = { "_Color", "_Tint", "_BaseColor", "_LineColor" };
        private static readonly string[] s_emissionColorProps = { "_EmissionColor", "_EmissiveColor" };
        private static readonly string[] s_emissionFloatProps =
            { "_EmissionIntensity", "_EmissiveIntensity", "_EmissionStrength", "_EmissionPower", "_UseEmission", "_EnableEmission" };

        public static void ApplySkinTexture(Material mat, Texture baseTex, Texture emissionTex)
        {
            _paintedEmission[mat.GetInstanceID()] = emissionTex != null ? emissionTex.GetInstanceID() : 0;

            foreach (string propName in s_baseMapProps)
            {
                if (mat.HasProperty(propName))
                    mat.SetTexture(propName, baseTex);
            }

            // Force the base color to white so the game's default tint doesn't overwrite or hide our custom texture
            foreach (string propName in s_tintProps)
            {
                if (mat.HasProperty(propName))
                    mat.SetColor(propName, Color.white);
            }

            if (emissionTex == null) return;

            foreach (string propName in s_emissionMapProps)
            {
                if (mat.HasProperty(propName))
                    mat.SetTexture(propName, emissionTex);
            }

            foreach (string propName in s_emissionColorProps)
            {
                if (mat.HasProperty(propName))
                    mat.SetColor(propName, Color.white * 2.0f);
            }

            foreach (string propName in s_emissionFloatProps)
            {
                if (mat.HasProperty(propName))
                    mat.SetFloat(propName, 1.0f);
            }

            try
            {
                mat.EnableKeyword("_EMISSION");
                mat.EnableKeyword("EMISSION_ON");
                mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
                mat.EnableKeyword("_EMISSIONMAP");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            catch { }

            ApplyCachedEmissionProps(mat, emissionTex);
        }

        // Cache emission-related property indices per shader so we only scan once.
        private static readonly Dictionary<int, int[]> _shaderEmissionPropCache = new Dictionary<int, int[]>();

        // Cache IsMaterialPhoneDefault results per material instanceId.
        // Cleared on scene change (materials are replaced, instanceIds change anyway).
        private static readonly Dictionary<int, bool> _materialPhoneDefaultCache = new Dictionary<int, bool>();

        // Emission texture each painted clone received (0 = none), by material instanceId. When the
        // shader has none of the slots EmissionApplied probes by name, a painted clone never looked
        // done and every scan cloned it again (log: "8 phone source material(s) updated" per scan).
        private static readonly Dictionary<int, int> _paintedEmission = new Dictionary<int, int>();

        // Textures (base, emission) each player's break effect was pre-painted with, by handler.
        private static readonly Dictionary<IntPtr, (int, int)> _debrisPainted = new Dictionary<IntPtr, (int, int)>();

        /// <summary>Clear all per-scene material caches. Call from OnSceneLoaded.</summary>
        public static void ClearMaterialCache()
        {
            _debrisPainted.Clear();
            _materialPhoneDefaultCache.Clear();
            _shaderEmissionPropCache.Clear();
            _paintedEmission.Clear();
        }

        private static void ApplyCachedEmissionProps(Material mat, Texture emissionTex)
        {
            if (mat == null || emissionTex == null) return;
            Shader shader = null;
            try { shader = mat.shader; } catch { }
            if (shader == null) return;

            int shaderId = shader.GetInstanceID();
            if (!_shaderEmissionPropCache.TryGetValue(shaderId, out var emissionIndices))
            {
                // First time seeing this shader: scan its properties once
                var found = new List<int>();
                try
                {
                    int count = shader.GetPropertyCount();
                    for (int i = 0; i < count; i++)
                    {
                        string propName;
                        try { propName = shader.GetPropertyName(i); }
                        catch { continue; }
                        if (string.IsNullOrEmpty(propName)) continue;

                        string lower = propName.ToLowerInvariant();
                        if (lower.Contains("emission") || lower.Contains("emissive") || lower.Contains("glow"))
                            found.Add(i);
                    }
                }
                catch { }
                emissionIndices = found.ToArray();
                _shaderEmissionPropCache[shaderId] = emissionIndices;
            }

            // Apply using cached indices
            foreach (int i in emissionIndices)
            {
                try
                {
                    string propName = shader.GetPropertyName(i);
                    var propType = shader.GetPropertyType(i);
                    if (propType == ShaderPropertyType.Texture)
                        mat.SetTexture(propName, emissionTex);
                    else if (propType == ShaderPropertyType.Color)
                        mat.SetColor(propName, Color.white);
                    else if (propType == ShaderPropertyType.Float || propType == ShaderPropertyType.Range)
                        mat.SetFloat(propName, 1.0f);
                }
                catch { }
            }
        }

        // Strict whitelist: only the default exterior cover is replaced. Equipped cover skins
        // (M_Phone_<name>) are left untouched so the game's skin selector still works, and so is
        // M_Skin_Inside_Default (base/battery, visible when the phone breaks).
        private const string CoverMaterialName = "M_Skin_Cover_Default";

        public static bool IsMaterialPhoneDefault(Material mat)
        {
            if (mat == null) return false;

            int matId = mat.GetInstanceID();
            if (_materialPhoneDefaultCache.TryGetValue(matId, out bool cached))
                return cached;

            string name = BaseMaterialName(mat.name);
            bool result = name.Equals(CoverMaterialName, StringComparison.OrdinalIgnoreCase) || _modelCoverMaterials.Contains(name);
            _materialPhoneDefaultCache[matId] = result;
            return result;
        }

        // Without the "(Clone)" / " (Instance)" / " (1)" suffix of the game's runtime copies.
        private static string BaseMaterialName(string name)
        {
            name ??= string.Empty;
            int cut = name.IndexOf('(');
            return (cut >= 0 ? name.Substring(0, cut) : name).TrimEnd();
        }

        // Body materials of the covers picked as custom cover model (SkinOverride.CoverModel). Only covers
        // a player loads as their custom base get here, so the whitelist above still holds for everyone
        // wearing those covers natively: nothing paints a player without a custom skin.
        private static readonly HashSet<string> _modelCoverMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _registeredModels = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _unpaintableModels = new HashSet<string>(StringComparer.Ordinal);

        public static bool IsUnpaintableModel(string key) => _unpaintableModels.Contains(key);

        /// <summary>
        /// Lets the custom skin paint the cover <paramref name="handler"/> shows, a cover other than the
        /// default one: its body (the largest mesh, outline aside) takes the texture in every material with
        /// a base map. A cover with none (the Wizard's shader draws its own pattern) can't show the skin.
        /// </summary>
        public static void RegisterCoverModel(EHS.Skins.SkinPrefabHandler handler)
        {
            string key = handler.CurrentSkinKey;
            if (string.IsNullOrEmpty(key) || _registeredModels.Contains(key)) return;
            if (!handler.TryGetInstance(out var instance) || instance == null) return;
            _registeredModels.Add(key);

            Renderer body = null;
            int bodyVertices = -1;
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                var skinned = renderer != null ? renderer.TryCast<SkinnedMeshRenderer>() : null;
                var filter = renderer != null && skinned == null ? renderer.GetComponent<MeshFilter>() : null;
                var mesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.name.IndexOf("_Outline", StringComparison.Ordinal) >= 0 || mesh.vertexCount <= bodyVertices) continue;
                body = renderer;
                bodyVertices = mesh.vertexCount;
            }

            var names = new List<string>();
            var materials = body != null ? body.sharedMaterials : null;
            if (materials != null)
                foreach (var m in materials)
                    if (m != null && m.HasProperty("_MainTex")) names.Add(BaseMaterialName(m.name));

            if (names.Count == 0)
            {
                _unpaintableModels.Add(key);
                Plugin.Log.LogWarning($"[Skin] Cover '{key}' has no material the custom skin can paint; it shows its own look.");
                return;
            }
            foreach (var name in names) _modelCoverMaterials.Add(name);
            _materialPhoneDefaultCache.Clear();
            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
            Plugin.Log.LogInfo($"[Skin] ✓ Custom skin can paint cover '{key}' ({string.Join(", ", names)}).");
        }

        // Emission map property names ApplySkinTexture writes to. Shared so the
        // repaint check reads the same slots the painter sets.
        private static readonly string[] s_emissionMapProps =
            { "_EmissionMap", "_EmissiveColorMap", "_EmissionTex", "_EmissionMask" };

        /// <summary>True if the material already carries <paramref name="target"/> in any emission map slot.</summary>
        private static bool EmissionApplied(Material m, Texture target)
        {
            if (_paintedEmission.TryGetValue(m.GetInstanceID(), out int painted))
                return painted == target.GetInstanceID();
            foreach (var propName in s_emissionMapProps)
            {
                if (m.HasProperty(propName) && m.GetTexture(propName) == target)
                    return true;
            }
            return false;
        }

        public static int TryApplySkinToPhoneSourceMaterials()
        {
            if (CharmState.CustomSkinTexture == null && CharmState.PlayerBaseTextures.Count == 0) return 0;

            int replaced = 0;
            try
            {
                var localPlayer = PlayerIdentity.GetLocalPlayerGO();
                var allPlayers = PlayerIdentity.GetAllPlayers();
                foreach (var p in allPlayers)
                {
                    if (p == null) continue;

                    Texture2D targetTex = null;
                    Texture2D targetEmission = null;

                    // FIX: Reliable local player detection for both lobby and game
                    if (p == localPlayer)
                    {
                        // Switched off from the game's customization menu: keep the native cover.
                        if (SkinOverride.IsLocalEnabled(EHS.Skins.SkinCategory.Cover))
                        {
                            targetTex = CharmState.CustomSkinTexture;
                            targetEmission = CharmState.CustomSkinEmissionTexture;
                        }
                    }
                    else if (SkinOverride.AllowsRemoteCustom(p, EHS.Skins.SkinCategory.Cover))
                    {
                        string nick = PlayerIdentity.GetPlayerNickname(p);
                        if (!string.IsNullOrEmpty(nick) && CharmState.PlayerBaseTextures.TryGetValue(nick, out var pTex))
                        {
                            targetTex = pTex;
                            CharmState.PlayerEmissionTextures.TryGetValue(nick, out targetEmission);
                        }
                    }

                    if (targetTex == null) continue;

                    // Pre-paint the break effect so its pieces already carry the skin. Once per handler and
                    // texture: it walks every debris prefab, and the break patches repaint it anyway.
                    var debris = p.GetComponent<EHS.DestroyedPhoneEffectHandler>();
                    if (debris != null)
                    {
                        var painted = (targetTex.GetInstanceID(), targetEmission != null ? targetEmission.GetInstanceID() : 0);
                        if (!_debrisPainted.TryGetValue(debris.Pointer, out var last) || last != painted)
                        {
                            DebrisSystem.SkinDestroyedPhoneHandler(debris, targetTex, targetEmission);
                            _debrisPainted[debris.Pointer] = painted;
                        }
                    }

                    var renderers = p.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null) continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var ren = renderers[i];
                        if (ren == null) continue;

                        // Repaints when EITHER the base texture or the emission map is missing: remote
                        // assets arrive sequentially (base first, emission a moment after).
                        int painted = PaintRenderer(ren, targetTex, targetEmission);
                        if (painted == 0) continue;
                        CharmState.SkinnedRendererIds.Add(ren.GetInstanceID());
                        replaced += painted;
                    }

                    // Replaced > 0 is summarised by DoScanAndReplace's per-call counter.
                    // Only dump diagnostics when we genuinely never managed to paint
                    // anything for this player. Without the SkinnedRendererIds check,
                    // any follow-up scan that finds everything already painted would
                    // also flag replaced == 0 and spam the dump.
                    if (p == localPlayer && !CharmState.SkinEverPainted && !CharmState.SkinDebugLogged
                             && CharmState.SkinnedRendererIds.Count == 0)
                    {
                        Plugin.Log.LogWarning($"[Skin-Diag] No phone material found on local player! Dumping all renderers:");
                        foreach (var r in renderers)
                        {
                            if (r == null) continue;
                            string matNames = "";
                            var rmats = r.sharedMaterials;
                            if (rmats != null)
                            {
                                foreach (var m in rmats) matNames += (m != null ? m.name : "null") + ", ";
                            }
                            Plugin.Log.LogWarning($"   -> Renderer '{r.gameObject.name}' has mats: {matNames}");
                        }
                        CharmState.SkinDebugLogged = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Error en TryApplySkinToPhoneSourceMaterials: {ex.Message}");
            }

            if (replaced > 0)
            {
                if (!CharmState.SkinEverPainted)
                    Plugin.Log.LogInfo($"[Skin] ✓ Painted {replaced} phone renderer(s) on first apply.");
                CharmState.SkinEverPainted = true;
            }
            return replaced;
        }

        /// <summary>
        /// Paints the exterior cover materials of one renderer with a clone carrying the texture.
        /// Materials already carrying it (base and emission) are left alone, so repeated calls are cheap.
        /// </summary>
        internal static int PaintRenderer(Renderer ren, Texture2D tex, Texture2D emission)
        {
            // The getter hands back a copy, so it is edited in place and assigned only when changed.
            var mats = ren.sharedMaterials;
            if (mats == null) return 0;

            int painted = 0;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null || !IsMaterialPhoneDefault(mat)) continue;

                var currentTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                if (currentTex == tex && (emission == null || EmissionApplied(mat, emission))) continue;

                var clonedMat = UnityEngine.Object.Instantiate(mat);
                if (clonedMat == null) continue;
                clonedMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                ApplySkinTexture(clonedMat, tex, emission);
                mats[m] = clonedMat;
                painted++;
            }

            if (painted > 0)
            {
                ren.sharedMaterials = mats;
                CoverExtras.Invalidate();
            }
            return painted;
        }

        /// <summary>Paints every exterior cover under <paramref name="root"/> (customization preview, icon studio).</summary>
        public static int PaintPhoneCovers(GameObject root, Texture2D tex, Texture2D emission)
        {
            if (root == null || tex == null) return 0;
            int painted = 0;
            try
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) return 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null) painted += PaintRenderer(renderers[i], tex, emission);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Error painting '{root.name}': {ex.Message}");
            }
            return painted;
        }
    }
}
