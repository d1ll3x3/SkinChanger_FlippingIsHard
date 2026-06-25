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
        // Key is carried explicitly (not derived from the filename) so the cache file can be
        // named by SteamID while the in-memory charm key stays the player's nick. Key == null
        // means the local player.
        private struct BundleJob { public string Path; public string Key; }
        private static Queue<BundleJob> _charmBundleQueue = new Queue<BundleJob>();
        private static string _currentCharmPlayer = null; // null = local player, "Nick" = friend
        private static bool _isLoadingBundle = false;
        private static UnityEngine.Networking.UnityWebRequest _bundleRequest;
        private static UnityEngine.AssetBundleRequest _extractionRequest;
        private static UnityEngine.AssetBundle _extractingBundle;

        /// <summary>True while any bundle is in flight (download or extraction).</summary>
        public static bool IsLoadingBundle => _isLoadingBundle;

        // --- Public API ---

        public static void LoadCharmMesh()
        {
            try
            {
                string localPath = Path.Combine(Plugin.PluginDirectory, "hatbundle");
                if (File.Exists(localPath))
                    _charmBundleQueue.Enqueue(new BundleJob { Path = localPath, Key = null });
                else
                    Plugin.Log.LogWarning($"[Charm] hatbundle not found at: {localPath}");

                ScanMultiplayerCharms();
                StartNextBundle();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Charm] Error in LoadCharmMesh: {ex}");
            }
        }

        /// <summary>
        /// Enqueues a bundle downloaded at runtime (remote skins/charms). The player key is
        /// passed explicitly (the cache file may be named by SteamID, not the nick). A key of
        /// "local" (the local player's own charm) is treated as the local bundle.
        /// Starts loading if the queue is idle.
        /// </summary>
        public static void EnqueueRemoteCharmBundle(string path, string nick)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string key = string.Equals(nick, "local", StringComparison.Ordinal) ? null : nick;
                _charmBundleQueue.Enqueue(new BundleJob { Path = path, Key = key });
                Plugin.Log.LogInfo($"[Charm] ✓ Queued remote bundle for: {nick}");
                if (!_isLoadingBundle) StartNextBundle();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Charm] EnqueueRemoteCharmBundle error: {ex}");
            }
        }

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

                    if (stored)
                        CharmReplacerBehavior.Instance?.TriggerImmediateReplacement();

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

        public static void TryApplyCharmToMeshFilters()
        {
            // Resolve local player once — this result is frame-cached in PlayerIdentity,
            // but we avoid even the cache-lookup cost by keeping a local reference.
            var localPlayer = PlayerIdentity.GetLocalPlayerGO();

            // Re-suppress original charm GOs that ChangeCharmClient may have re-activated.
            // The game's charm system calls SetActive(true) on its own slots when syncing
            // charm IDs by network, undoing our SetActive(false). We catch this here.
            foreach (var kv in CharmState.ReplacedOriginalCharmGOs)
            {
                var go = kv.Value;
                if (go == null) continue;
                if (go.activeSelf)
                {
                    go.SetActive(false);
                    Plugin.Log.LogDebug($"[Charm] Re-suppressed game charm re-enabled by network: '{go.name}'");
                }
                // Also keep the renderer hidden as a secondary guard
                var mr2 = go.GetComponent<MeshRenderer>();
                if (mr2 != null && mr2.enabled) mr2.enabled = false;
            }

            bool useLocalPrefab = CharmState.CustomPrefab != null && CharmState.CustomPrefabHasMagicaCloth;

            // The local charm must ONLY ever touch the local player. If the network owner
            // isn't resolved yet (localPlayer == null), skip — applying via a scene-wide
            // MeshFilter scan would put the local charm on EVERY player (the "charm leak")
            // and pre-empt remote players' own (physics-bearing) prefab charms. The charm
            // retry loop calls back until the owner resolves.
            bool wantLocalCharm = useLocalPrefab
                || (CharmState.CustomMesh == null && CharmState.CustomPrefab != null)
                || CharmState.CustomMesh != null;
            if (wantLocalCharm && localPlayer == null)
                Plugin.Log.LogDebug("[Charm] Local player not resolved yet; deferring local charm.");

            // === LOCAL PLAYER: full prefab when it carries its own MC2, mesh-swap fallback otherwise ===
            if (localPlayer != null && (useLocalPrefab || (CharmState.CustomMesh == null && CharmState.CustomPrefab != null)))
            {
                try
                {
                    Il2CppArrayBase<MeshFilter> filters = localPlayer.GetComponentsInChildren<MeshFilter>(true);

                    for (int i = 0; i < filters.Count; i++)
                    {
                        try
                        {
                            var mf = filters[i];
                            if (mf == null || mf.sharedMesh == null) continue;
                            if (!mf.gameObject.activeSelf) continue;
                            if (!IsCharmTarget(mf.gameObject)) continue;

                            ReplaceCharmWithPrefab(mf, CharmState.CustomPrefab, isLocal: true);
                            return;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[Charm] Error in prefab MF[{i}]: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Local prefab replacement error: {ex.Message}");
                }
            }
            else if (localPlayer != null && CharmState.CustomMesh != null)
            {
                try
                {
                    Il2CppArrayBase<MeshFilter> filters = localPlayer.GetComponentsInChildren<MeshFilter>(true);

                    for (int i = 0; i < filters.Count; i++)
                    {
                        try
                        {
                            var mf = filters[i];
                            if (mf == null || mf.sharedMesh == null) continue;

                            int instanceId = mf.GetInstanceID();
                            if (CharmState.CharmedMeshFilterIds.Contains(instanceId)) continue;

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
                                        PrepareRuntimeMaterial(cloned);
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
                            CharmState.CharmedMeshFilterIds.Add(instanceId);
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

            // === REMOTE PLAYERS ===
            if (CharmState.PlayerCharmPrefabs.Count > 0 || CharmState.PlayerCharmMeshes.Count > 0)
            {
                try
                {
                    var allPlayers = PlayerIdentity.GetAllPlayers();

                    foreach (var playerGO in allPlayers)
                    {
                        if (playerGO == null || playerGO == localPlayer) continue;

                        string nick = PlayerIdentity.GetPlayerNickname(playerGO);
                        if (string.IsNullOrEmpty(nick)) continue;

                        if (CharmState.PlayerCharmPrefabs.TryGetValue(nick, out var prefab) && PrefabHasMagicaCloth(prefab))
                        {
                            ApplyCharmPrefabToPlayer(playerGO, prefab);
                            continue;
                        }

                        if (CharmState.PlayerCharmMeshes.TryGetValue(nick, out var targetMesh))
                        {
                            CharmState.PlayerCharmMaterials.TryGetValue(nick, out var targetMaterials);
                            ApplyCharmToPlayer(playerGO, targetMesh, targetMaterials);
                            continue;
                        }

                        if (CharmState.PlayerCharmPrefabs.TryGetValue(nick, out prefab))
                            ApplyCharmPrefabToPlayer(playerGO, prefab);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Remote TryApplyCharmToMeshFilters error: {ex.Message}");
                }
            }
        }

        // --- Internal ---

        private static void ScanMultiplayerCharms()
        {
            string charmsDir = Path.Combine(Plugin.PluginDirectory, "Charms");
            if (!Directory.Exists(charmsDir)) return;

            foreach (var file in Directory.GetFiles(charmsDir, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                string lower = fileName.ToLowerInvariant();
                if (lower.EndsWith(".png") || lower.EndsWith(".txt") || lower.EndsWith(".meta") || lower.EndsWith(".hash"))
                    continue;

                // Remote-cached files (have a .hash sidecar) are named by SteamID and are
                // re-applied under the correct nick by RemoteSkinService when the player
                // appears. Loading them here would key them by the SteamID filename and never
                // match. Only hand-placed files (no sidecar, named by the player) are loaded.
                if (File.Exists(file + ".hash")) continue;

                string playerName = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(playerName)) continue;

                _charmBundleQueue.Enqueue(new BundleJob { Path = file, Key = playerName });
                Plugin.Log.LogDebug($"[Charm] ✓ Queued multiplayer bundle for: {playerName}");
            }
        }

        private static void StartNextBundle()
        {
            if (_charmBundleQueue.Count == 0)
            {
                Plugin.Log.LogInfo("[Charm] All bundles processed.");
                return;
            }

            var job = _charmBundleQueue.Dequeue();
            string path = job.Path;
            _currentCharmPlayer = job.Key; // null => local; nick => remote (explicit, not from filename)

            string label = _currentCharmPlayer ?? "local";
            Plugin.Log.LogInfo($"[Charm] Loading bundle for '{label}': {path}");

            string uri = "file:///" + path.Replace("\\", "/");
            _bundleRequest = UnityEngine.Networking.UnityWebRequestAssetBundle.GetAssetBundle(uri);
            _bundleRequest.SendWebRequest();
            _isLoadingBundle = true;
        }

        private static bool ExtractAndStoreAssets(Il2CppReferenceArray<UnityEngine.Object> allAssets, string playerName)
        {
            if (allAssets == null) return false;

            string label = playerName ?? "local";
            Mesh extractedMesh = null;
            Il2CppReferenceArray<Material> extractedMaterials = null;

            // First pass: pick the BEST GameObject to use as the charm prefab. A bundle can
            // contain several GameObjects (e.g. the charm mesh AND a separate object holding the
            // MagicaCloth), and LoadAllAssetsAsync returns them in a NON-deterministic order that
            // differs between the first async download and a later cached/sync load. Taking "the
            // last GameObject" therefore picked the cloth-bearing object only sometimes — which is
            // exactly why the charm got physics only after a restart. Score each candidate and
            // keep the one that carries the MagicaCloth (and a renderer), deterministically.
            GameObject bestGo = null;
            int bestScore = -1;
            Mesh standaloneMesh = null;
            Material standaloneMaterial = null;

            foreach (var obj in allAssets)
            {
                if (obj == null) continue;

                var go = obj.TryCast<GameObject>();
                if (go != null)
                {
                    bool hasCloth = go.GetComponentInChildren<MagicaCloth2.MagicaCloth>(true) != null;
                    bool hasRenderer = go.GetComponentInChildren<MeshRenderer>(true) != null
                                    || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
                    int score = (hasCloth ? 2 : 0) + (hasRenderer ? 1 : 0);
                    if (score > bestScore) { bestScore = score; bestGo = go; }
                    continue;
                }

                var meshObj = obj.TryCast<Mesh>();
                if (meshObj != null) { if (standaloneMesh == null) standaloneMesh = meshObj; continue; }

                var matObj = obj.TryCast<Material>();
                if (matObj != null && standaloneMaterial == null) standaloneMaterial = matObj;
            }

            // Diagnostic (only when NO MagicaCloth was found): dump every component type in every
            // GameObject of the bundle, by real type name. Reveals whether the bundle simply has
            // no cloth (just a mesh), a cloth under a different type (version mismatch), or a
            // missing/unresolved script (shows as <missing/null>). Gated so well-formed charms
            // (cloth present) don't spam the log.
            if (bestScore < 2)
            try
            {
                foreach (var obj in allAssets)
                {
                    var g = obj?.TryCast<GameObject>();
                    if (g == null) continue;
                    var comps = g.GetComponentsInChildren<Component>(true);
                    var names = new System.Collections.Generic.List<string>();
                    if (comps != null)
                        for (int i = 0; i < comps.Count; i++)
                        {
                            try { names.Add(comps[i] == null ? "<missing/null>" : (comps[i].GetIl2CppType()?.FullName ?? "?")); }
                            catch { names.Add("<err>"); }
                        }
                    Plugin.Log.LogInfo($"[CharmBundle][Diag] '{label}' GO='{g.name}' children={g.transform.childCount} comps=[{string.Join(" | ", names)}]");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[CharmBundle][Diag] dump error: {ex.Message}"); }

            if (bestGo != null)
            {
                bool sourceWasActive = bestGo.activeSelf;
                try { bestGo.SetActive(false); } catch { }

                var prefab = UnityEngine.Object.Instantiate(bestGo);
                try { bestGo.SetActive(sourceWasActive); } catch { }

                prefab.name = $"CustomCharm_{label}";
                prefab.hideFlags = HideFlags.DontUnloadUnusedAsset;
                prefab.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(prefab);

                if (playerName == null)
                {
                    CharmState.CustomPrefab = prefab;
                    CharmState.CustomPrefabHasMagicaCloth = PrefabHasMagicaCloth(prefab);
                }
                else
                    CharmState.PlayerCharmPrefabs[playerName] = prefab;

                Plugin.Log.LogInfo($"[CharmBundle] ✓ Prefab stored for '{label}': '{prefab.name}' ({prefab.GetInstanceID()}) children={prefab.transform.childCount} score={bestScore}");
                LogPrefabPhysicsSource(prefab, label);

                // Mesh + materials from the chosen GO (used by the mesh-swap fallback path).
                var mf = bestGo.GetComponent<MeshFilter>() ?? bestGo.GetComponentInChildren<MeshFilter>(true);
                if (mf?.sharedMesh != null)
                {
                    extractedMesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
                    extractedMesh.name = $"CustomGorraMesh_{label}";
                    extractedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;

                    var mr = bestGo.GetComponent<MeshRenderer>() ?? bestGo.GetComponentInChildren<MeshRenderer>(true);
                    if (mr != null && mr.sharedMaterials != null && mr.sharedMaterials.Length > 0)
                    {
                        var newMats = new Il2CppReferenceArray<Material>(mr.sharedMaterials.Length);
                        for (int i = 0; i < mr.sharedMaterials.Length; i++)
                        {
                            var cloned = UnityEngine.Object.Instantiate(mr.sharedMaterials[i]);
                            cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            PrepareRuntimeMaterial(cloned);
                            newMats[i] = cloned;
                        }
                        extractedMaterials = newMats;
                    }
                }
            }

            // Standalone Mesh / Material fallbacks (bundles that ship a bare mesh/material).
            if (extractedMesh == null && standaloneMesh != null)
            {
                extractedMesh = UnityEngine.Object.Instantiate(standaloneMesh);
                extractedMesh.name = $"CustomGorraMesh_{label}";
                extractedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            if (extractedMaterials == null && standaloneMaterial != null)
            {
                var clonedMat = UnityEngine.Object.Instantiate(standaloneMaterial);
                if (clonedMat != null)
                {
                    clonedMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    PrepareRuntimeMaterial(clonedMat);
                    extractedMaterials = new Il2CppReferenceArray<Material>(new Material[] { clonedMat });
                }
            }

            if (playerName == null)
            {
                if (extractedMesh != null) CharmState.CustomMesh = extractedMesh;
                if (extractedMaterials != null) CharmState.BundleMaterials = extractedMaterials;
            }
            else
            {
                if (extractedMesh != null)
                    CharmState.PlayerCharmMeshes[playerName] = extractedMesh;
                if (extractedMaterials != null)
                    CharmState.PlayerCharmMaterials[playerName] = extractedMaterials;
            }

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

        /// <summary>Returns true if this GameObject name matches the phone charm slot we replace.</summary>
        private static bool IsExactPhoneCharm(string name) =>
            !string.IsNullOrEmpty(name) && name.Contains("charm_phone");

        private static bool IsCharmTarget(GameObject go)
        {
            if (go == null) return false;

            try
            {
                string goName = (go.name ?? string.Empty).ToLowerInvariant();

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

        // --- Apply charm to a single remote player (mesh-swap path) ---

        // Rate-limit re-applying after the game reverts our swapped mesh (its charm network
        // re-sync), so we don't get into a tight fight loop with the game.
        private static readonly Dictionary<int, float> _lastReswap = new Dictionary<int, float>();
        private const float ReswapCooldown = 2f;
        // Log the "mesh-only = no reliable physics" hint once per charm root.
        private static readonly HashSet<int> _meshOnlyHintLogged = new HashSet<int>();

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

                    // Skip inactive slots — a prefab replacement may have disabled the
                    // original SM_Charm_Phone. Without this check the mesh-swap path
                    // would re-apply to the hidden slot, producing a duplicate charm.
                    if (!mf.gameObject.activeSelf) continue;

                    int instanceId = mf.GetInstanceID();
                    if (mf.sharedMesh == targetMesh) continue; // already our mesh — nothing to do

                    string meshName = "";
                    try { meshName = mf.sharedMesh.name ?? ""; } catch { }
                    if (meshName.StartsWith("CustomGorraMesh")) continue; // already a custom charm mesh

                    if (!IsCharmTarget(mf.gameObject)) continue;

                    // If we already swapped this slot but the mesh is no longer ours, the game
                    // re-applied its original charm (ChangeCharmClient network re-sync). Re-apply
                    // ours instead of leaving the original — but rate-limit so we never get into a
                    // tight fight loop with the game.
                    if (CharmState.CharmedMeshFilterIds.Contains(instanceId))
                    {
                        if (_lastReswap.TryGetValue(instanceId, out float t) && Time.time - t < ReswapCooldown)
                            continue;
                        _lastReswap[instanceId] = Time.time;
                        Plugin.Log.LogWarning($"[Charm] Game reverted charm on '{mf.gameObject.name}'; re-applying custom mesh.");
                    }

                    var mr = mf.GetComponent<MeshRenderer>();

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
                                PrepareRuntimeMaterial(cloned);
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
                        {
                            CharmState.CapturedGameMaterials = mr.sharedMaterials;
                        }
                        if (CharmState.CapturedGameMaterials != null && mr != null)
                        {
                            mr.sharedMaterials = CharmState.CapturedGameMaterials;
                        }
                    }

                    mf.sharedMesh = targetMesh;
                    CharmState.CharmedMeshFilterIds.Add(instanceId);
                    Plugin.Log.LogInfo($"[Charm] ✓ Mesh replaced on '{mf.gameObject.name}'");

                    ResetMagicaCloth(mf);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Error in MF[{i}]: {ex.Message}");
                }
            }
        }

        // --- Prefab-based charm replacement ---

        /// <summary>
        /// Reemplaza el SM_Charm_Phone original con el prefab del bundle.
        /// Solo se usa como fallback para prefabs compuestos. Para bundles simples
        /// con MeshFilter/MeshRenderer, el camino preferido es sustituir mesh/material.
        /// </summary>
        private static void ReplaceCharmWithPrefab(MeshFilter originalMF, GameObject prefab, bool isLocal = false)
        {
            if (prefab == null) return;

            int originalId = originalMF.GetInstanceID();
            if (CharmState.CharmedMeshFilterIds.Contains(originalId)) return;

            var originalGO = originalMF.gameObject;
            var parent     = originalGO.transform.parent;
            var newCharm = UnityEngine.Object.Instantiate(prefab);
            newCharm.transform.SetParent(parent, false);
            newCharm.transform.localPosition = originalGO.transform.localPosition;
            
            // Apply original rotation, but add an offset of -90 on the X axis to point it towards the sky
            newCharm.transform.localRotation = originalGO.transform.localRotation * UnityEngine.Quaternion.Euler(-90f, 0f, 0f);
            
            // Preserve the user's prefab localScale, do not overwrite it with the original's.

            SetLayerRecursively(newCharm, originalGO.layer);

            var cloths = newCharm.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
            if (cloths == null || cloths.Count == 0)
                Plugin.Log.LogWarning("[CharmPhysics] Prefab replacement has no MagicaCloth; leaving physics untouched.");
            else
                Plugin.Log.LogInfo($"[CharmPhysics] Prefab replacement using {cloths.Count} bundled MagicaCloth component(s).");

            // Solo upgrade Built-in→URP. NO copiar el shader del originalGO (es SG_Player_MSAO,
            // shader del cuerpo del jugador, y rompe los materiales del charm).
            FixMaterialShaders(newCharm);

            newCharm.SetActive(true);

            // Diagnóstico post-activación — útil si algo se ve raro (nivel Debug para no ensuciar logs)
            try
            {
                Plugin.Log.LogDebug($"[Charm] active={newCharm.activeInHierarchy} lossyScale={newCharm.transform.lossyScale} children={newCharm.transform.childCount}");
                var anyR = newCharm.GetComponentInChildren<Renderer>();
                if (anyR != null)
                    Plugin.Log.LogDebug($"[Charm] bounds center={anyR.bounds.center} size={anyR.bounds.size}");
                else
                    Plugin.Log.LogWarning("[Charm] No Renderer encontrado en el prefab instanciado");
            }
            catch { }

            ForceRenderersEnabled(newCharm);

            RebuildBundledMagicaCloth(newCharm);

            // Disable the original charm slot. Also disable its renderer independently
            // as a second layer — even if the game calls SetActive(true) on it again
            // (e.g. via ChangeCharmClient network sync), the renderer stays hidden until
            // the next scan runs the re-suppress loop at the top of TryApplyCharmToMeshFilters.
            var originalMR = originalGO.GetComponent<MeshRenderer>();
            if (originalMR != null) originalMR.enabled = false;
            originalGO.SetActive(false);

            CharmState.CharmedMeshFilterIds.Add(originalId);
            CharmState.ReplacedOriginalCharmGOs[originalId] = originalGO; // track for re-suppress
            if (isLocal) CharmState.LocalCharmApplied = true;  // only block retries for local player

            Plugin.Log.LogInfo($"[Charm] ✓ Prefab replacing '{originalGO.name}' (layer={originalGO.layer})");
        }

        private static bool PrefabHasMagicaCloth(GameObject prefab)
        {
            if (prefab == null) return false;

            try
            {
                var cloth = prefab.GetComponent<MagicaCloth2.MagicaCloth>()
                    ?? prefab.GetComponentInChildren<MagicaCloth2.MagicaCloth>(true);
                return cloth != null;
            }
            catch
            {
                return false;
            }
        }

        private static void RebuildBundledMagicaCloth(GameObject charm)
        {
            if (charm == null) return;

            try
            {
                var cloths = charm.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
                if (cloths == null || cloths.Count == 0) return;

                // Do NOT call BuildAndRun() here. The user configured the cloth in the Unity Editor,
                // which generates pre-build data; the component binds natively on its own Start when
                // the prefab is instantiated and active. We just ensure the components are enabled.
                // (Restart proves native binding works once the prefab path actually runs — the
                // real first-load failure was the charm-leak pre-empting this path, fixed elsewhere.)
                for (int i = 0; i < cloths.Count; i++)
                {
                    var cloth = cloths[i];
                    if (cloth != null && !cloth.enabled)
                    {
                        cloth.enabled = true;
                        Plugin.Log.LogInfo($"[CharmPhysics] Enabled pre-built MagicaCloth on '{cloth.gameObject.name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmPhysics] MagicaCloth enable error: {ex.Message}");
            }
        }

        private static void LogPrefabPhysicsSource(GameObject prefab, string label)
        {
            try
            {
                var renderers = prefab.GetComponentsInChildren<Renderer>(true);
                var meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
                var cloths = prefab.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);

                Plugin.Log.LogDebug($"[CharmBundle] Prefab '{label}' contents: children={prefab.transform.childCount}, renderers={renderers?.Count ?? 0}, meshFilters={meshFilters?.Count ?? 0}, magicaCloth={cloths?.Count ?? 0}");

                for (int i = 0; i < prefab.transform.childCount; i++)
                {
                    var child = prefab.transform.GetChild(i);
                    Plugin.Log.LogDebug($"[CharmBundle]   child[{i}]='{child.name}' children={child.childCount}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmBundle] Prefab diagnostics failed for '{label}': {ex.Message}");
            }
        }

        /// <summary>
        /// Solo upgrade de shaders Built-in (Standard, Legacy/...) → URP/Lit.
        /// Los shaders URP/SRP del bundle se respetan tal cual.
        /// Detecta shaders rotos (Hidden/InternalErrorShader) por si algún shader no está en el juego.
        /// </summary>
        private static void FixMaterialShaders(GameObject newCharm)
        {
            try
            {
                var newMRs = newCharm.GetComponentsInChildren<MeshRenderer>(true);
                foreach (var mr in newMRs)
                {
                    if (mr == null) continue;
                    foreach (var mat in mr.sharedMaterials)
                    {
                        if (mat == null) continue;
                        string shaderName = mat.shader?.name ?? "";

                        if (mat.shader == null || shaderName == "Hidden/InternalErrorShader")
                        {
                            Plugin.Log.LogError($"[CharmShader] Mat '{mat.name}' tiene shader ROTO ('{shaderName}'). El shader original del bundle no existe en el juego. Reexporta usando un shader que el juego tenga cargado (ej. URP/Lit).");
                            var fallback = Shader.Find("Universal Render Pipeline/Lit");
                            if (fallback != null) mat.shader = fallback;
                            continue;
                        }

                        bool isBuiltIn = shaderName == "Standard"
                                      || shaderName == "Standard (Specular setup)"
                                      || shaderName == "Diffuse"
                                      || shaderName == "Bumped Diffuse"
                                      || shaderName.StartsWith("Legacy Shaders/");

                        if (isBuiltIn)
                        {
                            var urpLit = Shader.Find("Universal Render Pipeline/Lit");
                            if (urpLit != null)
                            {
                                Plugin.Log.LogInfo($"[CharmShader] Built-in→URP: '{shaderName}' → 'Universal Render Pipeline/Lit'");
                                mat.shader = urpLit;
                            }
                            else
                            {
                                Plugin.Log.LogWarning("[CharmShader] URP/Lit no encontrado en este juego");
                            }
                        }
                        else
                        {
                            Plugin.Log.LogInfo($"[CharmShader] '{shaderName}' ya es SRP/custom — sin cambios");
                        }

                        PrepareRuntimeMaterial(mat);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmShader] Error: {ex.Message}");
            }
        }

        private static void PrepareRuntimeMaterial(Material mat)
        {
            if (mat == null) return;

            try
            {
                if (mat.HasProperty("_BaseColor"))
                {
                    var color = mat.GetColor("_BaseColor");
                    if (color.a < 0.99f)
                    {
                        color.a = 1f;
                        mat.SetColor("_BaseColor", color);
                    }
                }

                if (mat.HasProperty("_Color"))
                {
                    var color = mat.GetColor("_Color");
                    if (color.a < 0.99f)
                    {
                        color.a = 1f;
                        mat.SetColor("_Color", color);
                    }
                }

                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);

                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = -1;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmMaterial] Runtime material prep failed for '{mat.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Garantiza enabled=true en todos los Renderer del GO.
        /// Protección ante MC2 ocultando renderers como efecto secundario de un build fallido.
        /// </summary>
        private static void ForceRenderersEnabled(GameObject go)
        {
            try
            {
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (!r.enabled)
                    {
                        r.enabled = true;
                        Plugin.Log.LogInfo($"[CharmRenderer] Re-enabled renderer en '{r.gameObject.name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmRenderer] Error: {ex.Message}");
            }
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }

        private static void ApplyCharmPrefabToPlayer(GameObject playerGO, GameObject prefab)
        {
            // Only scan ACTIVE MeshFilters: after placement the original SM_Charm_Phone is
            // disabled. Including inactive (true) would find it again on the next scan and
            // attempt a second placement — causing the double-charm bug in multiplayer.
            var filters = playerGO.GetComponentsInChildren<MeshFilter>();
            if (filters == null) return;

            for (int i = 0; i < filters.Count; i++)
            {
                try
                {
                    var mf = filters[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!IsCharmTarget(mf.gameObject)) continue;

                    ReplaceCharmWithPrefab(mf, prefab);
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Charm] Error in prefab MF[{i}]: {ex.Message}");
                }
            }
        }

        private static void ResetMagicaCloth(MeshFilter mf)
        {
            try
            {
                var root = mf.transform.root.gameObject;
                var allMBs = root.GetComponentsInChildren<MonoBehaviour>(true);

                foreach (var mb in allMBs)
                {
                    if (mb == null) continue;
                    string typeName = "";
                    try { typeName = mb.GetIl2CppType()?.FullName ?? ""; } catch { continue; }

                    if (!typeName.Contains("MagicaCloth2") && !typeName.Contains("MagicaCloth"))
                        continue;

                    // This is the game's OWN MagicaCloth on the original charm slot. A mesh-only
                    // charm reuses it: we swap the mesh and BuildAndRun the game's cloth so it
                    // simulates the NEW mesh. This only succeeds when the cloth is still FRESH
                    // (unbuilt) — e.g. right after a graceful restart. If the phone has been spawned
                    // a while (the usual case, and always for late remote downloads) MagicaCloth2
                    // refuses with "[MC2] Already built" and the charm hangs static. Neither
                    // disabling the component nor toggling the GameObject makes MC2 release that
                    // built state (confirmed in-game: both still log "Already built", and toggling
                    // makes the game re-clone the charm in a loop). So we do NOT fight it: a single
                    // BuildAndRun gives physics in the fresh case and is a harmless no-op otherwise.
                    // Reliable first-load physics require the charm to ship its OWN MagicaCloth2
                    // (the prefab path) — see the hint below.
                    try
                    {
                        var mc2 = mb.TryCast<MagicaCloth2.MagicaCloth>();
                        if (mc2 != null) mc2.BuildAndRun();
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[CharmPhysics] BuildAndRun: {ex.Message}"); }

                    if (_meshOnlyHintLogged.Add(root.GetInstanceID()))
                        Plugin.Log.LogWarning(
                            "[CharmPhysics] Mesh-only charm: physics only apply when the game's cloth " +
                            "is fresh (after a restart). For physics on the FIRST load, export the charm " +
                            "WITH a configured MagicaCloth2 component so it loads via the prefab path.");

                    return;
                }

                Plugin.Log.LogWarning($"[CharmPhysics] No MagicaCloth/MagicaCloth2 found in hierarchy of '{root.name}'.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[CharmPhysics] Error resetting MagicaCloth: {ex.Message}");
            }
        }
    }
}
