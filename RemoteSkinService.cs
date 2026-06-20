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

        // Players already processed this session (avoids re-enqueuing every scan tick).
        private static readonly HashSet<string> _resolved =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The local player's own assets are pulled once per session (set after the first
        // attempt with a resolvable local nickname).
        private static bool _ownChecked = false;

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
            if (_manifestReady && !_manifestFailed && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanIntervalSeconds;
                ScanPlayers();
            }
            TickDownloads();
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
                var local = PlayerIdentity.GetLocalPlayerGO();

                // Pull the local player's OWN assets from the backend (once) into the plugin
                // root, so a user who uploaded their skin sees themselves without copying files.
                if (!_ownChecked && local != null)
                {
                    string localNick = PlayerIdentity.GetPlayerNickname(local);
                    if (!string.IsNullOrEmpty(localNick))
                        EnsureOwnAssets(localNick, PlayerIdentity.GetPlayerSteamId(local));
                }

                foreach (var p in PlayerIdentity.GetAllPlayers())
                {
                    if (p == null || p == local) continue;
                    string nick = PlayerIdentity.GetPlayerNickname(p);
                    if (string.IsNullOrEmpty(nick)) continue;
                    EnsureDownloaded(nick, PlayerIdentity.GetPlayerSteamId(p));
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
            if (!string.IsNullOrEmpty(steamId)) _bySteamId.TryGetValue(steamId, out entry);
            if (entry == null) _byName.TryGetValue(nick.Trim().ToLowerInvariant(), out entry);

            _ownChecked = true; // processed: don't retry every tick

            if (entry == null) return;

            string urlKey = !string.IsNullOrEmpty(steamId) ? steamId : entry.SteamId;
            string baseUrl = BaseUrl.TrimEnd('/');
            string dir = Plugin.PluginDirectory;

            if (entry.SkinHash != null)
                EnqueueOwnIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/skin",
                    Path.Combine(dir, "Skin.png"), "skin", nick, entry.SkinHash);

            if (entry.EmissionHash != null)
                EnqueueOwnIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/emission",
                    Path.Combine(dir, "Skin_Emission.png"), "emission", nick, entry.EmissionHash);

            if (entry.CharmHash != null)
                EnqueueOwnIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/charm",
                    Path.Combine(dir, "hatbundle"), "charm", nick, entry.CharmHash);
        }

        /// <summary>
        /// Like EnqueueIfStale but for own assets. Skips silently if a hand-placed file exists
        /// (present without a .hash sidecar) so the user's manual files always win.
        /// </summary>
        private static void EnqueueOwnIfStale(string url, string destPath, string kind, string nick, string hash)
        {
            if (_enqueued.Contains(url)) return;

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
                    ParseManifest(_manifestTask.Result);
                    _manifestReady = true;
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
            if (_resolved.Contains(nick)) return;

            // Resolve the entry: Steam ID takes priority, then normalized name.
            Entry entry = null;
            if (!string.IsNullOrEmpty(steamId)) _bySteamId.TryGetValue(steamId, out entry);
            if (entry == null) _byName.TryGetValue(nick.Trim().ToLowerInvariant(), out entry);

            _resolved.Add(nick); // processed: don't retry every tick

            if (entry == null) return;

            // URL key: prefer the (stable) Steam ID when we have it.
            string urlKey = !string.IsNullOrEmpty(steamId) ? steamId : entry.SteamId;
            string baseUrl = BaseUrl.TrimEnd('/');

            string skinsDir = Path.Combine(Plugin.PluginDirectory, "Skins");
            string charmsDir = Path.Combine(Plugin.PluginDirectory, "Charms");

            if (entry.SkinHash != null &&
                !CharmState.PlayerBaseTextures.ContainsKey(nick))
            {
                EnqueueIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/skin",
                    Path.Combine(skinsDir, nick + ".png"), "skin", nick, entry.SkinHash);
            }

            if (entry.EmissionHash != null &&
                !CharmState.PlayerEmissionTextures.ContainsKey(nick))
            {
                EnqueueIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/emission",
                    Path.Combine(skinsDir, nick + "_emission.png"), "emission", nick, entry.EmissionHash);
            }

            if (entry.CharmHash != null &&
                !CharmState.PlayerCharmMeshes.ContainsKey(nick) &&
                !CharmState.PlayerCharmPrefabs.ContainsKey(nick))
            {
                EnqueueIfStale($"{baseUrl}/asset/{Uri.EscapeDataString(urlKey)}/charm",
                    Path.Combine(charmsDir, nick + ".hatbundle"), "charm", nick, entry.CharmHash);
            }
        }

        /// <summary>Enqueues if the file is missing or its cached hash does not match.</summary>
        private static void EnqueueIfStale(string url, string destPath, string kind, string nick, string hash)
        {
            if (_enqueued.Contains(url)) return;

            // Cache: if the file exists and the .hash sidecar matches, reuse it.
            if (File.Exists(destPath) && ReadHashSidecar(destPath) == hash)
            {
                Plugin.Log.LogDebug($"[Remote] Cache hit '{nick}' ({kind}); loading from disk.");
                ApplyToState(destPath, kind, nick, own: false);
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
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[Remote] Download failed '{_active.Nick}' ({_active.Kind}): " +
                            $"{_activeTask.Exception?.GetBaseException().Message}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Remote] Post-download error: {ex.Message}");
                }
                finally
                {
                    _activeTask = null;
                }
                return;
            }

            if (_queue.Count == 0) return;

            _active = _queue.Dequeue();
            _activeTask = DownloadToFileAsync(_active.Url, _active.DestPath);
        }

        /// <summary>Downloads (thread pool) to a managed byte[] and writes to disk. true on success.</summary>
        private static async Task<bool> DownloadToFileAsync(string url, string destPath)
        {
            try
            {
                byte[] data = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (data == null || data.Length == 0) return false;
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
                        if (tex != null) CharmState.CustomSkinTexture = tex;
                    }
                    else if (kind == "emission")
                    {
                        var tex = SkinLoader.LoadTextureFromFile(path, "CustomPlayerSkinEmission");
                        if (tex != null) CharmState.CustomSkinEmissionTexture = tex;
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
