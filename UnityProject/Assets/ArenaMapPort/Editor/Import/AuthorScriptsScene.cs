// ArenaMapPort WP2 — §B.WP2.6 / §D: the fresh-authored Arena_Equator_Scripts scene. Not extracted
// (no scenes/Arena_Equator_Scripts.json exists in WP1's package, §E.1) — built here from real
// Assembly-CSharp types via GameRefs (§B.WP2.1), one instance of each of §D's five components.
//
// ExecuteInEditMode hazard (found this pass, not previously documented): LocationScene and
// SpawnPointMarker both carry [ExecuteInEditMode]. Unity invokes Awake()/Start() synchronously the
// moment such a component is attached to a GameObject that is active-in-hierarchy — even outside
// Play mode. LocationScene.Awake() (LocationScene.cs, this pass's read) unconditionally dereferences
// `WorldInteractiveObjects.Length` with no null guard — exactly the NRE site spt-location-contract.md
// §2 documents for the RUNTIME case, but it fires just as surely here at author time if the field is
// still null when Awake runs. §D's own fix (call FillArrays() before anything reads these fields)
// only works if Awake is deferred until after FillArrays() runs — so every object in this scene is
// built under a root GameObject kept INACTIVE (SetActive(false)) for the whole authoring pass; no
// ExecuteInEditMode callback fires while activeInHierarchy is false, regardless of load order. Only
// once every field (LocationScene's arrays via FillArrays(), BotZone's three arrays, each
// SpawnPointMarker's _collider/_spawnPoint) is fully wired does the root get reactivated — at which
// point Awake/Start run exactly once, against fully-populated data, matching what a real scene load
// does.
using System;
using System.Collections.Generic;
using ArenaMapPort.Editor.Common;
using EFT;
using EFT.Game.Spawning;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.Import
{
    public static class AuthorScriptsScene
    {
        // §D: "Author 2-4 markers in practice" — one Sides=All plus dedicated Usec/Bear ones.
        // Two parallel arrays rather than a ValueTuple array/deconstruction (kept deliberately
        // plain — see docs/progress/wp2.md's S1 crash-diagnosis note).
        private static readonly string[] SpawnDefNames = { "SpawnPoint_Player_All", "SpawnPoint_Player_Usec", "SpawnPoint_Player_Bear" };
        private static readonly EPlayerSideMask[] SpawnDefSides = { EPlayerSideMask.All, EPlayerSideMask.Usec, EPlayerSideMask.Bear };

        // s1-finish fix: `AddComponent<T>()` for a MonoBehaviour living in a PRECOMPILED plugin
        // DLL (GameRefs/Assembly-CSharp.dll) can return NULL the first few times it's used in a
        // fresh batchmode process — Unity logs "Script <Name> has not finished compilation yet.
        // Please wait until compilation of the script has finished and try again." and returns
        // null rather than throwing. Empirically NOT a matter of waiting longer on THIS thread
        // (confirmed: 80 retries over 20s of Thread.Sleep, all against the same synchronous call
        // stack, 100% failure — because sleeping the main thread never lets
        // EditorApplication.update actually tick, and ticking the update loop is exactly what
        // finishes whatever deferred linking step Unity needs here). The real fix
        // (BuildBundles.Run) retries this whole method from an EditorApplication.update callback
        // instead, which DOES yield back to Unity's own loop between attempts — see
        // docs/progress/s1-finish.md. This helper just fails fast (throws) on a null result so the
        // outer update-tick retry knows to try again; it does not itself sleep or loop.
        private static T AddComponentOrThrow<T>(GameObject go) where T : Component
        {
            T comp = go.AddComponent<T>();
            if (comp == null)
                throw new InvalidOperationException("[ArenaMapPort][AuthorScriptsScene] AddComponent<" + typeof(T).Name +
                    "> returned null (Unity: script not yet ready in this process).");
            return comp;
        }

        public static Scene Build(ArenaPackage pkg, long unixDateTime, bool isFirstScene = false)
        {
            // See SceneAssembler.Assemble's matching comment — Additive throws on the very first
            // scene of a fresh batchmode session (default Untitled scene is unsaved).
            NewSceneMode mode = isFirstScene ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            EditorSceneManager.SetActiveScene(scene);

            GameObject root = new GameObject("ScriptsRoot");
            // See file header: must go inactive BEFORE any ExecuteInEditMode component is attached
            // anywhere under it.
            root.SetActive(false);

            // --- LevelSettings (§D row 1) ---
            var levelSettingsGo = new GameObject("LevelSettings");
            levelSettingsGo.transform.SetParent(root.transform, false);
            var levelSettings = AddComponentOrThrow<LevelSettings>(levelSettingsGo);
            levelSettings.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
            levelSettings.SkyColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            levelSettings.EquatorColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            levelSettings.GroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            levelSettings.AmbientIntensity = 1f;
            levelSettings.fog = false;
            levelSettings.skybox = null;
            levelSettings.CameraPrefab = null;
            levelSettings.PostProcessProfilePrefab = null;

            // --- LocationExportInfo (§D row 4) ---
            var exportGo = new GameObject("LocationExportInfo");
            exportGo.transform.SetParent(root.transform, false);
            var exportInfo = AddComponentOrThrow<LocationExportInfo>(exportGo);
            exportInfo._unixDateTime = unixDateTime;

            // --- BotZone (§D row 3) ---
            var botZoneGo = new GameObject("BotZone_Main");
            botZoneGo.transform.SetParent(root.transform, false);
            var botZone = AddComponentOrThrow<global::BotZone>(botZoneGo);
            botZone.Id = 0;
            botZone.CanSpawnBoss = false;
            botZone.SnipeZone = false;
            botZone._maxPersons = -1;
            // Hard-NRE fields (§D, §H risk #11) — explicit empty, never left null.
            botZone.PatrolWays = Array.Empty<global::PatrolWay>();
            botZone.SpawnPointMarkers = new List<SpawnPointMarker>();
            botZone.UnSpawnPoints = Array.Empty<global::UnspawnPoint>();

            // --- SpawnPointMarker x N (§D row 5) ---
            Vector3[] positions = ResolveSpawnPositions(pkg, SpawnDefNames.Length);
            var markers = new List<SpawnPointMarker>();
            for (int i = 0; i < SpawnDefNames.Length; i++)
            {
                string name = SpawnDefNames[i];
                EPlayerSideMask sides = SpawnDefSides[i];
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                go.transform.position = positions[i];

                var box = go.AddComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = new Vector3(1f, 2f, 1f);
                box.enabled = true;

                var marker = AddComponentOrThrow<SpawnPointMarker>(go);
                marker._collider = box;
                var spawnPoint = new SpawnPoint();
                spawnPoint.Id = Guid.NewGuid().ToString();
                spawnPoint.Name = name;
                spawnPoint.Position = positions[i];
                spawnPoint.Rotation = Quaternion.identity;
                spawnPoint.Sides = sides;
                spawnPoint.Categories = ESpawnCategoryMask.Player;
                // fix-gamestart, finding F1 (blocker): null here crashes SPT's own EmptyInfilFixPatch at
                // raid-start (BaseLocalGame.cs:790 tolerates null _entryPoint, but SPT's Harmony prefix on
                // the same vmethod_6 does not — see ArenaMapPort.Client/SpawnInfiltrationRepairs.cs for the
                // full chain). The runtime Harmony postfix there is what actually ships this fix without a
                // bundle rebuild; this literal just keeps a future rebuild correct-by-construction. Keep in
                // sync with SpawnInfiltrationFixPostfix.InfiltrationName — must be the identical string.
                spawnPoint.Infiltration = "ArenaEquator";
                spawnPoint.BotZone = null; // §D: no BotZone linkage needed — global spawn-point scan.
                spawnPoint.DelayToCanSpawnSec = 4f;
                spawnPoint.CorePointId = 0;
                marker._spawnPoint = spawnPoint;
                markers.Add(marker);
            }
            botZone.SpawnPointMarkers.AddRange(markers);

            // --- LocationScene (§D row 2) — added last, FillArrays() scans siblings/children ---
            var locationSceneGo = new GameObject("LocationScene");
            locationSceneGo.transform.SetParent(root.transform, false);
            var locationScene = AddComponentOrThrow<global::LocationScene>(locationSceneGo);
            locationScene.FillArrays(); // populates every one of the ~30 array fields, non-null by construction

            // Now, and only now, activate: Awake/OnEnable/Start fire once, against fully-wired data.
            root.SetActive(true);

            Debug.Log("[ArenaMapPort][AuthorScriptsScene] built '" + ArenaPaths.ScriptsSceneName + "': LevelSettings=1, " +
                      "LocationScene=1 (BotZones=" + locationScene.BotZones.Length + ", SpawnPointMarkers=" +
                      locationScene.SpawnPointMarkers.Length + "), BotZone=1 (SpawnPointMarkers=" +
                      botZone.SpawnPointMarkers.Count + "), LocationExportInfo=1, SpawnPointMarker=" + markers.Count);

            return scene;
        }

        /// <summary>Picks N well-spread author-time spawn positions. Prefers WP1's
        /// anchors_reference sample positions (§C.7 — a sanity cross-check that a chosen position
        /// falls within Arena's own playable area) when present; falls back to a small fixed spread
        /// near the origin otherwise (true for the S1 slice, whose anchors_reference is empty — no
        /// SpawnPointMarker-class objects fell inside the ~50-object slice).</summary>
        private static Vector3[] ResolveSpawnPositions(ArenaPackage pkg, int count)
        {
            var result = new Vector3[count];
            var anchors = pkg.Manifest["anchors_reference"] as JObject;
            JArray samples = null;
            if (anchors != null)
            {
                var entry = anchors["EFT.Game.Spawning.SpawnPointMarker"] as JObject;
                if (entry != null) samples = entry["sample_positions"] as JArray;
            }

            if (samples != null && samples.Count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var p = (JArray)samples[i % samples.Count];
                    result[i] = new Vector3((float)p[0], (float)p[1], (float)p[2]) + new Vector3(i * 0.5f, 0f, 0f);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                    result[i] = new Vector3(i * 2f, 1f, 0f);
            }
            return result;
        }
    }
}
