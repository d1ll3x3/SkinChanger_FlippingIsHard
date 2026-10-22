using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using Steamworks;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Wears the game's red cap (AccessoryHead) backwards for one account. Undocumented on purpose.
    /// The game syncs which head accessory a player wears, not how its instance is posed, so the flip
    /// is decided locally on every client: everyone running the mod sees it, players without the mod
    /// see the cap the way the game placed it.
    /// PlayerNetworked carries no SteamID64 in this build, so the account is only identified on its own
    /// machine (Steamworks) and that client tells the others by riding the flag SkinOverride already
    /// publishes in SkinSync[SkinCategory.None] — no nickname matching, which a rename would break.
    /// </summary>
    internal static class CapFlip
    {
        // The red cap is Groggy's hat; 'ahd_cap' is the black one, which keeps its pose.
        private const string CapSkinKey = "ahd_groggy";
        private const ulong OwnerSteamId = 76561198103496657UL;
        private const string FlagToken = "cap=1";

        private static bool _ownerResolved;
        private static bool _isOwner;

        // Transforms already turned around. The handler builds a new instance whenever the skin is
        // reapplied, so a fresh instance gets a fresh id and is flipped again.
        private static readonly HashSet<int> _flipped = new HashSet<int>();
        // One line per head accessory key seen, so the log names what each hat is called.
        private static readonly HashSet<string> _loggedKeys = new HashSet<string>(StringComparer.Ordinal);

        public static void ClearSceneState() => _flipped.Clear();

        /// <summary>Appended to the flag SkinOverride publishes, so other clients flip this player's cap.</summary>
        public static string LocalFlagToken() => IsOwnerAccount() ? ";" + FlagToken : "";

        /// <summary>A player in a match: the owner decides for itself, everyone else reads the flag.</summary>
        public static void Evaluate(PlayerSkins skins, PlayerNetworked networked)
        {
            try
            {
                var handler = SkinOverride.GetHandler(skins.skinsHandlerMap, SkinCategory.AccessoryHead);
                bool flip = networked.IsOwner ? IsOwnerAccount() : HasRemoteFlag(networked);
                FlipIfCap(handler, flip);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Skin] Cap flip error: {ex.Message}");
            }
        }

        /// <summary>A visual that only mirrors the local player: the preview and the main-menu phone.</summary>
        public static void EvaluateLocal(SkinPrefabHandler handler)
        {
            try { FlipIfCap(handler, IsOwnerAccount()); }
            catch (Exception ex) { Plugin.Log.LogDebug($"[Skin] Cap flip error: {ex.Message}"); }
        }

        private static void FlipIfCap(SkinPrefabHandler handler, bool flip)
        {
            if (handler == null || handler.IsLoading) return;

            string key = handler.CurrentSkinKey;
            if (!string.IsNullOrEmpty(key) && _loggedKeys.Add(key))
                Plugin.Log.LogInfo($"[Skin] Head accessory '{key}'.");

            if (!flip || !string.Equals(key, CapSkinKey, StringComparison.Ordinal)) return;

            SkinInstanceRef instance;
            if (!handler.TryGetInstance(out instance) || instance == null) return;

            var cap = instance.transform;
            if (_flipped.Contains(cap.GetInstanceID())) return;

            // Neither the socket's up nor the phone body's up is the phone's long axis on every visual
            // (one put the cap under the phone, the other tipped it over). The instance origin sits at the
            // phone's centre with the cap on top, so the instance's local axis that points at the cap is
            // the phone's vertical; spinning around it through the origin keeps the crown in place and
            // only sends the visor to the back.
            var renderers = cap.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Count == 0) return;
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++) bounds.Encapsulate(renderers[i].bounds);
            var toCap = cap.InverseTransformPoint(bounds.center);
            if (toCap.sqrMagnitude < 1e-8f) return;
            float ax = Mathf.Abs(toCap.x), ay = Mathf.Abs(toCap.y), az = Mathf.Abs(toCap.z);
            var localAxis = ay >= ax && ay >= az ? Vector3.up : ax >= az ? Vector3.right : Vector3.forward;
            _flipped.Add(cap.GetInstanceID());
            cap.RotateAround(cap.position, cap.TransformDirection(localAxis), 180f);
            Plugin.Log.LogDebug("[Skin] Cap turned around.");
        }

        private static bool HasRemoteFlag(PlayerNetworked networked)
        {
            string flag = SkinOverride.ReadSync(networked, SkinCategory.None);
            if (string.IsNullOrEmpty(flag)) return false;
            return Array.IndexOf(flag.Split(';'), FlagToken) >= 0;
        }

        private static bool IsOwnerAccount()
        {
            if (_ownerResolved) return _isOwner;
            try
            {
                ulong id = SteamUser.GetSteamID().m_SteamID;
                if (id == 0UL) return false; // Steam not up yet — ask again next tick
                _isOwner = id == OwnerSteamId;
                _ownerResolved = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Skin] Steam account unavailable: {ex.Message}");
            }
            return _isOwner;
        }
    }
}
