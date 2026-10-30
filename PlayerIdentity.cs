using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;

namespace CharmReplacer
{
    internal static class PlayerIdentity
    {
        // GetPlayerNickname is called every scan tick per player; each nickname logs only on first
        // observation instead of flooding the log.
        private static readonly HashSet<string> _loggedNicknames = new HashSet<string>(StringComparer.Ordinal);

        // --- Per-frame caches (cleared on scene change) ---
        private static int _cachedFrame = -1;
        private static List<GameObject> _cachedAllPlayers;
        private static int _cachedLocalPlayerFrame = -1;
        private static GameObject _cachedLocalPlayer;
        private static readonly Dictionary<int, string> _nicknameCache = new Dictionary<int, string>();

        private static PlayerNetworked GetNetworked(GameObject p) => p.GetComponent<PlayerNetworked>();

        public static string GetPlayerNickname(GameObject p)
        {
            if (p == null) return string.Empty;

            // Fast path: check cache by instance ID
            int instanceId = p.GetInstanceID();
            if (_nicknameCache.TryGetValue(instanceId, out var cachedNick))
                return cachedNick;

            try
            {
                var networked = GetNetworked(p);
                var syncVar = networked != null ? networked.NicknameSyncVar : null;
                string nick = syncVar != null ? syncVar.Value : null;
                // Empty until the SyncVar arrives; not cached, so the next scan asks again.
                if (string.IsNullOrEmpty(nick)) return string.Empty;

                if (_loggedNicknames.Add(nick))
                    Plugin.Log.LogInfo($"[PlayerIdentity] + Nickname: {nick}");
                _nicknameCache[instanceId] = nick;
                return nick;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerIdentity] GetPlayerNickname error: {ex}");
            }
            return string.Empty;
        }

        private static readonly Dictionary<int, string> _steamIdCache = new Dictionary<int, string>();
        private static readonly HashSet<int> _loggedSteamIds = new HashSet<int>();

        /// <summary>
        /// SteamID64 of a player, or string.Empty when it can't be resolved (matching then falls back
        /// to the nickname). The v1.7 release syncs no SteamID64 on PlayerNetworked: the local player's
        /// own comes from Steamworks, everyone else's from the Steam lobby, which lists each member under
        /// the same persona name NicknameSyncVar carries.
        /// </summary>
        public static string GetPlayerSteamId(GameObject p)
        {
            if (p == null) return string.Empty;
            int instanceId = p.GetInstanceID();
            if (_steamIdCache.TryGetValue(instanceId, out var cached))
                return cached;

            string result = null;
            try
            {
                var networked = GetNetworked(p);
                result = networked != null && networked.IsOwner
                    ? Steamworks.SteamUser.GetSteamID().m_SteamID.ToString()
                    : SteamIdFromLobby(GetPlayerNickname(p));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[PlayerIdentity] GetPlayerSteamId error: {ex.Message}");
            }

            if (!IsValidSteamId64(result))
            {
                // A member can join the lobby after their player spawns: keep asking instead of caching the miss.
                if (_loggedSteamIds.Add(instanceId))
                    Plugin.Log.LogInfo("[PlayerIdentity] Resolved SteamID: <none — using name>");
                return string.Empty;
            }

            Plugin.Log.LogInfo($"[PlayerIdentity] Resolved SteamID: {result}");
            _steamIdCache[instanceId] = result;
            return result;
        }

        private static EHS.Steam.SteamManager _steamManager;

