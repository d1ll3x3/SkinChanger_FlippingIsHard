using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace CharmReplacer
{
    internal static class PlayerIdentity
    {
        private static bool _hasDumpedPlayerNetworked = false;
        // GetPlayerNickname is called every scan tick per player; without this
        // gate the info log floods at hundreds of lines per second once a
        // remote player is in scene. Each unique (resolver, nickname) pair
        // logs only on first observation.
        private static readonly HashSet<string> _loggedNicknameResolutions = new HashSet<string>(StringComparer.Ordinal);

        // --- Per-frame caches (cleared on scene change) ---
        private static int _cachedFrame = -1;
        private static List<GameObject> _cachedAllPlayers;
        private static readonly Dictionary<int, string> _nicknameCache = new Dictionary<int, string>();

        public static string GetPlayerNickname(GameObject p)
        {
            if (p == null) return string.Empty;

            // Fast path: check cache by instance ID
            int instanceId = p.GetInstanceID();
            if (_nicknameCache.TryGetValue(instanceId, out var cachedNick))
                return cachedNick;

            try
            {
                var mbs = p.GetComponents<MonoBehaviour>();
                MonoBehaviour netObj = null;
                foreach (var mb in mbs)
                {
                    if (mb != null && mb.GetIl2CppType() != null && mb.GetIl2CppType().Name == "PlayerNetworked")
                    {
                        netObj = mb;
                        break;
                    }
                }

                if (netObj != null)
                {
                    var il2CppType = netObj.GetIl2CppType();

                    // Dump all members ONCE per session so we know the real structure
                    if (!_hasDumpedPlayerNetworked)
                    {
                        _hasDumpedPlayerNetworked = true;
                        Plugin.Log.LogDebug("[PlayerIdentity] === FIRST ENCOUNTER: Dumping PlayerNetworked members ===");
                        DumpTypeMembers(il2CppType);
                    }

                    // Candidate property/field names to try (ordered by likelihood)
                    string[] candidateNames = {
                        "NicknameSyncVar", "Nickname", "SteamName",
                        "Username", "PlayerName", "Name", "DisplayName"
                    };

                    foreach (var name in candidateNames)
                    {
                        // --- Try as PROPERTY ---
                        var prop = il2CppType.GetProperty(name);
                        if (prop != null)
                        {
                            try
                            {
                                string propTypeName = "";
                                try { propTypeName = prop.PropertyType?.Name ?? "?"; } catch { }

                                var getter = prop.GetGetMethod();
                                if (getter != null)
                                {
                                    var res = getter.Invoke(netObj, null);
                                    if (res != null)
                                    {
                                        string str = TryExtractString(res, propTypeName);
                                        if (!string.IsNullOrEmpty(str))
                                        {
                                            string key = "p:" + name + "=" + str;
                                            if (_loggedNicknameResolutions.Add(key))
                                                Plugin.Log.LogInfo($"[PlayerIdentity] + Nickname found via property '{name}': {str}");
                                            _nicknameCache[instanceId] = str;
                                            return str;
                                        }
                                        Plugin.Log.LogDebug($"[PlayerIdentity]   property '{name}' ({propTypeName}) returned empty/null string");
                                    }
                                }
                            }
                            catch { }
                        }

                        // --- Try as FIELD ---
                        var field = il2CppType.GetField(name);
                        if (field != null)
                        {
                            try
                            {
                                string fieldTypeName = "";
                                try { fieldTypeName = field.FieldType?.Name ?? "?"; } catch { }

                                var val = field.GetValue(netObj);
                                if (val != null)
                                {
                                    string str = TryExtractString(val, fieldTypeName);
                                    if (!string.IsNullOrEmpty(str))
                                    {
                                        string key = "f:" + name + "=" + str;
                                        if (_loggedNicknameResolutions.Add(key))
                                            Plugin.Log.LogInfo($"[PlayerIdentity] + Nickname found via field '{name}': {str}");
                                        _nicknameCache[instanceId] = str;
                                        return str;
                                    }
                                    Plugin.Log.LogDebug($"[PlayerIdentity]   field '{name}' ({fieldTypeName}) returned empty/null string");
                                }
                            }
                            catch { }
                        }
                    }

                    if (_loggedNicknameResolutions.Add("!none"))
                        Plugin.Log.LogWarning("[PlayerIdentity] ! No valid nickname found in any candidate.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerIdentity] GetPlayerNickname error: {ex}");
            }
            return string.Empty;
        }

        /// <summary>
        /// Try to extract a managed string from an Il2Cpp object.
        /// If the object itself is a string, extract it directly.
        /// If it's a SyncVar&lt;string&gt; or similar wrapper, try .Value property.
        /// </summary>
        private static string TryExtractString(Il2CppSystem.Object obj, string typeName)
        {
            if (obj == null) return null;

            // 1) Direct string extraction
            string direct = IL2CPP.Il2CppStringToManaged(obj.Pointer);
            if (!string.IsNullOrEmpty(direct) && direct[0] != '\0')
                return direct;

            // 2) If it looks like a SyncVar wrapper, try .Value
            if (typeName.Contains("SyncVar") || typeName.Contains("NetworkVar"))
            {
                try
                {
                    var objType = obj.GetIl2CppType();
                    if (objType != null)
                    {
                        var valueProp = objType.GetProperty("Value");
                        if (valueProp != null)
                        {
                            var getter = valueProp.GetGetMethod();
                            if (getter != null)
                            {
                                var inner = getter.Invoke(obj, null);
                                if (inner != null)
                                {
                                    string innerStr = IL2CPP.Il2CppStringToManaged(inner.Pointer);
                                    if (!string.IsNullOrEmpty(innerStr) && innerStr[0] != '\0')
                                        return innerStr;
                                }
                            }
                        }
                        // Also try .Value field
                        var valueField = objType.GetField("Value");
                        if (valueField != null)
                        {
                            var inner = valueField.GetValue(obj);
                            if (inner != null)
                            {
                                string innerStr = IL2CPP.Il2CppStringToManaged(inner.Pointer);
                                if (!string.IsNullOrEmpty(innerStr) && innerStr[0] != '\0')
                                    return innerStr;
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        public static System.Collections.Generic.List<GameObject> GetAllPlayers()
        {
            // Frame-cached: FindObjectsOfType is expensive; reuse result within the same frame
            int currentFrame = Time.frameCount;
            if (_cachedFrame == currentFrame && _cachedAllPlayers != null)
                return _cachedAllPlayers;

            var list = new System.Collections.Generic.List<GameObject>();
            try
            {
                var tagged = GameObject.FindGameObjectsWithTag("Player");
                if (tagged != null) list.AddRange(tagged);
                
                var networked = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var n in networked)
                {
                    if (n != null && n.GetIl2CppType() != null && n.GetIl2CppType().Name == "PlayerNetworked" && !list.Contains(n.gameObject))
                        list.Add(n.gameObject);
                }
            }
            catch { }

            _cachedFrame = currentFrame;
            _cachedAllPlayers = list;
            return list;
        }

        /// <summary>Clear per-scene caches (call on scene change).</summary>
        public static void ClearCaches()
        {
            _cachedFrame = -1;
            _cachedAllPlayers = null;
            _nicknameCache.Clear();
        }

        public static bool IsLocalPlayer(GameObject p)
        {
            if (p == null) return false;
            try
            {
                var cam = p.GetComponentInChildren<Camera>(false);
                if (cam != null && cam.isActiveAndEnabled) return true;

                var mbs = p.GetComponents<MonoBehaviour>();
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    var typeObj = mb.GetIl2CppType();
                    if (typeObj != null)
                    {
                        var isOwnerProp = typeObj.GetProperty("IsOwner");
                        if (isOwnerProp != null)
                        {
                            var method = isOwnerProp.GetGetMethod();
                            if (method != null)
                            {
                                var res = method.Invoke(mb, null);
                                if (res != null)
                                {
                                    var unboxed = res.Unbox<bool>();
                                    if (unboxed) return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static GameObject GetLocalPlayerGO()
        {
            try
            {
                var allPlayers = GetAllPlayers();

                // 100% Safe Criteria: The network owner
                foreach (var p in allPlayers)
                {
                    if (IsLocalPlayer(p)) return p;
                }

                // Fallback for lobby: Distance to Camera.main
                if (Camera.main != null && allPlayers.Count > 0)
                {
                    GameObject best = null;
                    float bestDist = float.MaxValue;
                    var camPos = Camera.main.transform.position;
                    
                    foreach (var p in allPlayers)
                    {
                        if (p == null) continue;
                        float d = Vector3.Distance(p.transform.position, camPos);
                        if (d < bestDist) { bestDist = d; best = p; }
                    }
                    
                    if (best != null && bestDist <= 10f) return best;
                }
            }
            catch { }

            return null;
        }

        private static void DumpTypeMembers(Il2CppSystem.Type il2CppType)
        {
            try
            {
                var bf = Il2CppSystem.Reflection.BindingFlags.Public
                       | Il2CppSystem.Reflection.BindingFlags.NonPublic
                       | Il2CppSystem.Reflection.BindingFlags.Instance
                       | Il2CppSystem.Reflection.BindingFlags.Static;

                var props = il2CppType.GetProperties(bf);
                if (props != null)
                {
                    Plugin.Log.LogDebug($"[PlayerIdentity] --- Properties ({props.Length}) ---");
                    foreach (var propItem in props)
                    {
                        try
                        {
                            string propTypeName = "";
                            try { propTypeName = propItem.PropertyType?.Name ?? "?"; } catch { propTypeName = "?"; }
                            Plugin.Log.LogDebug($"[PlayerIdentity]   Property: {propItem.Name} : {propTypeName}");
                        }
                        catch { }
                    }
                }
                var fields = il2CppType.GetFields(bf);
                if (fields != null)
                {
                    Plugin.Log.LogDebug($"[PlayerIdentity] --- Fields ({fields.Length}) ---");
                    foreach (var fieldItem in fields)
                    {
                        try
                        {
                            string fieldTypeName = "";
                            try { fieldTypeName = fieldItem.FieldType?.Name ?? "?"; } catch { fieldTypeName = "?"; }
                            Plugin.Log.LogDebug($"[PlayerIdentity]   Field: {fieldItem.Name} : {fieldTypeName}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PlayerIdentity] DumpTypeMembers error: {ex.Message}");
            }
        }
    }
}
