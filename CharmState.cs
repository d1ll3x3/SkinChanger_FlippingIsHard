using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

namespace CharmReplacer;
// ═══════════════════════════════════════════════════════════════
// Mod global state
// ═══════════════════════════════════════════════════════════════
internal static class CharmState
{
    public static Mesh CustomMesh;
    public static Il2CppReferenceArray<Material> CapturedGameMaterials;
    public static Il2CppReferenceArray<Material> BundleMaterials;

    // Prefab-based charm replacement (preferred when bundle includes MagicaCloth2)
    public static GameObject CustomPrefab;
    public static System.Collections.Generic.Dictionary<string, GameObject> PlayerCharmPrefabs = new System.Collections.Generic.Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);

    // Multiplayer charm meshes + materials (key = player nickname)
    public static System.Collections.Generic.Dictionary<string, Mesh> PlayerCharmMeshes = new System.Collections.Generic.Dictionary<string, Mesh>(System.StringComparer.OrdinalIgnoreCase);
    public static System.Collections.Generic.Dictionary<string, Il2CppReferenceArray<Material>> PlayerCharmMaterials = new System.Collections.Generic.Dictionary<string, Il2CppReferenceArray<Material>>(System.StringComparer.OrdinalIgnoreCase);
    // Track which MeshFilters have already been charmed (per player)
    public static HashSet<int> CharmedMeshFilterIds = new HashSet<int>();

    // Custom skin variables
        public static Texture2D CustomSkinTexture;
    public static Texture2D CustomSkinEmissionTexture;
    // Captured at runtime from the game's M_Phone_Basic_Default material so the
    // old-version (0.9.15) branch — where the local active material has no name
    // and only its _MainTex distinguishes cosmetics — can tell whether the player
    // is currently on the default cosmetic.
    public static Texture DefaultPhoneTexture;
    public static bool DefaultPhoneTextureLogged = false;
    public static System.Collections.Generic.Dictionary<string, Texture2D> PlayerBaseTextures = new System.Collections.Generic.Dictionary<string, Texture2D>(System.StringComparer.OrdinalIgnoreCase);
    public static System.Collections.Generic.Dictionary<string, Texture2D> PlayerEmissionTextures = new System.Collections.Generic.Dictionary<string, Texture2D>(System.StringComparer.OrdinalIgnoreCase);
    public static bool SkinLoaded = false;
    // Player skin tracking (phone + body renderers)
    public static HashSet<int> SkinnedRendererIds = new HashSet<int>();
    public static HashSet<int> MenuPreviewRendererIds = new HashSet<int>();
    public static HashSet<int> MenuPreviewRootIds = new HashSet<int>();
    public static bool SkinDebugLogged = false;
    public static bool SkinEverPainted = false;
    // Charm retry tracking
    public static bool LocalCharmApplied = false;
}