        /// <summary>SteamID64 of the current lobby member whose persona name is <paramref name="nick"/>, else null.</summary>
        private static string SteamIdFromLobby(string nick)
        {
            if (string.IsNullOrEmpty(nick)) return null;
            try
            {
                if (_steamManager == null)
                    _steamManager = UnityEngine.Object.FindAnyObjectByType<EHS.Steam.SteamManager>(FindObjectsInactive.Exclude);
                if (_steamManager == null) return null;

                var lobby = _steamManager.CurrentLobbySteamId;
                if (lobby.m_SteamID == 0UL) return null;

                string wanted = nick.Trim();
                int count = Steamworks.SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < count; i++)
                {
                    var member = Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                    string name = Steamworks.SteamFriends.GetFriendPersonaName(member);
                    if (!string.Equals(name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    return member.m_SteamID.ToString();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[PlayerIdentity] Lobby SteamID lookup error: {ex.Message}");
            }
            return null;
        }

        /// <summary>A valid SteamID64 is 17 digits in the public-account range (starts 7656119…).</summary>
        public static bool IsValidSteamId64(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 17) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return s.StartsWith("7656", StringComparison.Ordinal);
        }

        public static List<GameObject> GetAllPlayers()
        {
            // Frame-cached: FindObjectsOfType is expensive; reuse result within the same frame
            int currentFrame = Time.frameCount;
            if (_cachedFrame == currentFrame && _cachedAllPlayers != null)
                return _cachedAllPlayers;

            var list = new List<GameObject>();
            try
            {
                var tagged = GameObject.FindGameObjectsWithTag("Player");
                if (tagged != null) list.AddRange(tagged);

                // Untagged players: only searched when the tag found nothing (always the case in the
                // main menu, which this runs in every second).
                if (list.Count == 0)
                {
                    var networked = UnityEngine.Object.FindObjectsByType<PlayerNetworked>(FindObjectsSortMode.None);
                    if (networked != null)
                        foreach (var n in networked)
                            if (n != null) list.Add(n.gameObject);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[PlayerIdentity] GetAllPlayers error: {ex.Message}");
            }

            _cachedFrame = currentFrame;
            _cachedAllPlayers = list;
            return list;
        }

        /// <summary>Clear per-scene caches (call on scene change).</summary>
        public static void ClearCaches()
        {
            _cachedFrame = -1;
            _cachedAllPlayers = null;
            _cachedLocalPlayerFrame = -1;
            _cachedLocalPlayer = null;
            _nicknameCache.Clear();
            _steamIdCache.Clear();
        }

        private static bool IsLocalPlayer(GameObject p)
        {
            if (p == null) return false;
            var cam = p.GetComponentInChildren<Camera>(false);
            if (cam != null && cam.isActiveAndEnabled) return true;

            // FishNet ownership of the player's network object.
            var networked = GetNetworked(p);
            return networked != null && networked.IsOwner;
        }

        public static GameObject GetLocalPlayerGO()
        {
            // Frame-cached, misses included: before ownership resolves every scan of the frame would
            // otherwise repeat the search.
            int currentFrame = Time.frameCount;
            if (_cachedLocalPlayerFrame == currentFrame)
                return _cachedLocalPlayer;

            GameObject result = null;
            try
            {
                var allPlayers = GetAllPlayers();

                // 100% Safe Criteria: The network owner
                foreach (var p in allPlayers)
                {
                    if (IsLocalPlayer(p)) { result = p; break; }
                }

                // Fallback (lobby / single-player ONLY): nearest player to Camera.main.
                // CRITICAL: only when there is exactly ONE player — otherwise, in multiplayer
                // before the network owner resolves, this picks the nearest REMOTE player as
                // "local", which makes the local charm get applied to the wrong player (and,
                // via the charm slot being marked, blocks the remote player's own physics).
                // With more than one player we return null and let the caller retry until
                // IsOwner resolves.
                if (result == null && allPlayers.Count == 1)
                {
                    var cam = Camera.main;
                    var p = allPlayers[0];
                    if (cam != null && p != null && Vector3.Distance(p.transform.position, cam.transform.position) <= 10f)
                        result = p;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[PlayerIdentity] GetLocalPlayerGO error: {ex.Message}");
            }

            _cachedLocalPlayerFrame = currentFrame;
            _cachedLocalPlayer = result;
            return result;
        }
    }
}
