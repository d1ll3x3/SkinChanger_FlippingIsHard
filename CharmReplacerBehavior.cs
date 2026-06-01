using System;
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

        // MagicaCloth2 delayed re-enable (disable → wait N frames → re-enable)
        private MonoBehaviour _pendingClothReenable = null;
        private int _reenableFrameDelay = 0;

        // Renderer re-enable after BuildAndRun (hide during async rebuild)
        private Renderer _pendingRendererReenable = null;
        private int _rendererReenableDelay = 0;

        public void Awake()
        {
            Instance = this;
            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += new Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>(OnSceneLoaded);
                CharmSystem.LoadCharmMesh();
                SkinLoader.LoadCustomSkin();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CharmReplacerBehavior] Awake error: {ex}");
            }
        }

        public void Update()
        {
            try
            {
                CharmSystem.UpdateBundleLoading();

                // MagicaCloth2 delayed re-enable
                if (_pendingClothReenable != null)
                {
                    if (_reenableFrameDelay > 0)
                    {
                        _reenableFrameDelay--;
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"[CharmPhysics] Re-enabling cloth on '{_pendingClothReenable.gameObject.name}'");
                        _pendingClothReenable.enabled = true;
                        _pendingClothReenable = null;
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
                if (CharmState.CustomMesh != null && !CharmState.LocalCharmApplied && _charmRetries > 0)
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
            _pendingScans = 15;
            _scanInterval = 0.5f;
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
                CharmState.SkinnedRendererIds.Clear();
                CharmState.MenuPreviewRendererIds.Clear();
                CharmState.MenuPreviewRootIds.Clear();
                CharmState.CharmedMeshFilterIds.Clear();
                CharmState.SkinDebugLogged = false;
                CharmState.DefaultPhoneTexture = null;
                CharmState.DefaultPhoneTextureLogged = false;
            }

            if (isGame)
            {
                CharmState.LocalCharmApplied = false;
                _charmRetries = 5;
                _charmRetryTimer = 0f;
            }

            // Sync paint attempt for hot scene transitions where the renderer is
            // already in scene. Falls through to the per-frame poll otherwise.
            if (isMainMenu)
            {
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
            _pendingClothReenable = cloth;
            _reenableFrameDelay = 3;
        }

        public void ScheduleRendererReenable(Renderer renderer, int frames = 10)
        {
            _pendingRendererReenable = renderer;
            _rendererReenableDelay = frames;
        }
    }
}
