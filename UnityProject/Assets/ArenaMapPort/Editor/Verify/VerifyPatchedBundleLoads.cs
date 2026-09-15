// ArenaMapPort — task label `identity-fix`, item 5: does a bundle tools/bundle/patch_monoscript_
// identity.py has re-saved (UnityPy read -> mutate 5 MonoScript.m_AssemblyName fields -> write
// back with BundleFile.save(packer="original")) still load with UNITY'S OWN native AssetBundle
// reader, not just UnityPy's? UnityPy re-serializes the ENTIRE bundle container from scratch
// (header, block-info directory, every nested SerializedFile) -- a genuine, if low, risk that some
// header/flag/block-layout detail a real Unity player needs (but UnityPy doesn't itself depend on
// when reading it back) could be subtly wrong. This is the actual, decisive check for that risk:
// AssetBundle.LoadFromFile is Unity's C++ engine code, completely independent of UnityPy/Python.
//
// -executeMethod ArenaMapPort.Editor.Verify.VerifyPatchedBundleLoads.Run -scenesBundle <path>
// (falls back to ArenaPaths.DefaultOutputDirS1 + "/arena_equator_scenes.bundle" if omitted).
// Safe to run with -quit (no scene authoring, no AddComponent, no EditorApplication.update
// yielding needed -- a synchronous AssetBundle.LoadFromFile + GetAllScenePaths()/
// GetAllAssetNames() + Unload() round trip only).
using System;
using System.IO;
using System.Linq;
using ArenaMapPort.Editor.Common;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Verify
{
    public static class VerifyPatchedBundleLoads
    {
        public static void Run()
        {
            // Multimap generalization (docs/progress/multimap-generalize.md): default derived from
            // the loaded map descriptor instead of the literal "arena_equator_scenes.bundle" — equals
            // that exact string when -mapDescriptor is omitted.
            string scenesBundlePath = ArenaPaths.GetArg(
                "-scenesBundle", ArenaPaths.DefaultOutputDirS1 + "/" + ArenaPaths.Map.ScenesBundleFileName);

            try
            {
                if (!File.Exists(scenesBundlePath))
                    throw new FileNotFoundException("bundle not found: " + scenesBundlePath);

                Debug.Log("[ArenaMapPort][VerifyPatchedBundleLoads] AssetBundle.LoadFromFile(" + scenesBundlePath + ") ...");
                AssetBundle bundle = AssetBundle.LoadFromFile(scenesBundlePath);
                if (bundle == null)
                    throw new IOException("AssetBundle.LoadFromFile returned null -- Unity's native reader "
                                           + "rejected this bundle (this is exactly the failure mode this "
                                           + "check exists to catch: a UnityPy re-save that Unity itself "
                                           + "cannot load, even though UnityPy can read it back fine)");

                string[] scenePaths = bundle.GetAllScenePaths();
                string[] allAssetNames = bundle.GetAllAssetNames();
                Debug.Log("[ArenaMapPort][VerifyPatchedBundleLoads] GetAllScenePaths() -> " + scenePaths.Length
                          + " : " + string.Join(", ", scenePaths));
                Debug.Log("[ArenaMapPort][VerifyPatchedBundleLoads] GetAllAssetNames() -> " + allAssetNames.Length
                          + " : " + string.Join(", ", allAssetNames));

                // The Scripts scene is the one carrying all 5 SS D EFT MonoScript-bearing
                // MonoBehaviours (LevelSettings/LocationScene/BotZone/LocationExportInfo/
                // SpawnPointMarker) -- this is the "MonoScript-bearing scene" item 5 asks to
                // confirm Unity's own AssetBundle reader can enumerate the asset name for, post-
                // patch. (Enumerating the bundle's own asset-name table, not entering Play Mode to
                // instantiate the scene, is the safe/synchronous check available from a plain
                // batchmode -executeMethod pass -- SceneManager.LoadScene for a bundled scene is a
                // runtime/Play-Mode API, not available to a -batchmode Editor script; the
                // structural bundle-format risk item 5 is actually about -- "did UnityPy corrupt
                // something a Unity player needs" -- is exactly what a successful
                // AssetBundle.LoadFromFile + asset-name enumeration proves either way.)
                bool foundScriptsScene = scenePaths.Any(p =>
                    p.IndexOf(ArenaPaths.Map.scripts_scene_name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!foundScriptsScene)
                    throw new Exception("'" + ArenaPaths.Map.scripts_scene_name + "' not found among "
                                         + scenePaths.Length + " scene path(s) reported by Unity's own "
                                         + "AssetBundle.GetAllScenePaths() -- expected it to be present "
                                         + "and readable post-patch");

                bundle.Unload(false);
                Debug.Log("[ArenaMapPort][VerifyPatchedBundleLoads] OK -- Unity's native AssetBundle reader "
                          + "loaded the patched bundle, enumerated " + scenePaths.Length + " scene path(s) + "
                          + allAssetNames.Length + " asset name(s), and found '" + ArenaPaths.Map.scripts_scene_name
                          + "' among them.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaMapPort][VerifyPatchedBundleLoads] FAILED: " + ex);
                EditorApplication.Exit(1);
            }
        }
    }
}
