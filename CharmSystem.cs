using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CharmReplacer
{
    internal static class CharmSystem
    {
        // --- Bundle loading state (queue-based for multiplayer) ---
        private static Queue<string> _charmBundleQueue = new Queue<string>();
        private static string _currentCharmPlayer = null; // null = local player, "Nick" = friend
        private static bool _isLoadingBundle = false;
        private static UnityEngine.Networking.UnityWebRequest _bundleRequest;
        private static UnityEngine.AssetBundleRequest _extractionRequest;
        private static UnityEngine.AssetBundle _extractingBundle;

        // --- Public API ---

        /// <summary>
        /// Start loading: local hatbundle first, then scan Charms/ folder for friend bundles.
        /// </summary>
        public static void LoadCharmMesh()
        {
            try
            {
                // 1) Local bundle
                string localPath = Path.Combine(Plugin.PluginDirectory, "hatbundle");
                if (File.Exists(localPath))
                {
                    _charmBundleQueue.Enqueue(localPath);
                }
                else
                {
                    Plugin.Log.LogWarning($"[Charm] hatbundle not found at: {localPath}");
                }

                // 2) Scan Charms/ folder for multiplayer bundles
                ScanMultiplayerCharms();

                // 3) Start processing queue
                StartNextBundle();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Charm] Error in LoadCharmMesh: {ex}");
            }
        }

        /// <summary>
        /// Called each frame from CharmReplacerBehavior.Update(). Processes async bundle queue.
        /// </summary>
        public static void UpdateBundleLoading()
        {
            if (!_isLoadingBundle) return;

            try
            {
                if (_bundleRequest != null)
                {
                    if (!_bundleRequest.isDone) return;

                    if (_bundleRequest.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        var bundle = UnityEngine.Networking.DownloadHandlerAssetBundle.GetContent(_bundleRequest);
                        if (bundle != null)
                        {
                            string label = _currentCharmPlayer ?? "local";
                            Plugin.Log.LogDebug($"[CharmBundle] ✓ Loaded bundle for '{label}'. Extracting assets...");
                            _extractionRequest = bundle.LoadAllAssetsAsync(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Object>());
                            _extractingBundle = bundle;
                        }
                    }
                    else
                    {
                        string label = _currentCharmPlayer ?? "local";
                        Plugin.Log.LogError($"[CharmBundle] WebRequest Error for '{label}': {_bundleRequest.error}");
                        _isLoadingBundle = false;
                        StartNextBundle();
                    }

                    _bundleRequest.Dispose();
                    _bundleRequest = null;
                }
                else if (_extractionRequest != null)
                {
                    if (!_extractionRequest.isDone) return;

                    var allAssets = _extractionRequest.allAssets;
                    string label = _currentCharmPlayer ?? "local";
                    Plugin.Log.LogDebug($"[CharmBundle] Extraction completed for '{label}': {allAssets?.Length ?? 0} objects.");

                    bool stored = ExtractAndStoreAssets(allAssets, _currentCharmPlayer);

                    _extractingBundle?.Unload(false);
                    _extractingBundle = null;
                    _extractionRequest = null;
                    _isLoadingBundle = false;

                    // If bundle loaded after scene was ready, trigger immediate scan
                    if (stored)
                        CharmReplacerBehavior.Instance?.TriggerImmediateReplacement();

                    // Process next bundle in queue
                    StartNextBundle();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CharmBundle] UpdateBundleLoading error: {ex}");
                _isLoadingBundle = false;
                StartNextBundle();
            }
        }

        /// <summary>
        /// Apply charm meshes to ALL players (local + remote), matching nickname to loaded bundles.
        /// </summary>
        public static void TryApplyCharmToMeshFilters()
        {
            // === LOCAL PLAYER (original proven logic, untouched) ===
            if (CharmState.CustomMesh != null)
            {
                try
                {
                    var playerGO = PlayerIdentity.GetLocalPlayerGO();
                    Il2CppArrayBase<MeshFilter> filters;
                    if (playerGO != null)
                        filters = playerGO.GetComponentsInChildren<MeshFilter>(true);
                    else
                        filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();

                    for (int i = 0; i < filters.Count; i++)
                    {
                        try
                        {
                            var mf = filters[i];
                            if (mf == null || mf.sharedMesh == null) continue;

                            string meshName = "";
                            try { meshName = mf.sharedMesh.name ?? ""; } catch { }

                            if (mf.sharedMesh == CharmState.CustomMesh) continue;
                            if (meshName.StartsWith("CustomGorraMesh")) continue;
                            if (!IsCharmTarget(mf.gameObject)) continue;

                            var mr = mf.GetComponent<MeshRenderer>();

                            if (CharmState.BundleMaterials != null && mr != null && CharmState.BundleMaterials.Length > 0)
                            {
                                var newMats = new Il2CppReferenceArray<Material>(CharmState.BundleMaterials.Length);
                                for (int m = 0; m < CharmState.BundleMaterials.Length; m++)
                                {
                                    var bundleMat = CharmState.BundleMaterials[m];
                                    if (bundleMat == null)
                                    {
                                        if (mr.sharedMaterials != null && m < mr.sharedMaterials.Length)
                                            newMats[m] = mr.sharedMaterials[m];
                                        continue;
                                    }
                                    var cloned = UnityEngine.Object.Instantiate(bundleMat);
                                    if (cloned != null)
                                    {
                                        cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                                        newMats[m] = cloned;
                                    }
                                    else if (mr.sharedMaterials != null && m < mr.sharedMaterials.Length)
                                    {
                                        newMats[m] = mr.sharedMaterials[m];
                                    }
                                }
                                mr.sharedMaterials = newMats;
                            }
                            else
                            {
                                if (CharmState.CapturedGameMaterials == null && mr != null
                                    && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0)
                                    CharmState.CapturedGameMaterials = mr.sharedMaterials;
                                if (CharmState.CapturedGameMaterials != null && mr != null)
                                    mr.sharedMaterials = CharmState.CapturedGameMaterials;
                            }

                            mf.sharedMesh = CharmState.CustomMesh;
                            Plugin.Log.LogInfo($"[Charm] Mesh replaced on '{mf.gameObject.name}'");
                            CharmState.LocalCharmApplied = true;

                            ResetMagicaCloth(mf);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[Charm] Error in MF[{i}]: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Local TryApplyCharmToMeshFilters error: {ex.Message}");
                }
            }

            // === REMOTE PLAYERS (multiplayer, dictionary lookup) ===
            if (CharmState.PlayerCharmMeshes.Count > 0)
            {
                try
                {
                    var localPlayer = PlayerIdentity.GetLocalPlayerGO();
                    var allPlayers = PlayerIdentity.GetAllPlayers();

                    foreach (var playerGO in allPlayers)
                    {
                        if (playerGO == null || playerGO == localPlayer) continue;

                        string nick = PlayerIdentity.GetPlayerNickname(playerGO);
                        if (string.IsNullOrEmpty(nick)) continue;
                        if (!CharmState.PlayerCharmMeshes.TryGetValue(nick, out var targetMesh)) continue;
                        CharmState.PlayerCharmMaterials.TryGetValue(nick, out var targetMaterials);

                        ApplyCharmToPlayer(playerGO, targetMesh, targetMaterials);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Remote TryApplyCharmToMeshFilters error: {ex.Message}");
                }
            }
        }

        // --- Internal ---

        /// <summary>
        /// Scan Charms/ folder and enqueue all .hatbundle files (name = player nickname).
        /// </summary>
        private static void ScanMultiplayerCharms()
        {
            string charmsDir = Path.Combine(Plugin.PluginDirectory, "Charms");
            if (!Directory.Exists(charmsDir)) return;

            foreach (var file in Directory.GetFiles(charmsDir, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                // Accept any file that could be a bundle (no extension restriction — same as local "hatbundle")
                // But skip anything that looks like an emission or skin texture
                string lower = fileName.ToLowerInvariant();
                if (lower.EndsWith(".png") || lower.EndsWith(".txt") || lower.EndsWith(".meta"))
                    continue;

                string playerName = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(playerName)) continue;

                _charmBundleQueue.Enqueue(file);
                Plugin.Log.LogDebug($"[Charm] ✓ Queued multiplayer bundle for: {playerName}");
            }
        }

        /// <summary>
        /// Start loading the next bundle from the queue. Sets _currentCharmPlayer.
        /// </summary>
        private static void StartNextBundle()
        {
            if (_charmBundleQueue.Count == 0)
            {
                Plugin.Log.LogInfo("[Charm] All bundles processed.");
                return;
            }

            string path = _charmBundleQueue.Dequeue();
            string fileName = Path.GetFileNameWithoutExtension(path);

            // Determine if this is the local bundle or a friend's
            string localName = Path.GetFileNameWithoutExtension(
                Path.Combine(Plugin.PluginDirectory, "hatbundle"));
            if (string.Equals(fileName, localName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(path), Plugin.PluginDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _currentCharmPlayer = null; // local
            }
            else
            {
                _currentCharmPlayer = fileName; // friend's nickname
            }

            string label = _currentCharmPlayer ?? "local";
            Plugin.Log.LogInfo($"[Charm] Loading bundle for '{label}': {path}");

            string uri = "file:///" + path.Replace("\\", "/");
            _bundleRequest = UnityEngine.Networking.UnityWebRequestAssetBundle.GetAssetBundle(uri);
            _bundleRequest.SendWebRequest();
            _isLoadingBundle = true;
        }

        /// <summary>
        /// Extract mesh and materials from bundle assets and store in the correct state slot.
        /// </summary>
        private static bool ExtractAndStoreAssets(Il2CppReferenceArray<UnityEngine.Object> allAssets, string playerName)
        {
            if (allAssets == null) return false;

            string label = playerName ?? "local";
            Mesh extractedMesh = null;
            Il2CppReferenceArray<Material> extractedMaterials = null;

            foreach (var obj in allAssets)
            {
                if (obj == null) continue;

                // Try direct Mesh
                var meshObj = obj.TryCast<Mesh>();
                if (meshObj != null && extractedMesh == null)
                {
                    extractedMesh = UnityEngine.Object.Instantiate(meshObj);
                    extractedMesh.name = $"CustomGorraMesh_{label}";
                    extractedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    Plugin.Log.LogDebug($"[CharmBundle] ✓ Mesh extracted for '{label}': '{extractedMesh.name}'");
                    continue;
                }

                // Try GameObject with MeshFilter
                var go = obj.TryCast<GameObject>();
                if (go != null && extractedMesh == null)
                {
                    var mf = go.GetComponent<MeshFilter>() ?? go.GetComponentInChildren<MeshFilter>();
                    if (mf?.sharedMesh != null)
                    {
                        extractedMesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
                        extractedMesh.name = $"CustomGorraMesh_{label}";
                        extractedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        Plugin.Log.LogDebug($"[CharmBundle] ✓ Mesh from GO for '{label}': '{extractedMesh.name}'");

                        var mr = go.GetComponent<MeshRenderer>() ?? go.GetComponentInChildren<MeshRenderer>();
                        if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0)
                        {
                            var newMats = new Il2CppReferenceArray<Material>(mr.sharedMaterials.Length);
                            for (int i = 0; i < mr.sharedMaterials.Length; i++)
                            {
                                var cloned = UnityEngine.Object.Instantiate(mr.sharedMaterials[i]);
                                cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                                newMats[i] = cloned;
                            }
                            extractedMaterials = newMats;
                            Plugin.Log.LogDebug($"[CharmBundle] ✓ Materials from GO for '{label}' ({extractedMaterials.Length})");
                        }
                    }
                }

                // Try direct Material
                if (extractedMaterials == null)
                {
                    var matObj = obj.TryCast<Material>();
                    if (matObj != null)
                    {
                        var clonedMat = UnityEngine.Object.Instantiate(matObj);
                        if (clonedMat != null)
                        {
                            clonedMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            extractedMaterials = new Il2CppReferenceArray<Material>(new Material[] { clonedMat });
                            Plugin.Log.LogDebug($"[CharmBundle] ✓ Material for '{label}': '{matObj.name}'");
                        }
                    }
                }
            }

            // Store in correct slot
            if (playerName == null)
            {
                // Local player
                if (extractedMesh != null) CharmState.CustomMesh = extractedMesh;
                if (extractedMaterials != null) CharmState.BundleMaterials = extractedMaterials;
            }
            else
            {
                // Friend
                if (extractedMesh != null)
                    CharmState.PlayerCharmMeshes[playerName] = extractedMesh;
                if (extractedMaterials != null)
                    CharmState.PlayerCharmMaterials[playerName] = extractedMaterials;
            }

            // Consolidated Info log
            if (extractedMesh != null || extractedMaterials != null)
            {
                string meshInfo = extractedMesh != null ? $"mesh: {extractedMesh.name}" : "no mesh";
                int matCount = extractedMaterials != null ? extractedMaterials.Length : 0;
                string matInfo = matCount > 0 ? $"{matCount} material(s)" : "no materials";
                Plugin.Log.LogInfo($"✓ Bundle '{label}' loaded ({meshInfo}, {matInfo})");
            }

            return extractedMesh != null || extractedMaterials != null;
        }

        // --- Charm target detection ---

        private static bool IsCharmTarget(GameObject go)
        {
            if (go == null) return false;

            try
            {
                string goName = (go.name ?? string.Empty).ToLowerInvariant();

                bool IsExactPhoneCharm(string name) =>
                    !string.IsNullOrEmpty(name) &&
                    name.Contains("charm_phone");

                if (IsExactPhoneCharm(goName))
                    return true;

                Transform parent = go.transform.parent;
                for (int d = 0; d < 3 && parent != null; d++)
                {
                    string pName = (parent.gameObject.name ?? string.Empty).ToLowerInvariant();
                    if (IsExactPhoneCharm(pName))
                        return true;
                    parent = parent.parent;
                }
            }
            catch { }

            return false;
        }

        // --- Apply charm to a single player ---

        private static void ApplyCharmToPlayer(GameObject playerGO, Mesh targetMesh, Il2CppReferenceArray<Material> targetMaterials)
        {
            var filters = playerGO.GetComponentsInChildren<MeshFilter>(true);
            if (filters == null) return;

            for (int i = 0; i < filters.Count; i++)
            {
                try
                {
                    var mf = filters[i];
                    if (mf == null || mf.sharedMesh == null) continue;

                    // Skip already-replaced filters
                    int instanceId = mf.GetInstanceID();
                    if (CharmState.CharmedMeshFilterIds.Contains(instanceId)) continue;
                    if (mf.sharedMesh == targetMesh) continue;

                    string meshName = "";
                    try { meshName = mf.sharedMesh.name ?? ""; } catch { }
                    if (meshName.StartsWith("CustomGorraMesh")) continue;

                    if (!IsCharmTarget(mf.gameObject)) continue;

                    var mr = mf.GetComponent<MeshRenderer>();

                    // Apply materials
                    if (targetMaterials != null && mr != null && targetMaterials.Length > 0)
                    {
                        var newMats = new Il2CppReferenceArray<Material>(targetMaterials.Length);
                        for (int m = 0; m < targetMaterials.Length; m++)
                        {
                            var bundleMat = targetMaterials[m];
                            if (bundleMat == null)
                            {
                                if (mr.sharedMaterials != null && m < mr.sharedMaterials.Length)
                                    newMats[m] = mr.sharedMaterials[m];
                                continue;
                            }

                            var cloned = UnityEngine.Object.Instantiate(bundleMat);
                            if (cloned != null)
                            {
                                cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                                newMats[m] = cloned;
                            }
                            else if (mr.sharedMaterials != null && m < mr.sharedMaterials.Length)
                            {
                                newMats[m] = mr.sharedMaterials[m];
                            }
                        }
                        mr.sharedMaterials = newMats;
                    }
                    else
                    {
                        // Use captured game materials (fallback)
                        if (CharmState.CapturedGameMaterials == null && mr != null
                            && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0)
                        {
                            CharmState.CapturedGameMaterials = mr.sharedMaterials;
                        }
                        if (CharmState.CapturedGameMaterials != null && mr != null)
                        {
                            mr.sharedMaterials = CharmState.CapturedGameMaterials;
                        }
                    }

                    // Apply mesh
                    mf.sharedMesh = targetMesh;
                    CharmState.CharmedMeshFilterIds.Add(instanceId);
                    Plugin.Log.LogInfo($"[Charm] ✓ Mesh replaced on '{mf.gameObject.name}'");

                    // Reset MagicaCloth physics
                    ResetMagicaCloth(mf);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Error in MF[{i}]: {ex.Message}");
                }
            }
        }

        private static void ResetMagicaCloth(MeshFilter mf)
        {
            try
            {
                var gameGO = mf.gameObject;
                var parentGO = gameGO.transform.parent?.gameObject;

                var allMB = new System.Collections.Generic.List<MonoBehaviour>();
                foreach (var mb in gameGO.GetComponents<MonoBehaviour>()) allMB.Add(mb);

                if (parentGO != null)
                {
                    foreach (var pMB in parentGO.GetComponents<MonoBehaviour>()) allMB.Add(pMB);
                }

                foreach (var mb in allMB)
                {
                    if (mb == null) continue;
                    string typeName = "";
                    try { typeName = mb.GetIl2CppType()?.Name ?? ""; } catch { }

                    if (typeName.Contains("MagicaCloth"))
                    {
                        Plugin.Log.LogDebug($"[CharmPhysics] MagicaCloth found on '{mb.gameObject.name}'");
                        var typeObj = mb.GetIl2CppType();
                        var resetMethod = typeObj.GetMethod("ResetCloth");
                        if (resetMethod != null)
                        {
                            var keepPoseBoxed = new Il2CppSystem.Boolean();
                            resetMethod.Invoke(mb, new Il2CppSystem.Object[] { keepPoseBoxed.BoxIl2CppObject() });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmPhysics] Error resetting MagicaCloth: {ex.Message}");
            }
        }
    }
}
