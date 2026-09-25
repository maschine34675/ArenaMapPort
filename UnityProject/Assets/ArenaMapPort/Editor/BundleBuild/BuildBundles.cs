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
//
// s1-finish fix — Scripts-scene authoring now yields to a real EditorApplication.update tick:
// `AddComponent<T>()` for a §D EFT type (living in the precompiled GameRefs/Assembly-CSharp.dll
// plugin) reproducibly returned null ("Script X has not finished compilation yet") for the WHOLE
// remaining lifetime of a synchronous -executeMethod call — confirmed NOT a caching issue (same
// result against a from-scratch Library) and NOT fixable by waiting longer on the SAME thread
// (80 retries / 20s of Thread.Sleep inside the synchronous call, 100% failure), because sleeping
// the main thread never lets Unity's own EditorApplication.update loop tick — and ticking that
// loop is exactly what finishes whatever deferred linking step a precompiled-plugin MonoScript
// needs here. Fix: everything up to and including per-content-scene assembly still runs
// synchronously (unaffected — confirmed working every run); Scripts-scene authoring onward runs
// from an EditorApplication.update callback instead (retried across real ticks, not real time),
// and this method now owns calling EditorApplication.Exit() explicitly at every exit path — the
// Unity.exe command line must NOT pass -quit for this to work (see docs/sync-game-refs and
// docs/progress/s1-finish.md for the full diagnosis and the exact command line used).
using System;
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
        public const long BuildUnixDateTime = 1789257600L; // 2026-09-13T00:00:00Z, matches make_base_json.py

        private sealed class PendingContext
        {
            public ArenaPackage pkg;
            public bool bakeNavMesh;
            public bool verify;
            public string packageDir;
            public string outputDir;
            public List<string> scenePaths;
            public List<GameObject> contentSceneRoots;
            public bool scriptsSceneIsFirst;
        }

        // s1-finish found (docs/progress/s1-finish.md "Second, separate blocker") that yielding to
        // real EditorApplication.update ticks alone did NOT resolve the "Script X has not finished
        // compilation yet" AddComponent failure (0/2000 ticks ever succeeded there) — the tick-based
        // retry was kept anyway as a cheap, harmless best-effort mechanism.
        //
        // s1-fix-r1 (docs/progress/s1-fix-r1.md) found and fixed the REAL root cause: "Assembly-
        // CSharp"/"Assembly-CSharp-firstpass" are Unity's own RESERVED names for its implicit
        // project-compiled script assemblies — a precompiled Plugin DLL under those literal names is
        // permanently stuck "not finished compilation" for AddComponent, independent of ticks/time/
        // caching. tools/gamerefs-fixup now renames the GameRefs copies (file + embedded assembly
        // identity) away from those names — confirmed fixed: AddComponent<LevelSettings>() (and
        // every other SS D type) now succeeds on the very first attempt, tick 1, every real run
        // since. This tick-based retry loop is kept exactly as-is (harmless, now effectively a
        // single-iteration fast path) rather than simplified back to a synchronous call, to avoid
        // touching more than necessary of an already-verified-working mechanism.
        private const int MaxScriptsSceneTicks = 50;

        private static PendingContext _pending;
        private static int _scriptsSceneTicks;

        // s1-finish DIAGNOSTIC ONLY (not part of the real S1/S2 pipeline, not invoked by Run()):
        // isolates whether AddComponent<LevelSettings>() ever succeeds in THIS Editor/GameRefs
        // setup independent of everything else Run() does first (85+ texture/material/mesh
        // imports, 2 content-scene assemblies, dozens of AssetDatabase.Refresh() calls) — tests
        // the theory that this project's OWN heavy asset-import activity earlier in the same
        // session is what keeps re-triggering whatever "not finished compilation" state Unity
        // reports, rather than a one-time startup cost. Logs one line and exits; nothing is
        // written under work/build.
        public static void TestAddComponentOnly()
        {
            try
            {
                var go1 = new GameObject("DiagnosticTest_GameObjectAPI");
                var comp1 = go1.AddComponent<LevelSettings>();
                Debug.Log("[ArenaMapPort][DIAGNOSTIC] GameObject.AddComponent<LevelSettings>: " +
                          (comp1 != null ? "SUCCEEDED" : "returned NULL"));

                var go2 = new GameObject("DiagnosticTest_ObjectFactoryAPI");
                Component comp2 = null;
                string via2 = "ObjectFactory.AddComponent";
                try
                {
                    comp2 = UnityEditor.ObjectFactory.AddComponent(go2, typeof(LevelSettings));
                }
                catch (Exception exObjFactory)
                {
                    Debug.Log("[ArenaMapPort][DIAGNOSTIC] " + via2 + " threw: " + exObjFactory.GetType().Name + ": " + exObjFactory.Message);
                }
                Debug.Log("[ArenaMapPort][DIAGNOSTIC] " + via2 + "<LevelSettings>: " +
                          (comp2 != null ? "SUCCEEDED" : "returned NULL/threw"));

                // Third path: resolve the System.Type via reflection from the loaded assembly by
                // name (instead of a compile-time `typeof(LevelSettings)` reference) in case the
                // compile-time reference itself is somehow bound to a stale/duplicate assembly
                // identity.
                var go3 = new GameObject("DiagnosticTest_ReflectionType");
                Component comp3 = null;
                Type reflType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // s1-fix-r1: the GameRefs copy's own assembly identity is now
                    // "SptGameRefs-AssemblyCSharp" (renamed away from the reserved "Assembly-CSharp"
                    // name that was this diagnostic's whole reason for existing — see
                    // tools/gamerefs-fixup/Program.cs's header comment / docs/progress/s1-fix-r1.md).
                    if (asm.GetName().Name != "SptGameRefs-AssemblyCSharp") continue;
                    reflType = asm.GetType("LevelSettings", false);
                    Debug.Log("[ArenaMapPort][DIAGNOSTIC] found loaded assembly 'Assembly-CSharp' (" + asm.FullName + "), GetType(\"LevelSettings\") = " + (reflType != null ? reflType.AssemblyQualifiedName : "null"));
                }
                if (reflType != null)
                    comp3 = go3.AddComponent(reflType);
                Debug.Log("[ArenaMapPort][DIAGNOSTIC] go.AddComponent(reflectedType): " +
                          (comp3 != null ? "SUCCEEDED" : "returned NULL/no type found"));

                bool anySucceeded = comp1 != null || comp2 != null || comp3 != null;
                EditorApplication.Exit(anySucceeded ? 0 : 1);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaMapPort][DIAGNOSTIC] threw: " + ex);
                EditorApplication.Exit(1);
            }
        }

        public static void Run()
        {
            // s1-finish fix (file header): the Unity.exe invocation for this method must NOT pass
            // -quit (Scripts-scene authoring needs real EditorApplication.update ticks, which only
            // happen if the process stays alive) — which means THIS method is now solely
            // responsible for the process ever terminating. Wrap the whole synchronous prefix so a
            // failure here (before the deferred stage even starts) still calls
            // EditorApplication.Exit(1) instead of leaving a hung batchmode process behind.
            try
            {
                RunSynchronousPrefix();
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaMapPort][BuildBundles] pipeline failed before Scripts-scene authoring: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void RunSynchronousPrefix()
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
            if (VerifySpawnPlacement.Required(pkg) && (!bakeNavMesh || skipScriptsScene))
                throw new IOException("Generic full-map builds require fresh NavMesh baking and a Scripts scene for verified player starts.");

            // §B.WP2's execution-order note: textures MUST import before materials
            // (Material.SetTexture against a not-yet-imported texture is a silent no-op).
            // Dictionary value type is the base Texture (impl-texture-dimensions §2) since a
            // Cube-dimension entry resolves to a Cubemap asset, not a Texture2D one.
            Dictionary<string, Texture> textures = TextureImportDriver.ImportAll(pkg);

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

            var ctx = new PendingContext
            {
                pkg = pkg,
                bakeNavMesh = bakeNavMesh,
                verify = verify,
                packageDir = packageDir,
                outputDir = outputDir,
                scenePaths = scenePaths,
                contentSceneRoots = contentSceneRoots,
                scriptsSceneIsFirst = !firstSceneCreated,
            };

            // §B.WP2.6 — Scripts scene, fresh-authored, added last (its own scene, per §D "all five
            // live in the single fresh-authored Arena_Equator_Scripts scene").
            if (skipScriptsScene)
            {
                Debug.LogWarning("[ArenaMapPort][BuildBundles] skipScriptsScene=true - Scripts scene NOT authored, §D is NOT satisfied by this run (diagnostic only).");
                FinishPipeline(ctx);
                return;
            }

            // s1-finish fix (see file header): don't call AuthorScriptsScene.Build synchronously
            // here — retry it from a real EditorApplication.update tick instead, since that's the
            // only thing that has ever let a GameRefs EFT MonoScript become AddComponent-able in
            // testing. The Unity.exe invocation for this method must omit -quit; this method now
            // owns the process's exit via EditorApplication.Exit() on every path (see
            // FinishPipeline/ScriptsSceneTick).
            _pending = ctx;
            _scriptsSceneTicks = 0;
            Debug.Log("[ArenaMapPort][BuildBundles] yielding to EditorApplication.update to author the Scripts scene (s1-finish fix)...");
            EditorApplication.update += ScriptsSceneTick;
        }

        private static void ScriptsSceneTick()
        {
            _scriptsSceneTicks++;
            try
            {
                Scene scriptsScene = AuthorScriptsScene.Build(_pending.pkg, BuildUnixDateTime, _pending.scriptsSceneIsFirst);
                string scriptsScenePath = ArenaPaths.ScenesDir + "/" + ArenaPaths.Map.scripts_scene_name + ".unity";
                bool scriptsOk = EditorSceneManager.SaveScene(scriptsScene, scriptsScenePath);
                if (!scriptsOk) throw new IOException("[ArenaMapPort][BuildBundles] failed to save Scripts scene -> " + scriptsScenePath);
                _pending.scenePaths.Add(scriptsScenePath);

                EditorApplication.update -= ScriptsSceneTick;
                Debug.Log("[ArenaMapPort][BuildBundles] Scripts scene authored successfully after " + _scriptsSceneTicks + " EditorApplication.update tick(s).");
                FinishPipeline(_pending);
            }
            catch (Exception ex)
            {
                if (_scriptsSceneTicks == 1)
                    Debug.LogWarning("[ArenaMapPort][BuildBundles] Scripts-scene authoring not ready on tick 1 (" + ex.GetType().Name + ": " + ex.Message + ") - retrying on subsequent EditorApplication.update ticks (this is the s1-finish fix in action, not a failure by itself)...");

                // s1-finish fix (round 2): a failed attempt that got past
                // EditorSceneManager.NewScene(..., Additive) but threw before
                // EditorSceneManager.SaveScene() leaves a dangling UNTITLED (unsaved, empty
                // .path) scene open as the active scene — confirmed this pass: attempt 1 failed
                // on AddComponent<LevelSettings> (the real, expected, now-fixed-by-yielding
                // issue), attempt 2 then failed on an UNRELATED "Cannot create a new scene
                // additively with an untitled scene unsaved" because attempt 1's own dangling
                // scene was still open — masking that attempt 2 would otherwise have succeeded.
                // Only applies to Additive mode (Single mode's own NewScene call on the next
                // retry discards whatever is open unconditionally, so it self-heals) — Additive
                // mode is what every real S1/S2 run uses, since content scenes are always
                // authored before the Scripts scene (§B.WP2.6).
                if (!_pending.scriptsSceneIsFirst)
                {
                    Scene maybeDangling = EditorSceneManager.GetActiveScene();
                    if (maybeDangling.IsValid() && string.IsNullOrEmpty(maybeDangling.path) && EditorSceneManager.sceneCount > 1)
                        EditorSceneManager.CloseScene(maybeDangling, true);
                }

                if (_scriptsSceneTicks >= MaxScriptsSceneTicks)
                {
                    EditorApplication.update -= ScriptsSceneTick;
                    Debug.LogError("[ArenaMapPort][BuildBundles] Scripts-scene authoring never succeeded after " + _scriptsSceneTicks + " EditorApplication.update ticks. Last error: " + ex);
                    EditorApplication.Exit(1);
                }
                // else: leave the delegate subscribed, try again on the next tick.
            }
        }

        private static void FinishPipeline(PendingContext ctx)
        {
            try
            {
                string navMeshAssetPath = null;
                UnityEngine.AI.NavMeshData placementNavMesh = null;
                if (ctx.bakeNavMesh)
                {
                    UnityEngine.AI.NavMeshData data = ArenaMapPort.Editor.NavMesh.BakeNavMesh.Bake(ctx.contentSceneRoots.ToArray());
                    placementNavMesh = data;
                    // Multimap generalization (docs/progress/multimap-generalize.md): was the literal
                    // "arena_equator.asset" — the asset name itself is not load-bearing (§B.WP2.7), but
                    // keying it off the map avoids two maps' intermediate .asset files colliding if
                    // Assets/ArenaImport/ is ever shared across a single Editor session building both.
                    navMeshAssetPath = ArenaPaths.NavMeshAssetDir + "/" + ArenaPaths.Map.key + ".asset";
                    ArenaMapPort.Editor.NavMesh.BakeNavMesh.SaveAsset(data, navMeshAssetPath);
                }

                if (VerifySpawnPlacement.Required(ctx.pkg))
                    VerifySpawnPlacement.Place(ctx.pkg, placementNavMesh, ctx.contentSceneRoots.ToArray(), ctx.outputDir);

                // PLAN-TWO-MAPS item 4: the NavMeshModifier/NavMeshModifierVolume components exist only
                // for the bake above. Strip them (and re-save the content scenes) BEFORE the bundle build:
                // a 'Unity.AI.Navigation' MonoScript in the scenes bundle fails patch_monoscript_identity.py
                // (only Assembly-CSharp[-firstpass] identities may ship). Runs whether or not this build
                // baked, since the scene files always carry them after SceneAssembler.
                NavMeshModifierImporter.StripBakeOnlyComponents(ctx.contentSceneRoots);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // Bundles land directly under outputDir (e.g. work/build/s1/arena_equator_scenes.bundle),
                // NOT a nested "bundles/" subfolder — matches tools/validate/validate_preset.py's
                // `s1_dir.glob("*scenes*.bundle")` auto-probe and WP4's own file-name expectations
                // (docs/progress/wp3.md/wp4.md, cross-checked this pass). WP4's own deploy step is what
                // copies the built file into dist/BepInEx/plugins/ArenaMapPort/bundles/ — a different,
                // later "bundles" folder under dist/, not here.
                string bundleOutputDir = ctx.outputDir.Replace('\\', '/');
                Directory.CreateDirectory(bundleOutputDir);

                // Multimap generalization (docs/progress/multimap-generalize.md): output bundle
                // filenames were the literals "arena_equator_scenes.bundle"/"arena_equator_navmesh.bundle"
                // — even with -packageDir pointed at a different map's extraction, the produced bundle
                // used to still be internally named for Equator, a hard collision once staged next to
                // Equator's own file in the shared dist/.../bundles/ folder. Now read from the loaded map
                // descriptor, defaulting to those exact same literals when -mapDescriptor is omitted.
                var builds = new List<AssetBundleBuild>
                {
                    new AssetBundleBuild
                    {
                        assetBundleName = ArenaPaths.Map.ScenesBundleFileName,
                        assetNames = ctx.scenePaths.ToArray(),
                    }
                };
                if (ctx.bakeNavMesh && navMeshAssetPath != null)
                {
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = ArenaPaths.Map.NavMeshBundleFileName,
                        assetNames = new[] { navMeshAssetPath },
                    });
                }

                // s1-finish task item 3 (O3 gap): PlayerSettings.stripUnusedMeshComponents defaults
                // to true, which strips a Mesh's normals/tangents/colors/etc. at BUILD time when
                // nothing in the built player's OWN shaders/materials samples that channel — a
                // per-project Editor setting read at AssetBundle build time, not something
                // MeshImporter.cs can compensate for per-mesh. Our stand-in shaders (§B.WP2.3/O1)
                // are placeholders with different (or no) property usage than the real BSG shaders
                // WP4's runtime rebind swaps back in — so Unity's stripping heuristic, run against
                // the STAND-IN shader, can silently drop a channel (e.g. tangents for a
                // normal-mapped material) that the REAL BSG shader needs once rebound in-game, with
                // no error anywhere in this pipeline to catch it. Must be set before
                // BuildAssetBundles, which is where stripping actually happens (Mesh data is
                // already finalized in the imported .asset files by this point; this setting
                // controls what BuildAssetBundles re-serializes into the bundle, not what
                // MeshImporter wrote to Assets/ArenaImport/Meshes/*.asset).
                bool prevStripUnusedMeshComponents = PlayerSettings.stripUnusedMeshComponents;
                PlayerSettings.stripUnusedMeshComponents = false;
                Debug.Log("[ArenaMapPort][BuildBundles] PlayerSettings.stripUnusedMeshComponents: " +
                          prevStripUnusedMeshComponents + " -> false (forced off for the bundle build, s1-finish item 3)");

                // §B.WP2.8 / §H risk #12: BuildTarget passed explicitly, never
                // EditorUserBuildSettings.activeBuildTarget.
                // New generic maps use LZ4 chunks: identity patching can preserve
                // compression without repeatedly expanding/recompressing a huge LZMA blob.
                // Existing Equator/Bay5 entrypoints retain their previous build option.
                BuildAssetBundleOptions bundleOptions = VerifySpawnPlacement.Required(ctx.pkg)
                    ? BuildAssetBundleOptions.ChunkBasedCompression : BuildAssetBundleOptions.None;
                AssetBundleManifest bundleManifest = UnityEditor.BuildPipeline.BuildAssetBundles(
                    bundleOutputDir, builds.ToArray(), bundleOptions, BuildTarget.StandaloneWindows64);

                if (bundleManifest == null)
                    throw new IOException("[ArenaMapPort][BuildBundles] BuildAssetBundles returned null (build failed) - see the Editor log above for the specific asset/shader error.");

                Debug.Log("[ArenaMapPort][BuildBundles] built " + builds.Count + " bundle(s) -> " + bundleOutputDir +
                          " : " + string.Join(", ", bundleManifest.GetAllAssetBundles()));

                if (ctx.verify)
                    VerifyBuild.Run(ctx.scenePaths, ctx.outputDir);

                Debug.Log("[ArenaMapPort][BuildBundles] DONE packageDir=" + ctx.packageDir + " outputDir=" + ctx.outputDir);
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaMapPort][BuildBundles] pipeline failed in FinishPipeline: " + ex);
                EditorApplication.Exit(1);
            }
        }
    }
}
