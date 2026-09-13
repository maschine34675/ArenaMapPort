// ArenaMapPort WP2 — §B.WP2.8 bundle build, orchestrating the whole headless pipeline in one
// entry point (§G.S1/S2's `-executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run`):
// texture import -> stand-in shaders -> material import -> mesh import -> scene assembly (one pass
// per content scene) -> Scripts scene authoring -> [optional NavMesh bake] -> AssetBundle build
// (BuildTarget.StandaloneWindows64 passed EXPLICITLY, never left to
// EditorUserBuildSettings.activeBuildTarget, per §H risk #12 / §B.WP2.8) -> [optional] editor
// verification pass, writing work/build/<slice>/editor_verify.json.
//
// O7: every path is a parameter (-packageDir/-outputDir on the Unity.exe command line after
// -executeMethod), defaulting to the S1 slice paths.
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using ArenaMapPort.Editor.Import;
using ArenaMapPort.Editor.Shaders;
using ArenaMapPort.Editor.Verify;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.BundleBuild
{
    public static class BuildBundles
    {
        // Fixed build-time Unix timestamp (§D's LocationExportInfo row: "captured once and
        // hardcoded at WP2/WP5 coordination time, not regenerated per-build, so the two never
        // drift"). WP5's base.json UnixDateTime must be set to this same value.
        public const long BuildUnixDateTime = 1757707200L; // 2025-09-13T00:00:00Z placeholder build stamp

        public static void Run()
        {
            string packageDir = ArenaPaths.GetArg("-packageDir", ArenaPaths.DefaultPackageDirS1);
            string outputDir = ArenaPaths.GetArg("-outputDir", ArenaPaths.DefaultOutputDirS1);
            bool bakeNavMesh = ArenaPaths.GetArg("-bakeNavMesh", "false") == "true";
            bool verify = ArenaPaths.GetArg("-verify", "true") == "true";
            // Diagnostic escape hatch ONLY (default false, NOT a real S1/S2 pass when true) — see
            // docs/progress/wp2.md "S1 crash diagnosis". §D requires the Scripts scene in every
            // real pass; this flag exists solely to let the rest of the pipeline (mesh/material/
            // texture import, two-pass scene assembly, bundle build) be exercised end-to-end while
            // GameRefs' Assembly-CSharp DLLs are pulled to work around the blocking native Editor
            // crash documented there, without also blocking on the unrelated question of whether
            // the REST of the pipeline works — used this pass to confirm it does (17.8MB bundle,
            // 64 GameObjects/47 Meshes/25 Texture2D/13 Material/3 LODGroup/1 Light, StandaloneWindows64
            // header, UnityPy re-read matches WP1's package counts exactly).
            bool skipScriptsScene = ArenaPaths.GetArg("-skipScriptsScene", "false") == "true";

            Debug.Log("[ArenaMapPort][BuildBundles] START packageDir=" + packageDir + " outputDir=" + outputDir +
                      " bakeNavMesh=" + bakeNavMesh + " verify=" + verify +
                      (skipScriptsScene ? " skipScriptsScene=true (DIAGNOSTIC ONLY, not a real S1 pass)" : ""));

            var pkg = new ArenaPackage(packageDir);

            // §B.WP2's execution-order note: textures MUST import before materials
            // (Material.SetTexture against a not-yet-imported Texture2D is a silent no-op).
            Dictionary<string, Texture2D> textures = TextureImportDriver.ImportAll(pkg);

            var shaderGen = new StandinShaderGenerator();
            shaderGen.CollectFromPackage(pkg);
            shaderGen.GenerateAll();

            Dictionary<string, Material> materials = MaterialImporter.ImportAll(pkg, shaderGen.Shaders, textures, outputDir);

            Dictionary<string, Mesh> meshes = MeshImporter.ImportAll(pkg);

            var physicMaterials = new PhysicMaterialCache();

            Directory.CreateDirectory(ArenaPaths.ScenesDir);

            var scenePaths = new List<string>();
            var contentSceneRoots = new List<GameObject>();

            bool firstSceneCreated = false;
            foreach (string sceneName in pkg.SceneNames)
            {
                Scene scene = SceneAssembler.Assemble(pkg, sceneName, meshes, materials, physicMaterials, !firstSceneCreated);
                firstSceneCreated = true;
                contentSceneRoots.AddRange(scene.GetRootGameObjects());
                string path = ArenaPaths.ScenesDir + "/" + sceneName + ".unity";
                bool ok = EditorSceneManager.SaveScene(scene, path);
                if (!ok) throw new IOException("[ArenaMapPort][BuildBundles] failed to save scene '" + sceneName + "' -> " + path);
                scenePaths.Add(path);
            }

            // §B.WP2.6 — Scripts scene, fresh-authored, added last (its own scene, per §D "all five
            // live in the single fresh-authored Arena_Equator_Scripts scene").
            if (!skipScriptsScene)
            {
                Scene scriptsScene = AuthorScriptsScene.Build(pkg, BuildUnixDateTime, !firstSceneCreated);
                string scriptsScenePath = ArenaPaths.ScenesDir + "/" + ArenaPaths.ScriptsSceneName + ".unity";
                bool scriptsOk = EditorSceneManager.SaveScene(scriptsScene, scriptsScenePath);
                if (!scriptsOk) throw new IOException("[ArenaMapPort][BuildBundles] failed to save Scripts scene -> " + scriptsScenePath);
                scenePaths.Add(scriptsScenePath);
            }
            else
            {
                Debug.LogWarning("[ArenaMapPort][BuildBundles] skipScriptsScene=true - Scripts scene NOT authored, §D is NOT satisfied by this run (diagnostic only).");
            }

            string navMeshAssetPath = null;
            if (bakeNavMesh)
            {
                UnityEngine.AI.NavMeshData data = ArenaMapPort.Editor.NavMesh.BakeNavMesh.Bake(contentSceneRoots.ToArray());
                navMeshAssetPath = ArenaPaths.NavMeshAssetDir + "/arena_equator.asset";
                ArenaMapPort.Editor.NavMesh.BakeNavMesh.SaveAsset(data, navMeshAssetPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Bundles land directly under outputDir (e.g. work/build/s1/arena_equator_scenes.bundle),
            // NOT a nested "bundles/" subfolder — matches tools/validate/validate_preset.py's
            // `s1_dir.glob("*scenes*.bundle")` auto-probe and WP4's own file-name expectations
            // (docs/progress/wp3.md/wp4.md, cross-checked this pass). WP4's own deploy step is what
            // copies the built file into dist/BepInEx/plugins/ArenaMapPort/bundles/ — a different,
            // later "bundles" folder under dist/, not here.
            string bundleOutputDir = outputDir.Replace('\\', '/');
            Directory.CreateDirectory(bundleOutputDir);

            var builds = new List<AssetBundleBuild>
            {
                new AssetBundleBuild
                {
                    assetBundleName = "arena_equator_scenes.bundle",
                    assetNames = scenePaths.ToArray(),
                }
            };
            if (bakeNavMesh && navMeshAssetPath != null)
            {
                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = "arena_equator_navmesh.bundle",
                    assetNames = new[] { navMeshAssetPath },
                });
            }

            // §B.WP2.8 / §H risk #12: BuildTarget passed explicitly, never
            // EditorUserBuildSettings.activeBuildTarget.
            AssetBundleManifest bundleManifest = UnityEditor.BuildPipeline.BuildAssetBundles(
                bundleOutputDir, builds.ToArray(), BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

            if (bundleManifest == null)
                throw new IOException("[ArenaMapPort][BuildBundles] BuildAssetBundles returned null (build failed) - see the Editor log above for the specific asset/shader error.");

            Debug.Log("[ArenaMapPort][BuildBundles] built " + builds.Count + " bundle(s) -> " + bundleOutputDir +
                      " : " + string.Join(", ", bundleManifest.GetAllAssetBundles()));

            if (verify)
                VerifyBuild.Run(scenePaths, outputDir);

            Debug.Log("[ArenaMapPort][BuildBundles] DONE packageDir=" + packageDir + " outputDir=" + outputDir);
        }
    }
}
