// ArenaMapPort WP2 — shared path constants + command-line parameter parsing (O7: "Tools must
// accept these as parameters" — every pipeline entry point reads package/output dirs from
// -executeMethod command-line args, falling back to O7's default S1 paths.)
//
// Multimap generalization (docs/progress/multimap-generalize.md): added the -mapDescriptor CLI
// arg + Map property. Every OTHER const/method here is unchanged — an unparameterized
// -executeMethod invocation (no -mapDescriptor passed) keeps working exactly as before, because
// Map falls back to Equator's own literal values (matching
// ArenaMapPort.Shared.ArenaMapRegistry.Equator / tools/maps/equator.json byte-for-byte) when the
// flag is absent.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Common
{
    /// <summary>
    /// One pose row of tools/maps/&lt;stem&gt;.doors.json as the NavMesh bake needs it (2026-10-23, docs/progress/door-poses.md,
    /// "NavMesh bake"): which node of which gate Arena moves. The poses themselves (closed and open transforms, seconds, ease,
    /// curve) are the client's business and are not read here. Plain fields, no Unity types, so the bake's test harness
    /// (tools/tests/test_navmesh_source_layers.py) compiles this class from the production text.
    /// </summary>
    public sealed class ArenaDoorRow
    {
        /// <summary>Hierarchy path of the gate's root (an opened_barriers entry; Equator's booth assemblies are not entries).</summary>
        public string barrier;
        /// <summary>The barrier root's direct child that moves; "" = the barrier root itself (Bay5's rollers).</summary>
        public string part;
        /// <summary>Transform path below the part of the animated node; "" = the part itself. The bake does not need it.</summary>
        public string node;
        /// <summary>World position of the barrier root instance (tells repeated paths apart); null = every instance of the path.</summary>
        public float[] at;
        /// <summary>
        /// The sidecar's closed.position: the LOCAL position of the node the row animates in its closed pose (the part itself
        /// for node ""). The bake uses it only to tell same-named direct children apart (AirPit's 16 passport leaves reuse six
        /// names): the child (for a node row: its node) whose localPosition is within 0.05 m takes the row. Null = the sidecar
        /// gave none; the first child of the name is taken, as before.
        /// </summary>
        public float[] closed_position;
        /// <summary>True for a SetActive row (the sidecar's "switch" object: AirPit's RED / GREEN lamp covers). It moves nothing
        /// and has no colliders, so the bake drops nothing for it and never reports it as unresolved.</summary>
        public bool is_switch;
    }

    /// <summary>
    /// JsonUtility-compatible mirror of tools/maps/&lt;key&gt;.json (same field names, snake_case
    /// to match the Python side exactly) — see that directory's own files for the canonical
    /// values. JsonUtility requires public fields (not properties) and supports only primitive
    /// arrays, no dictionaries — this shape is deliberately flat for that reason.
    /// </summary>
    [Serializable]
    public class ArenaMapDescriptorData
    {
        public string key;
        public string id_field;
        public string scripts_scene_name;
        public string[] all_scene_names;
        // Visual backdrop scenes stay loaded but must not enlarge the playable NavMesh/cover footprint.
        public string[] background_scene_names;

        public bool IsBackgroundScene(string sceneName) =>
            background_scene_names != null && Array.IndexOf(background_scene_names, sceneName) >= 0;
        public string infiltration_name;
        public string locale_name;
        public string locale_description;
        public int icon_x;
        public int icon_y;
        public string template_key;
        public string db_base_json_path;
        // bay5-port: per-map authored-fog color (r,g,b — always length 3), read by
        // AuthorScriptsScene.cs instead of a single hardcoded Equator-blue-tinted literal.
        // Sourced from that map's OWN Arena_<Key>_Scripts scene RenderSettings.m_FogColor (Bay5:
        // level85, r=0.132285476 g=0.147871643 b=0.137986973 — a dark harbor grey-green, distinct
        // from Equator's (0.62,0.66,0.70) blue-grey, which is itself Arena_Equator_Scripts'
        // (level54) own real fog color, not an arbitrary constant). FogColorOrDefault() below
        // falls back to Equator's literal for any older map JSON missing this field.
        public float[] fog_color;
        // exfils-stage2 follow-up / stage 1 (2026-09-20): hierarchy paths of the static round-start barriers the
        // client opens at raid start (BoothDoorRepairs.OpenBarriers, mirrored from ArenaMapDescriptor.OpenedBarriers
        // by BaseJsonTests.OpenedBarriers_DescriptorMirrorsSidecar). BakeNavMesh excludes their non-frame parts
        // from the NavMesh sources so the spaces behind them are not islands. Optional (null/empty = none).
        // 2026-10-23: a barrier that has pose rows in tools/maps/<stem>.doors.json (ArenaPaths.DoorRows) is different: the
        // client parks or drives only its posed parts and leaves every other part standing, so the bake drops only the
        // posed parts' subtrees (BakeNavMesh.ResolveOpenedBarriers). Equator lists none since the same day: its FCK hall
        // shutter stays shut, like Arena's.
        public string[] opened_barriers;
        // PLAN-AIRPIT item 8 (docs/progress/airpit-release.md). Three optional bake-only inputs, mirrored from
        // tools/maps/<key>.json by ArenaMapDescriptor.NavMeshBakeExclusions / BakeBounds / NavMeshBakeLayers and
        // guarded by validate_map_descriptors.py and the xunit mirror tests. Absent/empty (Equator, Bay5) keeps
        // the bake exactly as it was; JsonUtility leaves missing arrays null, BakeNavMesh treats null as empty.
        //   navmesh_bake_exclusions: hierarchy paths ("Root/Child/... (n)", same syntax as opened_barriers) whose
        //     whole subtree is dropped from the NavMesh sources WITHOUT being deactivated at runtime.
        //   bake_bounds: [min_x, max_x, min_y, max_y, min_z, max_z], the bake volume used instead of the renderer union.
        //   navmesh_bake_layers: layer indices CollectSources may use instead of "every layer but Water".
        public string[] navmesh_bake_exclusions;
        public float[] bake_bounds;
        public int[] navmesh_bake_layers;
        // Bowl (docs/PLAN-BOWL.md, 2026-10-18): true keeps the layer-29 LevelBorder cubes as physical bake sources
        // (BakeNavMesh.NonPhysicalLayerReason skips its layer-29 rule; navmesh_bake_layers must list 29) because they
        // are the stadium's only play-area bound and stay active at runtime (ArenaMapDescriptor.KeepLevelBorders).
        // JsonUtility leaves a missing bool false, so Equator, Bay5 and AirPit are unchanged.
        public bool keep_level_borders;

        static readonly float[] DefaultFogColor = { 0.62f, 0.66f, 0.70f };

        public float[] FogColorOrDefault() => (fog_color != null && fog_color.Length == 3) ? fog_color : DefaultFogColor;

        // Bundle served-key/asset-name/filename naming follows the project-wide convention
        // (docs/progress/multimap-generalize.md): never stored in the JSON itself, always derived
        // from `key` — the exact trap NavMeshInstaller.cs/BuildBundles.cs fell into pre-
        // generalization (storing the bundle filename as an independent literal that could drift
        // from the served key) is structurally impossible here.
        public string ScenesBundleFileName => key + "_scenes.bundle";
        public string NavMeshBundleFileName => key + "_navmesh.bundle";
        public string PresetBundleFileName => key + "_preset.bundle";
        public string PresetAssetName => key + ".scenespreset.asset";
        public string PresetMName => key + ".ScenesPreset";
    }

    public static class ArenaPaths
    {
        // O7 default paths.
        public const string DefaultPackageDirS1 = "D:/ArenaMapPort/work/extract/equator_s1";
        public const string DefaultOutputDirS1 = "D:/ArenaMapPort/work/build/s1";
        public const string DefaultPackageDirFull = "D:/ArenaMapPort/work/extract/equator";
        public const string DefaultOutputDirFull = "D:/ArenaMapPort/work/build/full";

        public const string ImportRoot = "Assets/ArenaImport";
        public const string MeshesDir = ImportRoot + "/Meshes";
        public const string TexturesDir = ImportRoot + "/Textures";
        public const string MaterialsDir = ImportRoot + "/Materials";
        public const string PhysicMaterialsDir = ImportRoot + "/PhysicMaterials";
        public const string ScenesDir = ImportRoot + "/Scenes";
        public const string NavMeshAssetDir = ImportRoot + "/NavMesh";

        public const string StandinShadersDir = "Assets/ArenaMapPort/StandinShaders";
        // O1: stand-in shaders are named "Hidden/ArenaMapPort/" + <original BSG shader name> —
        // never the bare original name, so the SDK Editor's own Shader.Find(original) can never
        // ambiguously resolve to our stand-in (that's the entire point of the prefix).
        public const string ShaderNamePrefix = "Hidden/ArenaMapPort/";

        // Equator-only default — kept for any caller that still wants the bare literal (e.g. a
        // diagnostic script never updated to read Map.scripts_scene_name). Every real pipeline
        // entry point (BuildBundles/AuthorScriptsScene/VerifyPatchedBundleLoads) reads
        // Map.scripts_scene_name instead, which equals this exact string when -mapDescriptor is
        // omitted.
        public const string ScriptsSceneName = "Arena_Equator_Scripts";

        static ArenaMapDescriptorData _map;

        /// <summary>
        /// The map descriptor for this invocation, loaded once from -mapDescriptor's JSON file
        /// (tools/maps/&lt;key&gt;.json) or, if that flag is absent, Equator's own literal values
        /// (byte-identical to this pipeline's pre-generalization, Equator-only behavior — see
        /// docs/progress/multimap-generalize.md). Every BuildBundles.cs/AuthorScriptsScene.cs/
        /// VerifyPatchedBundleLoads.cs consumer reads through this instead of a bare Equator
        /// const.
        /// </summary>
        public static ArenaMapDescriptorData Map
        {
            get
            {
                if (_map != null)
                {
                    return _map;
                }

                string path = GetArg("-mapDescriptor", null);
                if (string.IsNullOrEmpty(path))
                {
                    _map = EquatorDefault();
                    return _map;
                }

                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "[ArenaMapPort][ArenaPaths] -mapDescriptor '" + path + "' does not exist");
                }

                string json = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<ArenaMapDescriptorData>(json);
                if (parsed == null || string.IsNullOrEmpty(parsed.key))
                {
                    throw new InvalidOperationException(
                        "[ArenaMapPort][ArenaPaths] -mapDescriptor '" + path + "' did not parse to a " +
                        "valid ArenaMapDescriptorData (missing/empty 'key' — JsonUtility silently returns " +
                        "a zeroed object on most shape mismatches instead of throwing, so this check is " +
                        "the only signal)");
                }

                _map = parsed;
                Debug.Log("[ArenaMapPort][ArenaPaths] loaded map descriptor '" + _map.key + "' from " + path);
                return _map;
            }
        }

        static ArenaDoorRow[] _doorRows;

        /// <summary>
        /// The pose rows of this invocation's map: tools/maps/&lt;stem&gt;.doors.json next to the -mapDescriptor file
        /// (bay5.json -> bay5.doors.json), loaded once. Empty when the flag is absent (Equator's built-in default has no
        /// barrier with poses: its booth leaves are dropped by name) or the map has no such sidecar (none of the four released
        /// maps since 2026-10-24; a map that lists opened barriers without one gets a Warning, see MissingDoorSidecarWarning). Only
        /// barrier/part/node/at, the closed position and the switch flag are kept (2026-10-24: AirPit's repeated leaf names and
        /// lamp switches); the file must be schema 2 and belong to the same map key as the descriptor.
        /// A malformed file throws: a bake that silently ignored its rows would drop a different set of colliders than
        /// the client moves.
        /// </summary>
        public static ArenaDoorRow[] DoorRows
        {
            get
            {
                if (_doorRows != null) return _doorRows;
                string path = DoorSidecarPath(GetArg("-mapDescriptor", null));
                if (path == null || !File.Exists(path))
                {
                    // 2026-10-24 (review of the AirPit rows): a sidecar that is absent from the working tree (airpit.doors.json was
                    // untracked until its commit) used to give zero rows without a word, and the bake then ran the legacy rule that
                    // the client no longer follows. The build stays possible (a map with an unanimated barrier needs no rows), but it says so.
                    string missing = MissingDoorSidecarWarning(path, path != null && Map.opened_barriers != null ? Map.opened_barriers.Length : 0);
                    if (missing != null) Debug.LogWarning("[ArenaMapPort][ArenaPaths] " + missing);
                    _doorRows = new ArenaDoorRow[0];
                    return _doorRows;
                }
                _doorRows = ParseDoorRows(File.ReadAllText(path), path, Map.key);
                Debug.Log("[ArenaMapPort][ArenaPaths] loaded " + _doorRows.Length + " door pose row(s) from " + path);
                return _doorRows;
            }
        }

        /// <summary>
        /// "&lt;dir&gt;/bay5.json" -> "&lt;dir&gt;/bay5.doors.json"; null for no descriptor path, a path that is not a .json file
        /// or one that already is a .doors.json file.
        /// </summary>
        public static string DoorSidecarPath(string descriptorPath)
        {
            const string json = ".json";
            if (string.IsNullOrEmpty(descriptorPath) || !descriptorPath.EndsWith(json, StringComparison.OrdinalIgnoreCase)) return null;
            if (descriptorPath.EndsWith(".doors.json", StringComparison.OrdinalIgnoreCase)) return null;
            return descriptorPath.Substring(0, descriptorPath.Length - json.Length) + ".doors.json";
        }

        /// <summary>
        /// The Warning for a map that lists opened barriers while no doors sidecar exists at <paramref name="sidecarPath"/>; null when
        /// there is nothing to say (no descriptor path to derive a sidecar from, no opened barrier). Only called for a sidecar that was
        /// not found. Every registered map with opened barriers has one (airpit, bay5, bowl), so the Warning is a missing-file signal.
        /// </summary>
        public static string MissingDoorSidecarWarning(string sidecarPath, int openedBarrierCount)
        {
            if (string.IsNullOrEmpty(sidecarPath) || openedBarrierCount <= 0) return null;
            return "no door pose sidecar at " + sidecarPath + " although the descriptor lists " + openedBarrierCount +
                   " opened_barriers: the bake uses the LEGACY rule (every non-frame part of every opened barrier is dropped), which does not " +
                   "match a client that follows the descriptor's door poses. Restore the file, or add an empty one if no barrier of this map is animated";
        }

        /// <summary>
        /// Reads the rows out of a doors sidecar (schema 2: "key", "poses" with barrier, part, node, at, closed.position and
        /// an optional switch per pose).
        /// Throws InvalidOperationException naming <paramref name="origin"/> for a wrong schema or key, a pose without a
        /// barrier, a part or node that is not a string, an "at" that is neither null nor three numbers, a closed.position
        /// that is present but not three numbers, or a "switch" that is neither null nor an object.
        /// </summary>
        public static ArenaDoorRow[] ParseDoorRows(string json, string origin, string expectedKey)
        {
            JObject root = JObject.Parse(json);
            JToken schema = root["schema_version"];
            if (schema == null || schema.Type != JTokenType.Integer || (int)schema != 2)
                throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": schema_version must be 2");
            JToken key = root["key"];
            if (key == null || key.Type != JTokenType.String || (string)key != expectedKey)
                throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": key '" + (key == null ? "(missing)" : key.ToString()) +
                                                    "' is not the descriptor's '" + expectedKey + "'");
            JArray poses = root["poses"] as JArray;
            if (poses == null)
                throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": 'poses' is not an array");
            var rows = new List<ArenaDoorRow>();
            for (int i = 0; i < poses.Count; i++)
            {
                JObject pose = poses[i] as JObject;
                JToken barrier = pose != null ? pose["barrier"] : null;
                JToken part = pose != null ? pose["part"] : null;
                JToken node = pose != null ? pose["node"] : null;
                if (barrier == null || barrier.Type != JTokenType.String || ((string)barrier).Length == 0 ||
                    part == null || part.Type != JTokenType.String || node == null || node.Type != JTokenType.String)
                    throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": poses[" + i +
                                                        "] needs a non-empty string 'barrier' and string 'part' and 'node' (\"\" is allowed for the last two)");
                float[] at = null;
                JToken atToken = pose["at"];
                if (atToken != null && atToken.Type != JTokenType.Null)
                {
                    JArray array = atToken as JArray;
                    if (array == null || array.Count != 3 ||
                        (array[0].Type != JTokenType.Float && array[0].Type != JTokenType.Integer) ||
                        (array[1].Type != JTokenType.Float && array[1].Type != JTokenType.Integer) ||
                        (array[2].Type != JTokenType.Float && array[2].Type != JTokenType.Integer))
                        throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": poses[" + i + "].at must be null or three numbers");
                    at = new[] { (float)array[0], (float)array[1], (float)array[2] };
                }
                // closed.position: absent = no discriminator (null); present it must be three numbers. A "switch" object marks a
                // SetActive row (its contents are the client's business). Both were added for AirPit (2026-10-24).
                float[] closedPosition = null;
                JToken closed = pose["closed"];
                JToken closedPositionToken = closed != null && closed.Type == JTokenType.Object ? closed["position"] : null;
                if (closedPositionToken != null && closedPositionToken.Type != JTokenType.Null)
                {
                    JArray array = closedPositionToken as JArray;
                    if (array == null || array.Count != 3 ||
                        (array[0].Type != JTokenType.Float && array[0].Type != JTokenType.Integer) ||
                        (array[1].Type != JTokenType.Float && array[1].Type != JTokenType.Integer) ||
                        (array[2].Type != JTokenType.Float && array[2].Type != JTokenType.Integer))
                        throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": poses[" + i + "].closed.position must be three numbers");
                    closedPosition = new[] { (float)array[0], (float)array[1], (float)array[2] };
                }
                bool isSwitch = false;
                JToken switchToken = pose["switch"];
                if (switchToken != null && switchToken.Type != JTokenType.Null)
                {
                    if (switchToken.Type != JTokenType.Object)
                        throw new InvalidOperationException("[ArenaMapPort][ArenaPaths] " + origin + ": poses[" + i + "].switch must be an object or absent");
                    isSwitch = true;
                }
                rows.Add(new ArenaDoorRow { barrier = (string)barrier, part = (string)part, node = (string)node, at = at,
                                            closed_position = closedPosition, is_switch = isSwitch });
            }
            return rows.ToArray();
        }

        static ArenaMapDescriptorData EquatorDefault()
        {
            return new ArenaMapDescriptorData
            {
                key = "arena_equator",
                id_field = "e9b8e12c60dfd2820e949462",
                scripts_scene_name = ScriptsSceneName,
                all_scene_names = new[]
                {
                    "Arena_Equator_Scripts",
                    "Arena_Equator",
                    "Arena_Equator_Lighting",
                    "Arena_Equator_DesignStuff",
                    "Arena_Equator_TeamFight",
                    "Arena_Equator_TeamFight_DesignStuff",
                },
                infiltration_name = "ArenaEquator",
                // No opened_barriers (user decision 2026-10-23): the FCK hall's roller shutter
                // 'SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)' stays shut, like Arena's, and is baked as a
                // wall. Equator's booth leaves are dropped by name (BakeNavMesh.ExcludedLeafNames), not by a barrier entry.
                locale_name = "Arena: Equator",
                locale_description = "The Equator shopping mall from EFT Arena. Ground floor playable; the upper rings are scenery.",
            icon_x = 788,
            icon_y = 521,
                template_key = "factory4_day",
            db_base_json_path = "arena_equator/base.json",
                fog_color = new[] { 0.62f, 0.66f, 0.70f },
            };
        }

        /// <summary>Reads a "-flagName value" pair out of Environment.GetCommandLineArgs(), as
        /// passed after -executeMethod on the Unity.exe command line. Falls back to
        /// defaultValue if the flag isn't present.</summary>
        public static string GetArg(string flagName, string defaultValue)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flagName, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return defaultValue;
        }
    }
}
