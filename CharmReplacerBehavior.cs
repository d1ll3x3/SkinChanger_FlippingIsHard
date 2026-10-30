using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CharmReplacer
{
    internal class CharmReplacerBehavior : MonoBehaviour
    {
        public static CharmReplacerBehavior Instance { get; private set; }

        private const float ScanInterval = 1.0f;
        private float _nextScanDelay = 0f;
        private int _pendingScans = 0;
        private int _charmRetries = 0;
        private float _charmRetryTimer = 0f;

        // Per-frame poll after scene transitions / paint-bucket events. Catches the
        // target renderer within ~16ms of it spawning instead of waiting for the
        // scheduled scan. Auto-stops on first paint or when stable-empty for a
        // few frames (cosmetic change with nothing to paint).
        private int _gameFastPollFrames = 0;
        private int _gameFastPollStableEmpty = 0;
        private const int FastPollBudgetFrames = 180;
        private const int StableEmptyFramesToStop = 5;

        // Skin retry: the phone visual is an async-loaded prefab (NetworkPlayer(Clone)/PlayerVisual)
        // that appears seconds-to-tens-of-seconds after scene entry — long after the fast poll gives
        // up. Retry the skin scan at low frequency while in gameplay (v1.7 game scenes are GUID-named,
        // so we key off "not the main menu" rather than an unreliable scene-name match). Already-painted
        // renderers are skipped, so this is cheap and also self-heals if the game reverts our material.
        private float _skinRetryTimer = 0f;
        private const float SkinRetryInterval = 1.5f;

        private bool _inMainMenu = false;

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

                // Who sees the custom cover/charm, and the icons of the menu's "Custom" entries.
                SkinOverride.Tick();
                LocalVisuals.Tick();
                IconStudio.Tick();

                if (!HasCharmSource && !HasSkinSource) return;

                if (_gameFastPollFrames > 0)
                {
                    if (HasSkinSource && CharmState.SkinnedRendererIds.Count == 0)
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
                        _nextScanDelay = ScanInterval;
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

                // Skin retry: keep scanning at low frequency while in gameplay until the async phone
                // visual loads. TryApplySkinToPhoneSourceMaterials skips already-painted renderers, so
                // re-running is cheap/idempotent, catches remote players whose phone loads late, and
                // self-heals if the game re-clones/reverts the phone material.
                if (HasSkinSource && !_inMainMenu && !CharmState.SkinEverPainted)
                {
                    _skinRetryTimer += Time.deltaTime;
                    if (_skinRetryTimer >= SkinRetryInterval)
                    {
                        _skinRetryTimer = 0f;
                        SkinSystem.TryApplySkinToPhoneSourceMaterials();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CharmReplacerBehavior] Update error: {ex}");
            }
        }

        // After animation and physics have placed the phone, so swinging charms follow this frame's pose.
        public void LateUpdate()
        {
            try { CharmSwing.Tick(Time.deltaTime); }
            catch (Exception ex) { Plugin.Log.LogError($"[CharmPhysics] Swing error: {ex}"); }
            try { CoverExtras.TickFrame(); }
            catch (Exception ex) { Plugin.Log.LogError($"[Skin] Cover extras frame error: {ex}"); }
        }

        private static bool HasSkinSource =>
            CharmState.CustomSkinTexture != null || CharmState.PlayerBaseTextures.Count > 0;

        private static bool HasCharmSource =>
            CharmState.CustomMesh != null || CharmState.CustomPrefab != null
            || CharmState.PlayerCharmMeshes.Count > 0 || CharmState.PlayerCharmPrefabs.Count > 0;

        public void DoScanAndReplace()
        {
            if (!HasCharmSource && !HasSkinSource) return;

            try
            {
                if (HasSkinSource)
                {
                    int sourceMaterialsReplaced = SkinSystem.TryApplySkinToPhoneSourceMaterials();
                    if (sourceMaterialsReplaced > 0)
                        Plugin.Log.LogInfo($"[Skin] ✓ {sourceMaterialsReplaced} phone source material(s) updated.");
                }

                if (HasCharmSource)
                    CharmSystem.TryApplyCharmToMeshFilters();
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
            // v1.7 gameplay scenes are GUID/addressable-named (e.g. "b8fc7983...", "Map_NW"), not "Game".
            // Treat any non-menu, non-bootstrap scene as gameplay.
            bool isGame = !isMainMenu
                          && !string.IsNullOrEmpty(scene.name)
                          && !scene.name.Contains("Bootstrap")
                          && !scene.name.Contains("Preload")
                          && !scene.name.Contains("Init");
            _inMainMenu = isMainMenu;

            if (isMainMenu || isGame)
            {
                PlayerIdentity.ClearCaches();
                SkinSystem.ClearMaterialCache();
                CharmState.SkinnedRendererIds.Clear();
                CharmState.CharmedMeshFilterIds.Clear();
                CharmState.ReplacedOriginalCharmGOs.Clear(); // re-enable tracking for new scene
                CharmState.SkinDebugLogged = false;
                SkinOverride.ClearSceneState();
                LocalVisuals.ClearSceneState(isMainMenu);
            }

            if (isGame)
            {
                CharmState.LocalCharmApplied = false;
                _charmRetries = 5;
                _charmRetryTimer = 0f;
                _skinRetryTimer = 0f;
                // No manifest refresh / download here: all remote assets were already prefetched at
                // the main menu (RequestMenuSync), so gameplay does no network work.
            }

            if (isMainMenu)
            {
                // Do all remote downloads here (in the menu): refresh the manifest and prefetch any
                // changed/missing assets, so a match never triggers network work. The menu phone and
                // the customization preview are painted by LocalVisuals.
                RemoteSkinService.RequestMenuSync();
            }
            // Sync paint attempt for hot scene transitions where the renderer is
            // already in scene. Falls through to the per-frame poll otherwise.
            if (isGame)
            {
                SkinSystem.TryApplySkinToPhoneSourceMaterials();
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
    }
}
