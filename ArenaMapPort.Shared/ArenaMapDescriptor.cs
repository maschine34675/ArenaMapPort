// ArenaMapPort.Shared — multimap generalization (docs/progress/multimap-generalize.md).
//
// One record per ported Arena location, replacing the ~15-file literal duplication the
// hardcoding audit found (every constant Client/Server/Editor/tooling needs to address one
// map's bundles/scenes/identity, in one place). ArenaEquatorLocation.cs is intentionally left
// completely UNCHANGED by this file — every Equator field below is read directly off that
// class's own consts/statics rather than re-declared as a second literal, so byte-for-byte
// drift between the two is structurally impossible (not just guarded by a unit test).
using System.Collections.Generic;

namespace ArenaMapPort.Shared
{
    /// <summary>
    /// Everything a ported Arena location needs to be served, gated, registered and
    /// displayed — one instance per map key. See docs/progress/multimap-generalize.md §1 for
    /// the full field-by-field rationale.
    ///
    /// A plain class with a constructor, not a C# 9 positional record: ArenaMapPort.Client
    /// targets netstandard2.1, which has no System.Runtime.CompilerServices.IsExternalInit —
    /// `init` accessors (which a positional record's compiler-generated properties use) fail to
    /// compile there (CS0518) without a polyfill. Plain get-only properties avoid the whole
    /// question; nothing here needs record value-equality or `with`-expressions.
    /// </summary>
    public sealed class ArenaMapDescriptor
    {
        public string Key { get; }                 // "arena_equator" / "arena_bay5" — LocationTable key, base.json Id
        public string IdField { get; }              // 24-hex MongoId, minted once, never regenerated (desyncs profiles/insurance)
        public string PresetBundleKey { get; }      // served key, e.g. "maps/arena_equator_preset.bundle"
        public string PresetAssetName { get; }      // e.g. "arena_equator.scenespreset.asset" — base.json Scene.rcid
        public string ScenesBundleKey { get; }      // served key, e.g. "arena_equator/arena_equator_scenes.bundle"
        public string NavMeshBundleKey { get; }     // served key, e.g. "arena_equator/arena_equator_navmesh.bundle"
        public string ScriptsSceneName { get; }     // authored-fresh Scripts scene, loaded first/Single
        public string[] AllSceneNames { get; }      // load/rcid order, ScriptsSceneName first
        public string InfiltrationName { get; }     // SpawnPointMarker.Infiltration value — must be unique per map
        public string LocaleName { get; }
        public string LocaleDescription { get; }
        public int IconX { get; }
        public int IconY { get; }
        public string DbBaseJsonPath { get; }       // path under ArenaMapPort.Server/db/ this map's base.json lives at

        // Parameter names are PascalCase (unusual for a C# constructor) so every call site below
        // can pass them as named arguments matching the property names 1:1 — this reads like the
        // positional-record syntax this class replaced (see class doc comment for why it isn't
        // actually a record) without needing the properties' own names duplicated a third time.
        public ArenaMapDescriptor(
            string Key,
            string IdField,
            string PresetBundleKey,
            string PresetAssetName,
            string ScenesBundleKey,
            string NavMeshBundleKey,
            string ScriptsSceneName,
            string[] AllSceneNames,
            string InfiltrationName,
            string LocaleName,
            string LocaleDescription,
            int IconX,
            int IconY,
            string DbBaseJsonPath)
        {
            this.Key = Key;
            this.IdField = IdField;
            this.PresetBundleKey = PresetBundleKey;
            this.PresetAssetName = PresetAssetName;
            this.ScenesBundleKey = ScenesBundleKey;
            this.NavMeshBundleKey = NavMeshBundleKey;
            this.ScriptsSceneName = ScriptsSceneName;
            this.AllSceneNames = AllSceneNames;
            this.InfiltrationName = InfiltrationName;
            this.LocaleName = LocaleName;
            this.LocaleDescription = LocaleDescription;
            this.IconX = IconX;
            this.IconY = IconY;
            this.DbBaseJsonPath = DbBaseJsonPath;
        }

