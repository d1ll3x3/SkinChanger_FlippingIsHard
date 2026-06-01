using System.IO;
using UnityEditor;
using UnityEngine;

public class BuildBundle
{
    [MenuItem("Tools/Build Charm Bundle")]
    static void Build()
    {
        // 1. Verify that there is something in the "hatbundle"
        string[] assets = AssetDatabase.GetAssetPathsFromAssetBundle("hatbundle");
        if (assets.Length == 0)
        {
            EditorUtility.DisplayDialog("Error",
                "No assets assigned to the 'hatbundle'.\n\n" +
                "Select your prefab in the Project panel and assign 'hatbundle' in the AssetBundle field (bottom of the Inspector).",
                "OK");
            return;
        }

        Debug.Log("[BuildBundle] Assets in hatbundle:");
        foreach (var a in assets)
            Debug.Log("  - " + a);

        // 2. Temporary build folder
        string buildDir = Path.Combine(Application.dataPath, "../BundleBuild");
        if (Directory.Exists(buildDir))
            Directory.Delete(buildDir, true);
        Directory.CreateDirectory(buildDir);

        // 3. Build the bundle
        AssetBundleBuild[] builds = new AssetBundleBuild[]
        {
            new AssetBundleBuild
            {
                assetBundleName = "hatbundle",  // exact name the mod looks for
                assetNames = assets
            }
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            buildDir,
            builds,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64
        );

        if (manifest == null)
        {
            EditorUtility.DisplayDialog("Error", "BuildAssetBundles returned null. Check the Console.", "OK");
            return;
        }

        // 4. Copy only the hatbundle to StreamingAssets
        string bundleSrc = Path.Combine(buildDir, "hatbundle");
        string streamingDir = Application.streamingAssetsPath;
        if (!Directory.Exists(streamingDir))
            Directory.CreateDirectory(streamingDir);

        string bundleDst = Path.Combine(streamingDir, "hatbundle");
        File.Copy(bundleSrc, bundleDst, overwrite: true);

        AssetDatabase.Refresh();

        Debug.Log("[BuildBundle] ✓ hatbundle copied to: " + bundleDst);
        EditorUtility.DisplayDialog("Done",
            "hatbundle successfully created at:\n" + bundleDst +
            "\n\nCopy it to BepInEx/plugins/charmreplacer/hatbundle",
            "OK");
    }
}