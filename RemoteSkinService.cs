using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Downloads skins/charms from the web backend "on demand": when a player appears
    /// in-game and has uploaded assets, they are downloaded into the Skins/Charms folders
    /// (cached on disk) and applied live via the existing loaders.
    ///
    /// Matches by Steam ID when available, otherwise by Steam name. The manifest is
    /// fetched once at boot from /api/manifest.tsv (TSV format to avoid a JSON dependency
    /// in the mod).
    ///
    /// Networking uses System.Net.Http (managed) because BepInEx IL2CPP plugins run on a
    /// real CoreCLR. Downloads run on the thread pool and return managed byte[] (no Il2Cpp
    /// array conversions); applying to Unity state happens on the main thread when the Task
    /// completes (in Tick()).
    ///
    /// Precedence: never overwrites an asset the user placed by hand —
    /// LoadCustomSkin()/LoadCharmMesh() run earlier at boot and, if the dictionary already
    /// contains the key, it is skipped here.
    /// </summary>
    internal static class RemoteSkinService
    {
        // --- Config (populated from Plugin.Load) ---
        public static bool Enabled = false;
        public static string BaseUrl = "";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // --- Manifest ---
        private sealed class Entry
        {
            public string SteamId;
            public string Name;       // normalized name (trim+lower)
            public string SkinHash;
            public string EmissionHash;
            public string CharmHash;
        }

        private static readonly Dictionary<string, Entry> _bySteamId =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Entry> _byName =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _manifestStarted = false;
        private static bool _manifestReady = false;
        private static bool _manifestFailed = false;
        private static Task<string> _manifestTask;

        // Last applied content hash per "key|kind" (key = in-memory asset key = nick, or
        // "__own__" for the local player's own assets). Drives LIVE refresh: when a periodic
        // manifest re-fetch yields a new hash, the mismatch re-triggers a download + repaint
        // without restarting the game.
        private static readonly Dictionary<string, string> _appliedHash =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Per-URL cooldown after a failed or hash-mismatched (stale CDN) download, so we don't
        // hammer the CDN while it catches up.
        private static readonly Dictionary<string, float> _retryAfter =
            new Dictionary<string, float>(StringComparer.Ordinal);

        // Manifest is refreshed only when requested (entering the main menu), never on a timer
        // during gameplay — so no network happens mid-match. _nextManifestRefetch is set to 0 to
        // fire once, then parked at MaxValue until the next menu request.
        private static float _nextManifestRefetch = 0f;
        private static string _lastManifestText = null;

        // When true, a full menu prefetch runs as soon as the manifest is ready (set at boot and
        // every time we enter the main menu).
        private static bool _syncRequested = true;

        // --- Download queue ---
        private struct Pending
        {
            public string Url;
            public string DestPath;
            public string Kind;       // "skin" | "emission" | "charm"
            public string Nick;       // player name (dictionary key in CharmState)
            public string Hash;       // expected hash (for the cache sidecar)
            public bool Own;          // true = the local player's own asset (apply to self)
        }

        private static readonly Queue<Pending> _queue = new Queue<Pending>();
        private static readonly HashSet<string> _enqueued = new HashSet<string>(StringComparer.Ordinal);
        private static Task<bool> _activeTask;
        private static Pending _active;

        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Kicks off the manifest request (once). Call at boot.</summary>
        public static void BeginManifestFetch()
        {
            if (!Enabled || _manifestStarted) return;
            if (string.IsNullOrEmpty(BaseUrl))
            {
                Plugin.Log.LogWarning("[Remote] BackendBaseUrl is empty; remote skins disabled.");
                _manifestFailed = true;
                return;
            }
            _manifestStarted = true;
            try
            {
                string url = BaseUrl.TrimEnd('/') + "/api/manifest.tsv";
                Plugin.Log.LogInfo($"[Remote] Fetching manifest: {url}");
                _manifestTask = _http.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Remote] Error starting manifest fetch: {ex.Message}");
                _manifestFailed = true;
            }
        }

        private static float _nextScanTime = 0f;
        private const float ScanIntervalSeconds = 1f;

        /// <summary>Advances network state. Call from Update().</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            TickManifest();
            MaybeRefetchManifest();
            // Menu prefetch: download everything that changed once the manifest is ready and no
            // fetch is in flight. Runs at boot and on each main-menu entry.
            if (_manifestReady && !_manifestFailed && _syncRequested && _manifestTask == null)
            {
                _syncRequested = false;
                SyncAllFromManifest();
            }
            if (_manifestReady && !_manifestFailed && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanIntervalSeconds;
                ScanPlayers();
            }
            TickDownloads();
        }

        /// <summary>Re-fetches the manifest when requested (entering the menu); never on a timer.</summary>
        private static void MaybeRefetchManifest()
        {
            if (!_manifestReady || _manifestTask != null) return; // initial fetch / one in flight
            if (Time.time < _nextManifestRefetch) return;
            _nextManifestRefetch = float.MaxValue; // park until the next explicit menu request
            try
            {
                // Cache-bust so Fastly is more likely to serve the fresh manifest; the per-asset
                // hash verification in DownloadToFileAsync is the real correctness guarantee.
                string url = BaseUrl.TrimEnd('/') + "/api/manifest.tsv?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _manifestTask = _http.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Remote] Manifest refetch start error: {ex.Message}");
            }
        }

        /// <summary>Forces a manifest refresh soon (used internally by the menu sync).</summary>
        public static void RequestManifestRefresh() => _nextManifestRefetch = 0f;

        /// <summary>
        /// Called when entering the main menu: refresh the manifest and re-run the full prefetch,
        /// so any DB change is pulled here (in the menu) and never mid-match.
        /// </summary>
        public static void RequestMenuSync()
        {
            _syncRequested = true;
            _nextManifestRefetch = 0f;
        }

        /// <summary>
        /// Discovers remote players and triggers downloads. Independent of the painting
        /// logic (which early-returns when no skins are loaded), avoiding the chicken-and-egg
        /// problem: with no local skins we still need to fetch remote ones. Throttled to 1s;
        /// EnsureDownloaded dedupes by nick, so after the first pass it is just O(1) lookups.
        /// </summary>
        private static void ScanPlayers()
        {
            try
            {
                // Remote players' assets are prefetched at the menu (SyncAllFromManifest) and already
                // sit in CharmState's dicts, so we do NOT start any network download in-match. The
                // only thing handled here is the LOCAL player's own assets, which need the in-game
                // nickname to resolve; it's hash-gated, so after the first session it's a no-op.
                var local = PlayerIdentity.GetLocalPlayerGO();
                if (local != null)
                {
                    string localNick = PlayerIdentity.GetPlayerNickname(local);
                    if (!string.IsNullOrEmpty(localNick))
                        EnsureOwnAssets(localNick, PlayerIdentity.GetPlayerSteamId(local));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Remote] ScanPlayers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads the local player's own skin/emission/charm (if uploaded) into the plugin
        /// root — Skin.png, Skin_Emission.png, hatbundle — the same names the boot loaders use.
        /// Runs once per session. Never overwrites hand-placed files (a file without a .hash
        /// sidecar is treated as the user's own and left alone).
        /// </summary>
        private static void EnsureOwnAssets(string nick, string steamId)
        {
            Entry entry = null;
            if (PlayerIdentity.IsValidSteamId64(steamId)) _bySteamId.TryGetValue(steamId, out entry);
            if (entry == null) _byName.TryGetValue(nick.Trim().ToLowerInvariant(), out entry);
            if (entry == null) return;

            string baseUrl = BaseUrl.TrimEnd('/');
            string dir = Plugin.PluginDirectory;

            if (entry.SkinHash != null)
                EnqueueOwnIfStale(AssetUrl(baseUrl, entry.SteamId, "skin", entry.SkinHash),
                    Path.Combine(dir, "Skin.png"), "skin", nick, entry.SkinHash);

            if (entry.EmissionHash != null)
                EnqueueOwnIfStale(AssetUrl(baseUrl, entry.SteamId, "emission", entry.EmissionHash),
                    Path.Combine(dir, "Skin_Emission.png"), "emission", nick, entry.EmissionHash);

            if (entry.CharmHash != null)
                EnqueueOwnIfStale(AssetUrl(baseUrl, entry.SteamId, "charm", entry.CharmHash),
                    Path.Combine(dir, "hatbundle"), "charm", nick, entry.CharmHash);
        }

        /// <summary>
        /// Like EnqueueIfStale but for own assets. Skips silently if a hand-placed file exists
        /// (present without a .hash sidecar) so the user's manual files always win.
        /// </summary>
        private static void EnqueueOwnIfStale(string url, string destPath, string kind, string nick, string hash)
        {
            string applyKey = "__own__|" + kind;
            if (_appliedHash.TryGetValue(applyKey, out var applied) && applied == hash) return;
            if (_enqueued.Contains(url)) return;
            if (_retryAfter.TryGetValue(url, out var until) && Time.time < until) return;

            if (File.Exists(destPath))
            {
                string sidecar = ReadHashSidecar(destPath);
                if (sidecar == null)
                {
                    // Hand-placed by the user (no sidecar): never touch it.
                    return;
                }
                if (sidecar == hash)
                {
                    // Already cached and current; the boot loader already applied it.
                    _appliedHash[applyKey] = hash;
                    return;
                }
            }

            _enqueued.Add(url);
            _queue.Enqueue(new Pending { Url = url, DestPath = destPath, Kind = kind, Nick = nick, Hash = hash, Own = true });
            Plugin.Log.LogInfo($"[Remote] Queued own {kind} download.");
        }

        private static void TickManifest()
        {
            if (_manifestTask == null) return;
            if (!_manifestTask.IsCompleted) return;

            try
            {
                if (_manifestTask.IsFaulted || _manifestTask.IsCanceled)
                {
                    _manifestFailed = true;
                    Plugin.Log.LogWarning($"[Remote] Manifest fetch failed: {_manifestTask.Exception?.GetBaseException().Message}");
                }
                else
                {
                    string text = _manifestTask.Result;
                    bool changed = text != _lastManifestText;
                    _lastManifestText = text;
                    ParseManifest(text);
                    _manifestReady = true;
                    // Park until the next explicit menu request — no periodic in-match refetch.
                    _nextManifestRefetch = float.MaxValue;
                    // Only log on a real change (the periodic re-fetch is otherwise silent).
                    // A changed manifest carries new hashes; the per-player hash tracking in
                    // EnsureDownloaded re-downloads + repaints the affected entries automatically.
                    if (changed)
                        Plugin.Log.LogInfo($"[Remote] Manifest ready: {_bySteamId.Count} by SteamID, {_byName.Count} by name.");
                }
            }
            catch (Exception ex)
            {
                _manifestFailed = true;
                Plugin.Log.LogError($"[Remote] Error parsing manifest: {ex.Message}");
            }
            finally
            {
                _manifestTask = null;
            }
        }

        private static void ParseManifest(string tsv)
        {
            _bySteamId.Clear();
            _byName.Clear();
            if (string.IsNullOrEmpty(tsv)) return;

            foreach (var rawLine in tsv.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ');
                if (line.Length == 0 || line[0] == '#') continue;

                var cols = line.Split('\t');
                if (cols.Length < 5) continue;

                var e = new Entry
                {
                    SteamId = cols[0].Trim(),
                    Name = cols[1].Trim(),
                    SkinHash = NullIfDash(cols[2]),
                    EmissionHash = NullIfDash(cols[3]),
                    CharmHash = NullIfDash(cols[4]),
                };
                if (!string.IsNullOrEmpty(e.SteamId)) _bySteamId[e.SteamId] = e;
                if (!string.IsNullOrEmpty(e.Name)) _byName[e.Name] = e;
            }
        }

        private static string NullIfDash(string s)
        {
            s = s?.Trim();
            return (string.IsNullOrEmpty(s) || s == "-") ? null : s;
        }

        /// <summary>
        /// Called when a remote player is discovered. If they have assets in the manifest
        /// that are not already cached/loaded, enqueues them for download.
        /// </summary>
        /// <param name="nick">Visible Steam name (dictionary key in CharmState).</param>
        /// <param name="steamId">Steam ID if it could be resolved, or null/empty.</param>
        public static void EnsureDownloaded(string nick, string steamId)
        {
            if (!Enabled || !_manifestReady || _manifestFailed) return;
            if (string.IsNullOrEmpty(nick)) return;

            // Resolve the entry: a valid SteamID64 takes priority, then normalized name.
            Entry entry = null;
            if (PlayerIdentity.IsValidSteamId64(steamId)) _bySteamId.TryGetValue(steamId, out entry);
            if (entry == null) _byName.TryGetValue(nick.Trim().ToLowerInvariant(), out entry);
            if (entry == null) return;

            EnqueueEntry(entry, nick);
        }

        /// <summary>
        /// Menu-time prefetch: walks every manifest entry and, for each asset, runs the existing
        /// hash gate so ONLY changed/missing assets download. Populates CharmState's dicts so
        /// in-match painting needs zero network. Called at boot and on every main-menu entry.
        /// </summary>
        public static void SyncAllFromManifest()
        {
            if (!Enabled || !_manifestReady || _manifestFailed) return;
            int n = 0;
            foreach (var kv in _byName)
            {
                EnqueueEntry(kv.Value, kv.Value.Name);
                n++;
            }
            Plugin.Log.LogInfo($"[Remote] Menu sync: checked {n} manifest entries (downloads only the changed ones).");
        }

        /// <summary>
        /// Enqueues hash-gated downloads for one manifest entry (skin/emission/charm). The asset URL
        /// and cache file are keyed by the uploader's SteamID64 (always present, stable, filesystem
        /// -safe); the in-memory dict key stays the nickname (synced across machines). EnqueueIfStale
        /// dedupes by (nick,kind,hash) and lets a changed manifest hash re-download.
        /// </summary>
        private static void EnqueueEntry(Entry entry, string nick)
        {
            string fileKey = SafeFileKey(entry, nick);
            string baseUrl = BaseUrl.TrimEnd('/');
            string skinsDir = Path.Combine(Plugin.PluginDirectory, "Skins");
            string charmsDir = Path.Combine(Plugin.PluginDirectory, "Charms");

            if (entry.SkinHash != null)
                EnqueueIfStale(AssetUrl(baseUrl, entry.SteamId, "skin", entry.SkinHash),
                    Path.Combine(skinsDir, fileKey + ".png"), "skin", nick, entry.SkinHash);

            if (entry.EmissionHash != null)
                EnqueueIfStale(AssetUrl(baseUrl, entry.SteamId, "emission", entry.EmissionHash),
                    Path.Combine(skinsDir, fileKey + "_emission.png"), "emission", nick, entry.EmissionHash);

            if (entry.CharmHash != null)
                EnqueueIfStale(AssetUrl(baseUrl, entry.SteamId, "charm", entry.CharmHash),
                    Path.Combine(charmsDir, fileKey + ".hatbundle"), "charm", nick, entry.CharmHash);
        }

        /// <summary>Asset download URL, keyed by SteamID64 and cache-busted by content hash.</summary>
        private static string AssetUrl(string baseUrl, string steamId, string kind, string hash) =>
            $"{baseUrl}/asset/{Uri.EscapeDataString(steamId)}/{kind}?v={hash}";

        /// <summary>Filesystem-safe cache file key: the SteamID64 (digits) when present, else a sanitized name.</summary>
        private static string SafeFileKey(Entry entry, string nick) =>
            !string.IsNullOrEmpty(entry.SteamId) ? entry.SteamId : Sanitize(nick);

        /// <summary>Replaces characters illegal in a Windows filename so the cache write can't fail.</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string r = sb.ToString().Trim().TrimEnd('.', ' ');
            return r.Length == 0 ? "unknown" : r;
        }

        /// <summary>
        /// Enqueues a download unless the asset is already current. "Current" = the same hash was
        /// already applied this session, or a cached file with a matching .hash sidecar exists
        /// (loaded from disk here). A changed manifest hash falls through to a fresh download.
        /// </summary>
        private static void EnqueueIfStale(string url, string destPath, string kind, string nick, string hash)
        {
            string applyKey = nick + "|" + kind;
            if (_appliedHash.TryGetValue(applyKey, out var applied) && applied == hash) return;
            if (_enqueued.Contains(url)) return;
            if (_retryAfter.TryGetValue(url, out var until) && Time.time < until) return;

            // Cache: if the file exists and the .hash sidecar matches, reuse it.
            if (File.Exists(destPath) && ReadHashSidecar(destPath) == hash)
            {
                Plugin.Log.LogDebug($"[Remote] Cache hit '{nick}' ({kind}); loading from disk.");
                ApplyToState(destPath, kind, nick, own: false);
                _appliedHash[applyKey] = hash;
                return;
            }

            _enqueued.Add(url);
            _queue.Enqueue(new Pending { Url = url, DestPath = destPath, Kind = kind, Nick = nick, Hash = hash, Own = false });
            Plugin.Log.LogInfo($"[Remote] Queued download '{nick}' ({kind}).");
        }

        private static void TickDownloads()
        {
            // One download in flight at a time (smooths network use and simplifies state).
            if (_activeTask != null)
            {
                if (!_activeTask.IsCompleted) return;

                try
                {
                    if (!_activeTask.IsFaulted && !_activeTask.IsCanceled && _activeTask.Result)
                    {
                        WriteHashSidecar(_active.DestPath, _active.Hash);
                        Plugin.Log.LogInfo($"[Remote] ✓ Downloaded '{_active.Nick}' ({_active.Kind}).");
                        ApplyToState(_active.DestPath, _active.Kind, _active.Nick, _active.Own);
                        string applyKey = (_active.Own ? "__own__" : _active.Nick) + "|" + _active.Kind;
                        _appliedHash[applyKey] = _active.Hash;
                    }
                    else
                    {
                        // Network error or hash mismatch (stale CDN). Cooldown, then a later poll
                        // retries — self-healing once the CDN serves the bytes matching the hash.
                        _retryAfter[_active.Url] = Time.time + 20f;
                        Plugin.Log.LogWarning($"[Remote] Download failed/stale '{_active.Nick}' ({_active.Kind}); will retry.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Remote] Post-download error: {ex.Message}");
                }
                finally
                {
                    // Always drop the in-flight marker: on success the hash gate prevents re-enqueue,
                    // on failure the cooldown does, and a later manifest change re-enqueues a new URL.
                    _enqueued.Remove(_active.Url);
                    _activeTask = null;
                }
                return;
            }

            if (_queue.Count == 0) return;

            _active = _queue.Dequeue();
            _activeTask = DownloadToFileAsync(_active.Url, _active.DestPath, _active.Hash);
        }

        /// <summary>
        /// Downloads (thread pool) to a managed byte[], verifies its SHA-256 against the expected
        /// manifest hash, and writes to disk. Returns false on a network error OR a hash mismatch
        /// (stale CDN) — in which case nothing is written, so a stale asset is never cached.
        /// </summary>
        private static async Task<bool> DownloadToFileAsync(string url, string destPath, string expectedHash)
        {
            try
            {
                byte[] data = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (data == null || data.Length == 0) return false;
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    string actual = Sha256Hex(data);
                    if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.LogWarning($"[Remote] Hash mismatch for {url} (stale CDN?); not caching.");
                        return false;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                File.WriteAllBytes(destPath, data);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Remote] Download error {url}: {ex.Message}");
                return false;
            }
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(data);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Applies a freshly downloaded/cached asset to the mod state live.</summary>
        private static void ApplyToState(string path, string kind, string nick, bool own)
        {
            try
            {
                if (own)
                {
                    // The local player's own assets feed the same state the boot loaders use.
                    if (kind == "skin")
                    {
                        var tex = SkinLoader.LoadTextureFromFile(path, "CustomPlayerSkin");
                        if (tex != null)
                        {
                            CharmState.CustomSkinTexture = tex;
                            // Repaint so a DB change to your own skin appears without a restart.
                            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
                        }
                    }
                    else if (kind == "emission")
                    {
                        var tex = SkinLoader.LoadTextureFromFile(path, "CustomPlayerSkinEmission");
                        if (tex != null)
                        {
                            CharmState.CustomSkinEmissionTexture = tex;
                            CharmReplacerBehavior.Instance?.RequestGameFastPoll();
                        }
                    }
                    else if (kind == "charm")
                    {
                        // Saved as PluginDirectory/hatbundle, so StartNextBundle treats it as local.
                        CharmSystem.EnqueueRemoteCharmBundle(path, "local");
                    }
                    return;
                }

                if (kind == "skin")
                {
                    var tex = SkinLoader.LoadTextureFromFile(path, $"skin_{nick}");
                    if (tex != null)
                    {
                        CharmState.PlayerBaseTextures[nick] = tex;
                        // Schedule a repaint: skins for charm-less players have no other
                        // event to trigger one.
                        CharmReplacerBehavior.Instance?.RequestGameFastPoll();
                    }
                }
                else if (kind == "emission")
                {
                    var tex = SkinLoader.LoadTextureFromFile(path, $"skinemission_{nick}");
                    if (tex != null)
                    {
                        CharmState.PlayerEmissionTextures[nick] = tex;
                        // The base skin was likely already painted (and the renderer marked
                        // "known"); the re-skin guard now detects the missing emission map.
                        CharmReplacerBehavior.Instance?.RequestGameFastPoll();
                    }
                }
                else if (kind == "charm")
                {
                    CharmSystem.EnqueueRemoteCharmBundle(path, nick);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Remote] Error applying '{kind}' for '{nick}': {ex.Message}");
            }
        }

        // --- Hash sidecar (cache) ---

        private static string ReadHashSidecar(string assetPath)
        {
            try
            {
                string p = assetPath + ".hash";
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }
        }

        private static void WriteHashSidecar(string assetPath, string hash)
        {
            try { File.WriteAllText(assetPath + ".hash", hash ?? ""); }
            catch { /* non-critical */ }
        }
    }
}