        // Physical bundle filenames are always the served key's basename — never stored
        // separately, never allowed to drift from the served key (the exact trap
        // NavMeshInstaller.cs and Plugin.cs each independently fell into for Equator, pre-
        // generalization — see docs/progress/multimap-generalize.md).
        public string ScenesBundleFileName => ScenesBundleKey.Substring(ScenesBundleKey.LastIndexOf('/') + 1);
        public string NavMeshBundleFileName => NavMeshBundleKey.Substring(NavMeshBundleKey.LastIndexOf('/') + 1);
        public string PresetBundleFileName => PresetBundleKey.Substring(PresetBundleKey.LastIndexOf('/') + 1);
    }

    /// <summary>
    /// Every registered map, in map-select/registration order. Add a new location by adding one
    /// entry here (plus its tools/maps/&lt;key&gt;.json twin for the Python/Editor side) — no
    /// other file needs a new per-map literal.
    /// </summary>
    public static class ArenaMapRegistry
    {
        /// <summary>
        /// Equator's descriptor — every field copied verbatim off <see cref="ArenaEquatorLocation"/>'s
        /// own consts/statics (not re-declared), so this record can never itself introduce drift.
        /// </summary>
        public static readonly ArenaMapDescriptor Equator = new(
            Key: ArenaEquatorLocation.Key,
            IdField: ArenaEquatorLocation.IdField,
            PresetBundleKey: ArenaEquatorLocation.PresetBundleKey,
            PresetAssetName: ArenaEquatorLocation.PresetAssetName,
            ScenesBundleKey: ArenaEquatorLocation.ScenesBundleKey,
            NavMeshBundleKey: ArenaEquatorLocation.NavMeshBundleKey,
            ScriptsSceneName: ArenaEquatorLocation.ScriptsSceneName,
            AllSceneNames: ArenaEquatorLocation.AllSceneNames,
            InfiltrationName: "ArenaEquator", // SpawnInfiltrationRepairs' pre-generalization literal
            LocaleName: "Arena: Equator (spike)",
            LocaleDescription: "Internal test build — geometry/collision/NavMesh spike.",
            IconX: 150, IconY: 760,
            DbBaseJsonPath: "base.json"); // unchanged legacy path — ArenaMapPort.Server/db/base.json

        /// <summary>
        /// Bay5 (TeamFight) descriptor. Extracted, built and registered (docs/progress/bay5-port.md)
        /// — db/arena_bay5/base.json exists on disk alongside Equator's. ArenaMapPortMod.OnLoadAsync's
        /// per-map try/catch still applies (mirrors Equator's own independence), so a future bad/
        /// missing base.json for either map still can't block the other's registration.
        /// </summary>
        public static readonly ArenaMapDescriptor Bay5 = new(
            Key: "arena_bay5",
            IdField: "f4f851a5fee201ad20a257dc", // sha1("arenamapport:arena_bay5:v1")[:24], mint-once
            PresetBundleKey: "maps/arena_bay5_preset.bundle",
            PresetAssetName: "arena_bay5.scenespreset.asset",
            ScenesBundleKey: "arena_bay5/arena_bay5_scenes.bundle",
            NavMeshBundleKey: "arena_bay5/arena_bay5_navmesh.bundle",
            ScriptsSceneName: "Arena_Bay5_Scripts",
            AllSceneNames: new[]
            {
                "Arena_Bay5_Scripts", "Arena_Bay5_Main", "Arena_Bay5_Light",
                "Arena_Bay5_DesignStuff", "Arena_Bay5_TeamFight",
                "Arena_Bay5_TeamFight_DesignStuff", "Arena_Bay5_DEPOT",
                "Arena_Bay5_WAREHOUSE", "Arena_Bay5_HANGAR",
            },
            // Scene load order matches docs/SPEC.md §B.WP3 step 3's ordering rule, extracted and
            // built per docs/progress/bay5-port.md — still worth a playtest-confirm against the
            // real in-game load order, but no longer an unvalidated proposal.
            InfiltrationName: "ArenaBay5",
            LocaleName: "Arena: Bay 5 (spike)",
            LocaleDescription: "Internal test build — outdoor harbor geometry/collision/NavMesh spike.",
            IconX: 150, IconY: 830,
            DbBaseJsonPath: "arena_bay5/base.json");

        /// <summary>All registered maps, in registration order. Iterate this — never hardcode
        /// <see cref="Equator"/> alone — everywhere a map-generic loop is needed.</summary>
        public static readonly IReadOnlyList<ArenaMapDescriptor> All = new[] { Equator, Bay5 };
    }
}
