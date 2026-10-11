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
using System.IO;
using UnityEngine;

namespace ArenaMapPort.Editor.Common
{
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
                opened_barriers = new[] { "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)" },
                locale_name = "Arena: Equator",
                locale_description = "The Equator shopping mall from EFT Arena. Ground floor playable; the upper rings are scenery.",
            icon_x = 700,
            icon_y = 290,
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
