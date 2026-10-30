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

        /// <summary>The local charm is placed as a prefab (else its mesh is swapped into the game's charm).</summary>
        public static bool LocalUsesPrefab =>
            CharmState.CustomPrefab != null && (CharmState.CustomPrefabHasMagicaCloth || CharmState.CustomMesh == null);

        // Prefab charms point up from the phone charm slot instead of along it.
        internal static readonly Quaternion PrefabRotationOffset = Quaternion.Euler(-90f, 0f, 0f);

        private const string CustomCharmNamePrefix = "CustomCharm_";

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
            // Switched off from the game's customization menu: the phone charm stays as is.
            bool localCharmOn = SkinOverride.IsLocalEnabled(EHS.Skins.SkinCategory.Charm);
            bool wantLocalCharm = localCharmOn
                && (useLocalPrefab
                    || (CharmState.CustomMesh == null && CharmState.CustomPrefab != null)
                    || CharmState.CustomMesh != null);
            if (wantLocalCharm && localPlayer == null)
                Plugin.Log.LogDebug("[Charm] Local player not resolved yet; deferring local charm.");

            // === LOCAL PLAYER: full prefab when it carries its own MC2, mesh-swap fallback otherwise ===
            if (localCharmOn && localPlayer != null && (useLocalPrefab || (CharmState.CustomMesh == null && CharmState.CustomPrefab != null)))
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
            else if (localCharmOn && localPlayer != null && CharmState.CustomMesh != null)
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
                            // The replaced original is switched off; CharmedMeshFilterIds is cleared on
                            // every skin apply, so this is what stops it from being cloned again.
                            if (!mf.gameObject.activeInHierarchy) continue;

                            int instanceId = mf.GetInstanceID();
                            if (CharmState.CharmedMeshFilterIds.Contains(instanceId)) continue;

                            string meshName = "";
                            try { meshName = mf.sharedMesh.name ?? ""; } catch { }

                            if (mf.sharedMesh == CharmState.CustomMesh) continue;
                            if (meshName.StartsWith("CustomGorraMesh")) continue;
                            if (!IsCharmTarget(mf.gameObject)) continue;

                            if (!SwapCharmMesh(mf, CharmState.CustomMesh, CharmState.BundleMaterials)) continue;
                            Plugin.Log.LogInfo($"[Charm] Mesh replaced on '{mf.gameObject.name}'");
                            CharmState.CharmedMeshFilterIds.Add(instanceId);
                            CharmState.LocalCharmApplied = true;
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
                        if (!SkinOverride.AllowsRemoteCustom(playerGO, EHS.Skins.SkinCategory.Charm)) continue;

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
                    // Each sharedMaterials read copies the array; read it once.
                    var sourceMats = mr != null ? mr.sharedMaterials : null;
                    if (sourceMats != null && sourceMats.Length > 0)
                    {
                        var newMats = new Il2CppReferenceArray<Material>(sourceMats.Length);
                        for (int i = 0; i < sourceMats.Length; i++)
                        {
                            var cloned = UnityEngine.Object.Instantiate(sourceMats[i]);
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
            !string.IsNullOrEmpty(name) && name.IndexOf("charm_phone", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsCharmTarget(GameObject go)
        {
            if (go == null) return false;

            try
            {
                // Name match first: every MeshFilter of every player is tested on each scan, and only
                // the few near the phone charm need the full ancestor walk below.
                bool nearCharm = IsExactPhoneCharm(go.name);
                Transform parent = go.transform.parent;
                for (int d = 0; d < 3 && parent != null && !nearCharm; d++)
                {
                    nearCharm = IsExactPhoneCharm(parent.gameObject.name);
                    parent = parent.parent;
                }
                if (!nearCharm) return false;

                // A placed custom charm sits next to the original under the phone charm instance, so
                // its own meshes match the check above and would get replaced again.
                for (var t = go.transform; t != null; t = t.parent)
                {
                    if ((t.name ?? string.Empty).StartsWith(CustomCharmNamePrefix, StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Charm] IsCharmTarget error: {ex.Message}");
                return false;
            }
        }

        // --- Mesh-only charms ---

        /// <summary>
        /// Shows <paramref name="mesh"/> in place of the charm mesh of <paramref name="mf"/>. Where the
        /// game's charm has a cloth, the original is hidden and the custom mesh swings rigidly from the
        /// same attachment point (CharmSwing); otherwise the mesh is swapped in place. False when nothing
        /// was changed.
        /// </summary>
        private static bool SwapCharmMesh(MeshFilter mf, Mesh mesh, Il2CppReferenceArray<Material> materials)
        {
            var cloth = mf.GetComponentInParent<MagicaCloth2.MagicaCloth>(true);
            if (cloth == null)
            {
                ApplyCharmMaterials(mf.GetComponent<MeshRenderer>(), materials);
                mf.sharedMesh = mesh;
                return true;
            }

            var parent = mf.transform.parent;
            if (parent != null && FindCustomCharmBelow(parent) != null) return false;

            // The custom charm is a charm of its own: the game's charm (mesh and strap) only provides
            // the attachment point, so all of it is hidden.
            var gameRenderers = cloth.GetComponentsInChildren<Renderer>(true);
            if (gameRenderers != null)
                for (int i = 0; i < gameRenderers.Count; i++)
                    if (gameRenderers[i] != null) gameRenderers[i].enabled = false;

            CharmSwing.Attach(mf, mesh, materials, ClothAnchor(cloth, mf, mesh), CustomCharmNamePrefix + "swing");

            // Hide the original the same way the prefab path does (re-suppressed if the game re-enables it).
            var originalGO = mf.gameObject;
            originalGO.SetActive(false);
            CharmState.ReplacedOriginalCharmGOs[mf.GetInstanceID()] = originalGO;
            Plugin.Log.LogInfo($"[CharmPhysics] ✓ Swinging custom mesh on '{mf.gameObject.name}'.");
            return true;
        }

        // Where the charm hangs from: the fixed (attachment) points painted in the game's cloth selection,
        // which lives in the cloth's local space. Without them, the top of the mesh.
        private static Vector3 ClothAnchor(MagicaCloth2.MagicaCloth cloth, MeshFilter mf, Mesh mesh)
        {
            var selection = cloth.serializeData2?.selectionData;
            if (selection != null && selection.positions != null && selection.attributes != null)
            {
                var sum = Vector3.zero;
                int fixedCount = 0;
                int n = Math.Min(selection.positions.Length, selection.attributes.Length);
                for (int i = 0; i < n; i++)
                {
                    if (!selection.attributes[i].IsFixed()) continue;
                    var p = selection.positions[i];
                    sum += new Vector3(p.x, p.y, p.z);
                    fixedCount++;
                }
                if (fixedCount > 0) return cloth.transform.TransformPoint(sum / fixedCount);
            }

            var bounds = mesh.bounds;
            return mf.transform.TransformPoint(new Vector3(bounds.center.x, bounds.max.y, bounds.center.z));
        }

        internal static void ApplyCharmMaterials(MeshRenderer mr, Il2CppReferenceArray<Material> targetMaterials)
        {
            if (mr == null) return;
            var currentMats = mr.sharedMaterials;
            if (targetMaterials != null && targetMaterials.Length > 0)
            {
                var newMats = new Il2CppReferenceArray<Material>(targetMaterials.Length);
                for (int m = 0; m < targetMaterials.Length; m++)
                {
                    var bundleMat = targetMaterials[m];
                    var cloned = bundleMat != null ? UnityEngine.Object.Instantiate(bundleMat) : null;
                    if (cloned != null)
                    {
                        cloned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        PrepareRuntimeMaterial(cloned);
                        newMats[m] = cloned;
                    }
                    else if (currentMats != null && m < currentMats.Length)
                    {
                        newMats[m] = currentMats[m];
                    }
                }
                mr.sharedMaterials = newMats;
                return;
            }

            if (CharmState.CapturedGameMaterials == null && currentMats != null && currentMats.Length > 0)
                CharmState.CapturedGameMaterials = currentMats;
            if (CharmState.CapturedGameMaterials != null)
                mr.sharedMaterials = CharmState.CapturedGameMaterials;
        }

        // --- Apply charm to a single remote player (mesh-swap path) ---

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

                    // Skip inactive slots — a prefab replacement or a cloth clone has disabled the
                    // original SM_Charm_Phone. Without this check it would be replaced again,
                    // producing a duplicate charm.
                    if (!mf.gameObject.activeInHierarchy) continue;

                    int instanceId = mf.GetInstanceID();
                    if (mf.sharedMesh == targetMesh) continue; // already our mesh — nothing to do

                    string meshName = "";
                    try { meshName = mf.sharedMesh.name ?? ""; } catch { }
                    if (meshName.StartsWith("CustomGorraMesh")) continue; // already a custom charm mesh

                    if (!IsCharmTarget(mf.gameObject)) continue;

                    if (!SwapCharmMesh(mf, targetMesh, targetMaterials)) continue;
                    CharmState.CharmedMeshFilterIds.Add(instanceId);
                    Plugin.Log.LogInfo($"[Charm] ✓ Mesh replaced on '{mf.gameObject.name}'");
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
            newCharm.transform.localRotation = originalGO.transform.localRotation * PrefabRotationOffset;

            // The phone charm's axes changed in the Steam release (that offset now points the charm at
            // the back of the phone), so finish by aligning the charm with the phone's own up axis.
            // Charm prefabs point along their -Y, so that is the axis that goes up.
            var phoneUp = PhoneUp(originalGO.transform);
            if (phoneUp.sqrMagnitude > 0f)
                newCharm.transform.rotation = Quaternion.FromToRotation(-newCharm.transform.up, phoneUp) * newCharm.transform.rotation;

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

        /// <summary>The phone charm mesh (not a placed custom charm) under <paramref name="root"/>.</summary>
        internal static MeshFilter FindOriginalCharmMesh(Transform root)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            if (filters == null) return null;
            for (int i = 0; i < filters.Count; i++)
                if (filters[i] != null && IsCharmTarget(filters[i].gameObject)) return filters[i];
            return null;
        }

        // Up axis of the phone body the charm hangs from: the preview / main-menu visual root, else the
        // player's rigidbody. Taken from the body rather than the world so a phone mid-flip still works.
        internal static Vector3 PhoneUp(Transform charm)
        {
            var dummy = charm.GetComponentInParent<EHS.PlayerVisualsDummy>(true);
            if (dummy != null && dummy.visualsRoot != null) return dummy.visualsRoot.up;
            var menu = charm.GetComponentInParent<EHS.Skins.EquippedPlayerSkins>(true);
            if (menu != null && menu.visualsRoot != null) return menu.visualsRoot.up;
            var body = charm.GetComponentInParent<EHS.Skins.PlayerSkins>(true)?.playerRef?.rb;
            return body != null ? body.transform.up : charm.root.up;
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
        internal static void FixMaterialShaders(GameObject newCharm)
        {
            try
            {
                Shader urpLit = null;
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
                            urpLit ??= Shader.Find("Universal Render Pipeline/Lit");
                            if (urpLit != null) mat.shader = urpLit;
                            continue;
                        }

                        bool isBuiltIn = shaderName == "Standard"
                                      || shaderName == "Standard (Specular setup)"
                                      || shaderName == "Diffuse"
                                      || shaderName == "Bumped Diffuse"
                                      || shaderName.StartsWith("Legacy Shaders/");

                        if (isBuiltIn)
                        {
                            urpLit ??= Shader.Find("Universal Render Pipeline/Lit");
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

        internal static void SetLayerRecursively(GameObject go, int layer)
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

        /// <summary>
        /// Puts the local custom charm on an arbitrary visual (the customization preview), using the
        /// same prefab/mesh choice as the local player.
        /// </summary>
        public static void ApplyLocalCharmTo(GameObject root)
        {
            if (root == null) return;
            try
            {
                if (LocalUsesPrefab)
                    ApplyCharmPrefabToPlayer(root, CharmState.CustomPrefab);
                else if (CharmState.CustomMesh != null)
                    ApplyCharmToPlayer(root, CharmState.CustomMesh, CharmState.BundleMaterials);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Charm] Preview charm error: {ex.Message}");
            }
        }

        /// <summary>The placed custom charm prefab under <paramref name="root"/>, if any.</summary>
        public static Transform FindCustomCharm(Transform root)
        {
            if (root == null) return null;
            if ((root.name ?? string.Empty).StartsWith(CustomCharmNamePrefix, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindCustomCharm(root.GetChild(i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Destroys placed custom charm prefabs under <paramref name="root"/>. ReplaceCharmWithPrefab
        /// parents them next to the original charm, which may sit outside the skin instance that
        /// SkinPrefabHandler.Clear destroys.
        /// </summary>
        public static void RemoveCustomCharms(Transform root)
        {
            if (root == null) return;
            // Detach before Destroy (deferred to end of frame) so the next search skips it.
            Transform charm;
            while ((charm = FindCustomCharmBelow(root)) != null)
            {
                charm.SetParent(null, false);
                UnityEngine.Object.Destroy(charm.gameObject);
            }
        }

        private static Transform FindCustomCharmBelow(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindCustomCharm(root.GetChild(i));
                if (found != null) return found;
            }
            return null;
        }
    }
}
