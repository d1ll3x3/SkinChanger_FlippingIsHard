using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CharmReplacer
{
    internal static class SkinSystem
    {
        public static void ApplySkinTexture(Material mat, Texture baseTex, Texture emissionTex)
        {
            _paintedEmission[mat.GetInstanceID()] = emissionTex != null ? emissionTex.GetInstanceID() : 0;

            foreach (string propName in new[] { "_MainTex", "_BaseMap", "_BaseColorMap" })
            {
                if (mat.HasProperty(propName))
                    mat.SetTexture(propName, baseTex);
            }

            // Force the base color to white so the game's default tint doesn't overwrite or hide our custom texture
            foreach (string propName in new[] { "_Color", "_Tint", "_BaseColor", "_LineColor" })
            {
                if (mat.HasProperty(propName))
                    mat.SetColor(propName, Color.white);
            }

            if (emissionTex == null) return;

            foreach (string propName in new[] { "_EmissionMap", "_EmissiveColorMap", "_EmissionTex", "_EmissionMask" })
            {
                if (mat.HasProperty(propName))
                    mat.SetTexture(propName, emissionTex);
            }

            foreach (string propName in new[] { "_EmissionColor", "_EmissiveColor" })
            {
                if (mat.HasProperty(propName))
                    mat.SetColor(propName, Color.white * 2.0f);
            }

            foreach (string propName in new[] { "_EmissionIntensity", "_EmissiveIntensity", "_EmissionStrength", "_EmissionPower", "_UseEmission", "_EnableEmission" })
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

        /// <summary>Clear all per-scene material caches. Call from OnSceneLoaded.</summary>
        public static void ClearMaterialCache()
        {
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

        // Strict whitelist: only the default phone material is replaced. Cosmetic
        // variants (M_Phone_MissElephant, M_Phone_Base_Gold, M_Phone_Basic_Green, etc.)
        // are left untouched so the game's skin selector still works.
        public static bool IsMaterialPhoneDefault(Material mat)
        {
            if (mat == null) return false;

            int matId = mat.GetInstanceID();
            if (_materialPhoneDefaultCache.TryGetValue(matId, out bool cached))
                return cached;

            bool result = IsMaterialPhoneDefaultCore(mat);
            _materialPhoneDefaultCache[matId] = result;
            return result;
        }

        // Matches a material name to a default-phone name, tolerating a runtime "(Clone)" / "(Instance)"
        // or " (1)" suffix (the game clones phone materials at runtime via MaterialHandler2).
        private static bool MatchesDefaultPhoneName(string matName, string defaultName)
        {
            if (matName == defaultName) return true;
            if (matName.StartsWith(defaultName) &&
                matName.Length > defaultName.Length &&
                (matName[defaultName.Length] == ' ' || matName[defaultName.Length] == '(')) return true;
            return false;
        }

        private static bool IsMaterialPhoneDefaultCore(Material mat)
        {
            if (mat == null) return false;
            string matName = (mat.name ?? string.Empty).ToLowerInvariant();

            // Default phone surface we repaint: ONLY the exterior cover/casing.
            //   v1.7 rework: M_Skin_Cover_Default (front cover, shader "Elegant Horse Studios/SG_Player_MSAO").
            //   Legacy (<= 0.12): m_phone_basic_default.
            // NOT M_Skin_Inside_Default (base/battery = interior, visible when the phone breaks) — it is
            // deliberately left to fall through to the "inside" hard-reject below.
            if (MatchesDefaultPhoneName(matName, "m_phone_basic_default")) return true;
            if (MatchesDefaultPhoneName(matName, "m_skin_cover_default")) return true;

            // Hard rejects (screens, internals, outlines).
            if (matName.Contains("screen") || matName.Contains("display") || matName.Contains("glass")) return false;
            if (matName.Contains("inside")) return false;
            if (matName.Contains("outline")) return false;

            // Old version (0.9.15): the local player's currently-equipped phone material
            // has no asset name and uses the shader "Elegant Horse Studios/SG_Player_MSAO".
            // ALL cosmetics in old version share this shader and look identical by name,
            // so we can only tell them apart by their _MainTex. Accept only when the
            // texture matches the captured default OR is already our custom skin.
            bool isShaderFallback = matName.Contains("sg_player_msao");
            if (!isShaderFallback && string.IsNullOrEmpty(mat.name))
            {
                try
                {
                    var shader = mat.shader;
                    if (shader != null)
                    {
                        string shaderName = (shader.name ?? string.Empty).ToLowerInvariant();
                        if (shaderName.Contains("sg_player_msao")) isShaderFallback = true;
                    }
                }
                catch { }
            }

            if (isShaderFallback)
            {
                var tex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;

                // Already our painted clone — let the caller's currentTex == targetTex
                // check downstream short-circuit, but don't reject it here.
                if (tex != null && tex == CharmState.CustomSkinTexture) return true;

                // The texture is the captured default phone texture → user is on Default cosmetic.
                if (CharmState.DefaultPhoneTexture != null && tex == CharmState.DefaultPhoneTexture) return true;

                // Default not captured yet → allow first-pass paint so we don't miss the
                // initial skin application. The pre-pass in TryApplySkinToPhoneSourceMaterials
                // should populate DefaultPhoneTexture before this branch runs in practice.
                if (CharmState.DefaultPhoneTexture == null) return true;

                // Different texture → it's a non-default cosmetic. Leave it alone.
                return false;
            }

            return false;
        }

        // One-shot capture of the game's default phone texture from any
        // M_Phone_Basic_Default material currently present in the scene. v4_Base_LOD
        // renderers always carry this material regardless of equipped cosmetic.
        private static void CaptureDefaultPhoneTextureIfNeeded(GameObject player)
        {
            if (CharmState.DefaultPhoneTexture != null) return;
            if (player == null) return;

            const string defaultName = "m_phone_basic_default";
            var renderers = player.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var ren = renderers[i];
                if (ren == null) continue;
                var mats = ren.sharedMaterials;
                if (mats == null) continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;
                    string mn = (mat.name ?? string.Empty).ToLowerInvariant();
                    if (mn != defaultName &&
                        !(mn.StartsWith(defaultName) && mn.Length > defaultName.Length &&
                          (mn[defaultName.Length] == ' ' || mn[defaultName.Length] == '('))) continue;

                    if (!mat.HasProperty("_MainTex")) continue;
                    var tex = mat.GetTexture("_MainTex");
                    if (tex == null) continue;
                    if (tex == CharmState.CustomSkinTexture) continue;

                    CharmState.DefaultPhoneTexture = tex;
                    if (!CharmState.DefaultPhoneTextureLogged)
                    {
                        Plugin.Log.LogDebug($"[Skin] ✓ Default phone texture captured: '{tex.name}'");
                        CharmState.DefaultPhoneTextureLogged = true;
                    }
                    return;
                }
            }
        }

        // Emission map property names ApplySkinTexture writes to. Shared so the
        // re-skin guard checks the same slots the painter sets.
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

        private static bool RendererNeedsReSkin(Renderer ren, Texture2D targetTex, Texture2D targetEmission)
        {
            if (ren == null) return true;
            var mats = ren.sharedMaterials;
            if (mats == null) return false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                if (!IsMaterialPhoneDefault(m)) continue;
                var currentTex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                if (currentTex != targetTex) return true;
                // Emission arrives a moment after the base skin for remote players;
                // without this the renderer is marked "known" and the later-arriving
                // emission map is never applied.
                if (targetEmission != null && !EmissionApplied(m, targetEmission)) return true;
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

                    // Capture the game's default phone texture once per session so the
                    // old-version shader-fallback branch can distinguish Default from
                    // other cosmetics by _MainTex identity.
                    CaptureDefaultPhoneTextureIfNeeded(p);

                    // Pre-paint physics handler synchronously if it exists on the player
                    try
                    {
                        var allMbs = p.GetComponents<MonoBehaviour>();
                        foreach (var mb in allMbs)
                        {
                            if (mb == null) continue;
                            string typeName = mb.GetIl2CppType()?.Name ?? "";
                            if (typeName.Contains("DestroyedPhone") || typeName.Contains("Debris") || typeName.Contains("BrokenPhone"))
                            {
                                DebrisSystem.SkinDestroyedPhoneHandler(mb, targetTex, targetEmission);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Skin-Pre] Error pre-painting from scanner: {ex.Message}");
                    }

                    var renderers = p.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null || renderers.Length == 0) continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var ren = renderers[i];
                        if (ren == null) continue;

                        bool isKnown = CharmState.SkinnedRendererIds.Contains(ren.GetInstanceID());
                        if (isKnown)
                        {
                            if (!RendererNeedsReSkin(ren, targetTex, targetEmission)) continue;
                            CharmState.SkinnedRendererIds.Remove(ren.GetInstanceID());
                        }

                        var sharedMats = ren.sharedMaterials;
                        if (sharedMats == null || sharedMats.Length == 0) continue;

                        bool rendererUpdated = false;
                        var newMats = new Il2CppReferenceArray<Material>(sharedMats.Length);

                        for (int m = 0; m < sharedMats.Length; m++)
                        {
                            var mat = sharedMats[m];
                            newMats[m] = mat;

                            if (mat == null) continue;
                            if (!IsMaterialPhoneDefault(mat)) continue;

                            // Repaint when EITHER the base texture or the emission map is not yet
                            // applied. Checking only the base would skip a material whose base is
                            // already correct but whose emission arrived later (remote assets come
                            // in sequentially: base first, emission a moment after) — that was the
                            // "others' emission only after restart" bug.
                            var currentTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                            bool baseOk = currentTex == targetTex;
                            bool emissionOk = targetEmission == null || EmissionApplied(mat, targetEmission);
                            if (baseOk && emissionOk) continue;

                            var clonedMat = UnityEngine.Object.Instantiate(mat);
                            if (clonedMat == null) continue;
                            clonedMat.hideFlags = HideFlags.DontUnloadUnusedAsset;

                            ApplySkinTexture(clonedMat, targetTex, targetEmission);
                            newMats[m] = clonedMat;
                            rendererUpdated = true;
                            replaced++;
                        }

                        if (rendererUpdated)
                        {
                            ren.sharedMaterials = newMats;
                            CharmState.SkinnedRendererIds.Add(ren.GetInstanceID());
                        }
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
        private static int PaintRenderer(Renderer ren, Texture2D tex, Texture2D emission)
        {
            var sharedMats = ren.sharedMaterials;
            if (sharedMats == null || sharedMats.Length == 0) return 0;

            int painted = 0;
            var newMats = new Il2CppReferenceArray<Material>(sharedMats.Length);
            for (int m = 0; m < sharedMats.Length; m++)
            {
                var mat = sharedMats[m];
                newMats[m] = mat;
                if (mat == null) continue;

                // Only the exterior phone cover — same specific matcher used in-game. No broad
                // heuristic here, so we don't repaint menu backgrounds/reflections.
                if (!IsMaterialPhoneDefault(mat)) continue;

                var currentTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                if (currentTex == tex && (emission == null || EmissionApplied(mat, emission))) continue;

                var clonedMat = UnityEngine.Object.Instantiate(mat);
                if (clonedMat == null) continue;
                clonedMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                ApplySkinTexture(clonedMat, tex, emission);
                newMats[m] = clonedMat;
                painted++;
            }

            if (painted > 0) ren.sharedMaterials = newMats;
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

        public static int TryApplySkinToMainMenuPhonePreview()
        {
            if (!UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.Contains("MainMenu")) return 0;
            if (CharmState.CustomSkinTexture == null) return 0;
            if (!SkinOverride.IsLocalEnabled(EHS.Skins.SkinCategory.Cover)) return 0;

            int replaced = 0;
            try
            {
                var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                if (renderers == null) return 0;

                for (int i = 0; i < renderers.Length; i++)
                {
                    var ren = renderers[i];
                    if (ren == null) continue;

                    int rendererId;
                    try { rendererId = ren.GetInstanceID(); } catch { continue; }
                    if (CharmState.MenuPreviewRendererIds.Contains(rendererId)) continue;

                    int painted = PaintRenderer(ren, CharmState.CustomSkinTexture, CharmState.CustomSkinEmissionTexture);
                    if (painted > 0)
                    {
                        CharmState.MenuPreviewRendererIds.Add(rendererId);
                        replaced += painted;
                    }
                }

                if (replaced > 0)
                    Plugin.Log.LogInfo($"[Skin] ✓ Menu preview: painted {replaced} phone renderer(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Skin] Error applying skin al preview del menu: {ex.Message}");
            }

            return replaced;
        }

    }
}
