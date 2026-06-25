using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CharmReplacer
{
    internal class CharmReplacerBehavior : MonoBehaviour
    {
        public static CharmReplacerBehavior Instance { get; private set; }

        private float _scanInterval = 1.0f;
        private float _nextScanDelay = 0f;
        private int _pendingScans = 0;
        private int _charmRetries = 0;
        private float _charmRetryTimer = 0f;

        // Per-frame poll after scene transitions / paint-bucket events. Catches the
        // target renderer within ~16ms of it spawning instead of waiting for the
        // scheduled scan. Auto-stops on first paint or when stable-empty for a
        // few frames (cosmetic change with nothing to paint).
        private int _menuFastPollFrames = 0;
        private int _gameFastPollFrames = 0;
        private int _gameFastPollStableEmpty = 0;
        private const int FastPollBudgetFrames = 180;
        private const int StableEmptyFramesToStop = 5;

        // MagicaCloth2 delayed re-enable queue — two parallel lists so multiple cloths
        // (e.g. local + remote player in multiplayer) can all be tracked independently.
        // Previously a single ref meant the second ScheduleClothReenable() call
        // overwrote the first, leaving the first cloth stuck disabled forever.
        private readonly List<MonoBehaviour> _pendingClothList  = new List<MonoBehaviour>();
        private readonly List<int>           _pendingClothDelays = new List<int>();

        // Delayed re-enable + BuildAndRun for the game's MagicaCloth after a mesh-only charm
        // swaps the phone-charm mesh. The cloth is disabled at swap time; a few frames later we
        // re-enable and rebuild so it re-initialises against the NEW mesh — giving physics on
        // the first load (not only after a graceful restart).
        private readonly List<MonoBehaviour> _pendingClothRebuild  = new List<MonoBehaviour>();
        private readonly List<int>           _pendingClothRebuildDelays = new List<int>();

        // Renderer re-enable after BuildAndRun (hide during async rebuild)
        private Renderer _pendingRendererReenable = null;
        private int _rendererReenableDelay = 0;

        public void Awake()
        {
            Instance = this;

            // Each subsystem is isolated so one failure (e.g. a missing dependency DLL while
            // decoding a PNG) can't cascade and disable the others — notably it must not stop
            // remote skins from starting.
            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += new Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>(OnSceneLoaded);
            }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmReplacerBehavior] sceneLoaded hook error: {ex}"); }

            // Create the drop-in folders so users can place files by hand without making them.
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Plugin.PluginDirectory, "Skins"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Plugin.PluginDirectory, "Charms"));
            }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmReplacerBehavior] Folder creation error: {ex}"); }

            try { CharmSystem.LoadCharmMesh(); }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmReplacerBehavior] LoadCharmMesh error: {ex}"); }

            try { SkinLoader.LoadCustomSkin(); }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmReplacerBehavior] LoadCustomSkin error: {ex}"); }

            // Remote skins: runs AFTER the local loaders so hand-placed files take precedence
            // (they are not re-downloaded).
            try { RemoteSkinService.BeginManifestFetch(); }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmReplacerBehavior] BeginManifestFetch error: {ex}"); }
        }

        public void OnDestroy()
        {
            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -=
                    new Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>(OnSceneLoaded);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CharmReplacerBehavior] OnDestroy error: {ex}");
            }
        }

        public void Update()
        {
            try
            {
                // Only tick bundle loading state machine when a load is in flight
                if (CharmSystem.IsLoadingBundle)
                    CharmSystem.UpdateBundleLoading();

                // Remote skins/charms: manifest + download queue.
                RemoteSkinService.Tick();

                // MagicaCloth2 delayed re-enable queue (multiplayer-safe)
                for (int ci = _pendingClothList.Count - 1; ci >= 0; ci--)
                {
                    if (_pendingClothDelays[ci] > 0)
                    {
                        _pendingClothDelays[ci]--;
                    }
                    else
                    {
                        var cloth = _pendingClothList[ci];
                        if (cloth != null)
                        {
                            Plugin.Log.LogInfo($"[CharmPhysics] Re-enabling cloth on '{cloth.gameObject.name}'");
                            cloth.enabled = true;
                        }
                        _pendingClothList.RemoveAt(ci);
                        _pendingClothDelays.RemoveAt(ci);
                    }
                }

                if (_pendingRendererReenable != null)
                {
                    if (_rendererReenableDelay > 0)
                        _rendererReenableDelay--;
                    else
                    {
                        _pendingRendererReenable.enabled = true;
                        Plugin.Log.LogDebug($"[CharmPhysics] Renderer re-enabled on '{_pendingRendererReenable.gameObject.name}'");
                        _pendingRendererReenable = null;
                    }
                }

                // Game-cloth rebuild after a mesh-only charm swap (re-enable + BuildAndRun)
                for (int ri = _pendingClothRebuild.Count - 1; ri >= 0; ri--)
                {
                    if (_pendingClothRebuildDelays[ri] > 0)
                    {
                        _pendingClothRebuildDelays[ri]--;
                        continue;
                    }
                    var cloth = _pendingClothRebuild[ri];
                    _pendingClothRebuild.RemoveAt(ri);
                    _pendingClothRebuildDelays.RemoveAt(ri);
                    if (cloth == null) continue;
                    try
                    {
                        // Re-activate the whole GameObject (it was toggled off at swap time) so
                        // MagicaCloth2 re-initialises from scratch against the NEW mesh, then build.
                        cloth.gameObject.SetActive(true);
                        cloth.enabled = true;
                        var mc2 = cloth.TryCast<MagicaCloth2.MagicaCloth>();
                        if (mc2 != null)
                        {
                            mc2.BuildAndRun();
                            Plugin.Log.LogInfo($"[CharmPhysics] ✓ Re-enabled charm GO + BuildAndRun on '{cloth.gameObject.name}' (success = no '[MC2] Already built' above).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[CharmPhysics] Cloth rebuild error: {ex.Message}");
                    }
                }


                if (CharmState.CustomMesh == null && CharmState.CustomPrefab == null
                    && CharmState.PlayerCharmMeshes.Count == 0 && CharmState.PlayerCharmPrefabs.Count == 0
                    && CharmState.CustomSkinTexture == null && CharmState.PlayerBaseTextures.Count == 0) return;

                if (_menuFastPollFrames > 0)
                {
                    if (CharmState.CustomSkinTexture != null && CharmState.MenuPreviewRootIds.Count == 0)
                    {
                        SkinSystem.TryApplySkinToMainMenuPhonePreview();
                        if (CharmState.MenuPreviewRootIds.Count > 0) _menuFastPollFrames = 0;
                    }
                    else
                    {
                        _menuFastPollFrames = 0;
                    }
                    if (_menuFastPollFrames > 0) _menuFastPollFrames--;
                }

                if (_gameFastPollFrames > 0)
                {
                    bool hasSkinSource = CharmState.CustomSkinTexture != null || CharmState.PlayerBaseTextures.Count > 0;
                    if (hasSkinSource && CharmState.SkinnedRendererIds.Count == 0)
                    {
                        SkinSystem.TryApplySkinToPhoneSourceMaterials();
                        if (CharmState.SkinnedRendererIds.Count > 0)
                        {
                            _gameFastPollFrames = 0;
                            _gameFastPollStableEmpty = 0;
                        }
                        else
                        {
                            // Nothing painted this frame. If we stay empty for a few
                            // consecutive frames, user is on a non-default cosmetic —
                            // stop polling so we don't burn CPU for the full budget.
                            _gameFastPollStableEmpty++;
                            if (_gameFastPollStableEmpty >= StableEmptyFramesToStop)
                            {
                                _gameFastPollFrames = 0;
                                _gameFastPollStableEmpty = 0;
                            }
                        }
                    }
                    else
                    {
                        _gameFastPollFrames = 0;
                        _gameFastPollStableEmpty = 0;
                    }
                    if (_gameFastPollFrames > 0) _gameFastPollFrames--;
                }

                if (_pendingScans > 0)
                {
                    _nextScanDelay -= Time.deltaTime;
                    if (_nextScanDelay <= 0)
                    {
                        DoScanAndReplace();
                        _pendingScans--;
                        _nextScanDelay = _scanInterval;
                    }
                }

                // Retry charm application periodically (handles FishNet overwrite / late spawn).
                // Check both CustomMesh and CustomPrefab — bundles with only a prefab (e.g. MagicaCloth)
                // never set CustomMesh, so the old check would silently skip retries.
                bool hasLocalCharm = CharmState.CustomMesh != null || CharmState.CustomPrefab != null;
                if (hasLocalCharm && !CharmState.LocalCharmApplied && _charmRetries > 0)
                {
                    _charmRetryTimer += Time.deltaTime;
                    if (_charmRetryTimer >= 2f)
                    {
                        _charmRetryTimer = 0f;
                        _charmRetries--;
                        Plugin.Log.LogDebug($"[Behavior] Charm retry #{5 - _charmRetries}/5");
                        DoScanAndReplace();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CharmReplacerBehavior] Update error: {ex}");
            }
        }

        public void DoScanAndReplace()
        {
            if (CharmState.CustomMesh == null && CharmState.CustomPrefab == null
                && CharmState.PlayerCharmMeshes.Count == 0 && CharmState.PlayerCharmPrefabs.Count == 0
                && CharmState.CustomSkinTexture == null && CharmState.PlayerBaseTextures.Count == 0) return;

            try
            {
                if (CharmState.CustomSkinTexture != null || CharmState.PlayerBaseTextures.Count > 0)
                {
                    try
                    {
                        int sourceMaterialsReplaced = SkinSystem.TryApplySkinToPhoneSourceMaterials();
                        if (sourceMaterialsReplaced > 0)
                        {
                            Plugin.Log.LogInfo($"[Skin] ✓ {sourceMaterialsReplaced} phone source material(s) updated.");
                        }

                        int menuPreviewReplaced = SkinSystem.TryApplySkinToMainMenuPhonePreview();
                        if (menuPreviewReplaced > 0)
                        {
                            Plugin.Log.LogInfo($"[Skin] ✓ {menuPreviewReplaced} menu preview renderer(s) updated.");
                        }
                    }
                    catch (Exception skinEx)
                    {
                        Plugin.Log.LogWarning($"[Skin] Apply error: {skinEx.Message}");
                    }
                }

                if (CharmState.CustomMesh != null || CharmState.CustomPrefab != null
                    || CharmState.PlayerCharmMeshes.Count > 0 || CharmState.PlayerCharmPrefabs.Count > 0)
                {
                    CharmSystem.TryApplyCharmToMeshFilters();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Scan] General error: {ex.Message}");
            }
        }

        public void ScheduleFastScans()
        {
            // Math.Max: cascading calls (e.g. scene load + RequestGameFastPoll within
            // the same frame) don't reset an already-running scan sequence back to 5.
            _pendingScans = Math.Max(_pendingScans, 5);
            _scanInterval = 1.0f;
            _nextScanDelay = 0f;
        }

        // Lightweight replacement for TriggerImmediateReplacement when called from
        // Harmony postfixes. Skips the synchronous DoScanAndReplace (which used to
        // cause a visible frame stutter on paint-bucket use) and lets the per-frame
        // fast poll pick up the new material.
        public void RequestGameFastPoll()
        {
            _gameFastPollFrames = FastPollBudgetFrames;
            _gameFastPollStableEmpty = 0;
            ScheduleFastScans();
        }

        public void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            Plugin.Log.LogDebug($"[Scene] Scene change to: {scene.name}");
            ScheduleFastScans();

            bool isMainMenu = scene.name.Contains("MainMenu");
            bool isGame = scene.name.Contains("Game");

            if (isMainMenu || isGame)
            {
                PlayerIdentity.ClearCaches();
                SkinSystem.ClearMaterialCache(); // invalidate IsMaterialPhoneDefault cache
                CharmState.SkinnedRendererIds.Clear();
                CharmState.MenuPreviewRendererIds.Clear();
                CharmState.MenuPreviewRootIds.Clear();
                CharmState.CharmedMeshFilterIds.Clear();
                CharmState.ReplacedOriginalCharmGOs.Clear(); // re-enable tracking for new scene
                CharmState.SkinDebugLogged = false;
                CharmState.DefaultPhoneTexture = null;
                CharmState.DefaultPhoneTextureLogged = false;
            }

            if (isGame)
            {
                CharmState.LocalCharmApplied = false;
                _charmRetries = 5;
                _charmRetryTimer = 0f;
                // No manifest refresh / download here: all remote assets were already prefetched at
                // the main menu (RequestMenuSync), so gameplay does no network work.
            }

            // Sync paint attempt for hot scene transitions where the renderer is
            // already in scene. Falls through to the per-frame poll otherwise.
            if (isMainMenu)
            {
                // Do all remote downloads here (in the menu): refresh the manifest and prefetch any
                // changed/missing assets, so a match never triggers network work.
                try { RemoteSkinService.RequestMenuSync(); } catch { }
                try { SkinSystem.TryApplySkinToMainMenuPhonePreview(); } catch { }
                _menuFastPollFrames = FastPollBudgetFrames;
            }
            if (isGame)
            {
                try { SkinSystem.TryApplySkinToPhoneSourceMaterials(); } catch { }
                _gameFastPollFrames = FastPollBudgetFrames;
                _gameFastPollStableEmpty = 0;
            }
        }

        // Kept for DestroyedPhoneEffectHandler postfixes which need a sync repaint
        // so the visual lands before the destroy animation. Skin/charm change
        // postfixes use RequestGameFastPoll instead to avoid frame stutter.
        public void TriggerImmediateReplacement()
        {
            DoScanAndReplace();
            ScheduleFastScans();
        }

        // MagicaCloth2 disables immediately, re-enables after N frames so the
        // physics engine has time to process the mesh swap before rebinding.
        public void ScheduleClothReenable(MonoBehaviour cloth)
        {
            _pendingClothList.Add(cloth);
            _pendingClothDelays.Add(3);
        }

        // Re-enable + BuildAndRun the game's MagicaCloth N frames after a mesh-only charm swap,
        // so it rebuilds against the swapped mesh (physics on first load, not only after restart).
        public void ScheduleClothRebuild(MonoBehaviour cloth, int frames)
        {
            if (cloth == null) return;
            _pendingClothRebuild.Add(cloth);
            _pendingClothRebuildDelays.Add(frames);
        }

        public void ScheduleRendererReenable(Renderer renderer, int frames = 10)
        {
            _pendingRendererReenable = renderer;
            _rendererReenableDelay = frames;
        }
    }
}
