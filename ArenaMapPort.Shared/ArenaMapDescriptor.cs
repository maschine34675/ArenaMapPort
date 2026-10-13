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
    /// <summary>
    /// exfils-stage2 (docs/progress/exfils-stage2.md) — one exfiltration point the client authors at runtime
    /// (the ported scenes ship no ExfiltrationPoint components; EFT only carries name/timing/eligibility of an
    /// exit from the server, never a position). Name must equal the base.json <c>exits[].Name</c> the server mod
    /// ships (tools/maps/&lt;key&gt;.exits.json is the single source; a unit test keeps this table in step).
    /// Position is the authored floor point (NavMesh-sampled at runtime), Size the trigger box in metres.
    /// </summary>
    public sealed class ArenaExitDescriptor
    {
        public string Name { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float SizeX { get; }
        public float SizeY { get; }
        public float SizeZ { get; }
        public float RotationY { get; }

        public ArenaExitDescriptor(string Name, float X, float Y, float Z,
            float SizeX = 4f, float SizeY = 3f, float SizeZ = 4f, float RotationY = 0f)
        {
            this.Name = Name;
            this.X = X;
            this.Y = Y;
            this.Z = Z;
            this.SizeX = SizeX;
            this.SizeY = SizeY;
            this.SizeZ = SizeZ;
            this.RotationY = RotationY;
        }
    }

    /// <summary>
    /// One invisible wall the client raises at raid start (AuthoredBorderRepairs): a vertical box from (X0, Z0) to
    /// (X1, Z1) spanning MinY..MaxY on the movement layer. AirPit's set follows Arena's own TeamFight outline (the
    /// LevelBorder polygon of its culling bake), with the north edge pulled onto the passport-hall facade line.
    /// Mirrors the map sidecar's "authored_borders" (AirPitMapTests.AuthoredBordersMatchTheSidecar).
    /// </summary>
    public sealed class ArenaBorderDescriptor
    {
        public string Name { get; }
        public float X0 { get; }
        public float Z0 { get; }
        public float X1 { get; }
        public float Z1 { get; }
        public float MinY { get; }
        public float MaxY { get; }

        public ArenaBorderDescriptor(string Name, float X0, float Z0, float X1, float Z1, float MinY, float MaxY)
        {
            this.Name = Name;
            this.X0 = X0;
            this.Z0 = Z0;
            this.X1 = X1;
            this.Z1 = Z1;
            this.MinY = MinY;
            this.MaxY = MaxY;
        }
    }

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
        public string[] BackgroundSceneNames { get; } // loaded visual scenery, excluded from navigation/cover bounds
        public string InfiltrationName { get; }     // SpawnPointMarker.Infiltration value — must be unique per map
        public string LocaleName { get; }
        public string LocaleDescription { get; }
        public int IconX { get; }
        public int IconY { get; }
        public string DbBaseJsonPath { get; }       // path under ArenaMapPort.Server/db/ this map's base.json lives at
        public ArenaExitDescriptor[] Exits { get; } // exfils-stage2 — runtime-authored exits (empty = raid ends by timer only)
        public string[] OpenedBarriers { get; }     // hierarchy paths of static scene objects whose non-frame parts are deactivated
                                                    // at raid start (Arena's round barriers that close a spawn area); empty = none
        // PLAN-AIRPIT item 8 — Editor-side NavMesh bake inputs. The client never reads them; they live here so the one
        // per-map table stays complete and tools/maps/<key>.json cannot drift (validate_map_descriptors.py and the xunit
        // mirror tests). All empty for Equator and Bay5, whose bake is unchanged.
        public string[] NavMeshBakeExclusions { get; } // hierarchy paths whose subtree is dropped from the NavMesh bake sources
        public ArenaBorderDescriptor[] AuthoredBorders { get; } // invisible walls raised at raid start (empty = none)
                                                       // but stays active at runtime (colliders Arena's own bake walks through)
        public float[] BakeBounds { get; }             // [min_x, max_x, min_y, max_y, min_z, max_z] of the bake volume; empty = renderer union
        public int[] NavMeshBakeLayers { get; }        // layers the bake may collect from; empty = every layer but Water
        public string[] RestoredShadowCasterRoots { get; } // exact active LOD roots with source-disabled shadow proxies to restore
        public string[] DistantShadowCasterPaths { get; } // active shadow proxies missing native LOD registration
        public string[] PuddleWaterMaterialNames { get; } // exact imported water-puddle material names; excludes oil decals
        // PLAN-AIRPIT item 11 — the per-map allow-list of ShaderRebind's fallback table: Arena shader names this map's
        // materials may be rebound to an SPT substitute (client ShaderStandinPolicy owns the original -> substitute rows,
        // the switches and the AirPit texture work). Empty = the map never substitutes (Equator and the parked maps).
        public string[] ShaderFallbackNames { get; }
        public bool InheritIndoorFloorDetails { get; } // small floor pieces inherit their supporting structural floor's classification
        public ArenaNeonFixture[] NeonFixtures { get; } // optional authored night installation
        public ArenaNightAccent[] NightAccents { get; } // decorative colored chemlights
        public int? FixedDayHour { get; }
        public int? FixedNightHour { get; }
        public bool UsesFixedStartTimes => FixedDayHour.HasValue && FixedNightHour.HasValue;
        public bool UsesServerTime { get; } // native advancing backend time, CURR/PAST and backend weather
        public bool HasTimeSelection => UsesFixedStartTimes || UsesServerTime;
        public string SunScene { get; }
        public string[] SunPaths { get; }
#nullable enable
        public ArenaDustDescriptor? Dust { get; } // exact source dust owner; null leaves other maps untouched
#nullable restore

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
            string DbBaseJsonPath,
#nullable enable
            ArenaExitDescriptor[]? Exits = null,
            string[]? OpenedBarriers = null,
            string[]? BackgroundSceneNames = null,
            string[]? RestoredShadowCasterRoots = null,
            ArenaDustDescriptor? Dust = null,
            int? FixedDayHour = null,
            int? FixedNightHour = null,
            string? SunScene = null,
            string[]? SunPaths = null,
            string[]? PuddleWaterMaterialNames = null,
            string[]? DistantShadowCasterPaths = null,
            bool InheritIndoorFloorDetails = false,
            ArenaNeonFixture[]? NeonFixtures = null,
            ArenaNightAccent[]? NightAccents = null,
            bool UsesServerTime = false,
            string[]? NavMeshBakeExclusions = null,
            float[]? BakeBounds = null,
            int[]? NavMeshBakeLayers = null,
            string[]? ShaderFallbackNames = null,
            ArenaBorderDescriptor[]? AuthoredBorders = null)
#nullable restore
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
            this.Exits = Exits ?? new ArenaExitDescriptor[0];
            this.OpenedBarriers = OpenedBarriers ?? new string[0];
            this.NavMeshBakeExclusions = NavMeshBakeExclusions ?? new string[0];
            this.AuthoredBorders = AuthoredBorders ?? new ArenaBorderDescriptor[0];
            this.BakeBounds = BakeBounds ?? new float[0];
            this.NavMeshBakeLayers = NavMeshBakeLayers ?? new int[0];
            this.BackgroundSceneNames = BackgroundSceneNames ?? new string[0];
            this.RestoredShadowCasterRoots = RestoredShadowCasterRoots ?? new string[0];
            this.DistantShadowCasterPaths = DistantShadowCasterPaths ?? new string[0];
            this.Dust = Dust;
            this.FixedDayHour = FixedDayHour;
            this.FixedNightHour = FixedNightHour;
            this.UsesServerTime = UsesServerTime;
            this.SunScene = SunScene ?? string.Empty;
            this.SunPaths = SunPaths ?? new string[0];
            this.PuddleWaterMaterialNames = PuddleWaterMaterialNames ?? new string[0];
            this.ShaderFallbackNames = ShaderFallbackNames ?? new string[0];
            this.InheritIndoorFloorDetails = InheritIndoorFloorDetails;
            this.NeonFixtures = NeonFixtures ?? new ArenaNeonFixture[0];
            this.NightAccents = NightAccents ?? new ArenaNightAccent[0];
        }

        public bool IsBackgroundScene(string sceneName) =>
            System.Array.IndexOf(BackgroundSceneNames, sceneName) >= 0;

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
            LocaleName: "Arena: Equator",
            LocaleDescription: "The Equator shopping mall from EFT Arena. Ground floor playable; the upper rings are scenery.",
            IconX: 700, IconY: 290,
            DbBaseJsonPath: "arena_equator/base.json",
            Exits: new[]
            {
                // exfils-stage2 — ground-floor edges beyond the player spawns (Usec/All spawn north at z 38–49,
                // Bear south at z -33.5); mirrors tools/maps/equator.exits.json, guarded by
                // BaseJsonTests.Exits_DescriptorMirrorsSidecar. Human-readable names double as locale keys.
                // Host test 1 (20.09.): the floor ends at z ≈ 49.6 / -44; the spawn booths are NavMesh islands
                // (physically open), and a box within 5 m of a spawn point would extract a fresh spawn — so the
                // gates sit on the far side of each spawn zone, 10 m from the nearest spawn.
                new ArenaExitDescriptor("Equator North Gate", 2.0f, 0.1f, 46.0f),
                new ArenaExitDescriptor("Equator South Gate", 5.0f, 0.1f, -44.0f),
            },
            OpenedBarriers: new[]
            {
                // Spawn barriers (20.09. evening, docs/progress/exfils-stage2.md "Spawn barriers"): the Usec spawn
                // (-11.7, 48.6) sits in the FCK hall, whose corner exit is this roller shutter at (-9.7, 0, 36.0) —
                // static geometry Arena opens per round by a script that is not ported. Of Equator's 20 shutters this
                // is the only one near a spawn; the other 19 line the outer walls. A hierarchy path, not a name: the
                // "(n)" names repeat in the scene ("equator_roller_shutters_02 (4)" exists twice).
                "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)",
            },
            Dust: new ArenaDustDescriptor("Arena_Equator_Lighting", "EQUATOR_DUST", 0.09f, 0.03f, 1, 1.0f,
                2.280590057373047f, 1.6160001754760742f, 3.3339996337890625f,
                31.478260040283203f, 5.016374111175537f, 48.85846710205078f),
            FixedDayHour: 12,
            NeonFixtures: ArenaNeonLayout.Equator,
            NightAccents: ArenaNeonLayout.EquatorAccents,
            InheritIndoorFloorDetails: true,
            FixedNightHour: 0,
            SunScene: "Arena_Equator_Lighting",
            SunPaths: new[] { "FAKE_SUN", "FAKE_SUN_VOLUME" },
            DistantShadowCasterPaths: new[]
            {
                // Original active ceiling proxies lack LOD membership required by EFT DistantShadow.
                // Floor 04 already has it. Keep original apertures, materials and meshes.
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_01/equator_floor_01_SHADOW_LOD0",
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_02/equator_floor_02_SHADOW_LOD0",
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_03/equator_floor_03_SHADOW_LOD0"
            });

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
                "Arena_Bay5_Background",
            },
            BackgroundSceneNames: new[] { "Arena_Bay5_Background" },
            // Scene load order matches docs/SPEC.md §B.WP3 step 3's ordering rule, extracted and
            // built per docs/progress/bay5-port.md — still worth a playtest-confirm against the
            // real in-game load order, but no longer an unvalidated proposal.
            InfiltrationName: "ArenaBay5",
            LocaleName: "Arena: Bay 5",
            LocaleDescription: "The Bay 5 harbour depot from EFT Arena: container yard, hangar, warehouse and depot.",
            IconX: 320, IconY: 820,
            DbBaseJsonPath: "arena_bay5/base.json",
            Exits: new[]
            {
                new ArenaExitDescriptor("Bay5 North Loading Bay", 96.0f, -2.9252f, 61.0f),
                new ArenaExitDescriptor("Bay5 South Container Lane", 99.029f, -3.8574f, -2.8513f),
            },
            OpenedBarriers: new[]
            {
                // Two west-facing USEC warehouse leaves and the first/northern BEAR depot gate.
                // The latter has a duplicate sibling name; both existing path resolvers select
                // the first assembly. No claim that its eastern duplicate is opened.
                "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)",
                "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
                "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame",
            },
            RestoredShadowCasterRoots: new[]
            {
                // Source level84 object25426: all three ShadowsOnly children are inactive,
                // while the visible LODs have casting Off. User report 2026-09-23 06:43.
                "SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03",
                // Adjacent source object25415 has the same inactive shadow proxies (2026-09-28).
                "SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03 (1)",
            },
            PuddleWaterMaterialNames: new[]
            {
                // Original "puddle indoor static" material; oil decals share the shader but are not water.
                "70f045bb011600e7",
            },
            // All six rows of the fallback table, as shipped (behind the legacy MaterialFixes/EnableShaderFallbacks
            // switch, default off, plain shader swap). Mirrors tools/maps/bay5.json (ShaderStandinPolicyTests).
            ShaderFallbackNames: new[]
            {
                "Custom/WorldTriplanarFresnelPro",
                "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)",
                "Cloth/ClothShader",
                "Cloth/ClothShader_backface",
                "Custom/Billboard_FogSheet_Simple",
                "p0/Reflective/Bumped Specular SMap Baked",
            },
            Dust: new ArenaDustDescriptor("Arena_Bay5_Light", "DUST", 0.07f, 0.04f, 1, 0.0f,
                99.63274383544922f, -1.5299997329711914f, 30.428937911987305f,
                52.685890197753906f, 12.275176048278809f, 42.59858703613281f),
            UsesServerTime: true);

        // Source scene selection: tools/extract/plans/airpit.json. Identity is permanent.
        public static readonly ArenaMapDescriptor AirPit = new(
            Key: "arena_airpit",
            IdField: "b7ed175254b3c9dce8653ae5",
            PresetBundleKey: "maps/arena_airpit_preset.bundle",
            PresetAssetName: "arena_airpit.scenespreset.asset",
            ScenesBundleKey: "arena_airpit/arena_airpit_scenes.bundle",
            NavMeshBundleKey: "arena_airpit/arena_airpit_navmesh.bundle",
            ScriptsSceneName: "Arena_AirPit_Scripts",
            AllSceneNames: new[] { "Arena_AirPit_Scripts", "Arena_AirPit_ShootOut", "Arena_AirPit_ShootOut_Light", "Arena_Airpit_DesignStuff", "Arena_Airpit_TeamFight", "Arena_Airpit_TeamFight_DesignStuff", "Arena_Airpit_Main_hall", "Arena_Airpit_Light" },
            InfiltrationName: "ArenaAirPit",
            LocaleName: "Arena: Air Pit",
            LocaleDescription: "The Air Pit airport terminal from EFT Arena: a large hall with an airliner inside, passport lanes to the north and a bus apron to the south. Ground floor and mezzanine are the main play space.",
            // Map-select slot (0..1000, the native UI divides by 1000): inside the box spanned by Equator
            // (700, 290) and Bay5 (320, 820), 200 units from Equator and 550 from Bay5, and at least 165
            // units from every vanilla icon (nearest: Customs 168, Interchange 171).
            IconX: 500,
            IconY: 300,
            DbBaseJsonPath: "arena_airpit/base.json",
            Exits: new[]
            {
                // Mirrors tools/maps/airpit.exits.json (AirPitMapTests.ExitTransformsMatchTheSidecarAndServerEligibility).
                // Floor points on the two ends of the play space, 14.8 m / 17.8 m from the nearest player spawn.
                new ArenaExitDescriptor("Air Pit North Gate", -11.0f, 0.1f, 62.0f),
                new ArenaExitDescriptor("Air Pit South Gate", -3.0f, 0.1f, -58.0f),
            },
            OpenedBarriers: new[]
            {
                // The two gate groups of Arena's TeamFight round start (GateEntity roots in
                // Arena_Airpit_TeamFight_DesignStuff). Single-segment root paths: BoothDoorRepairs.FindByPath and
                // BakeNavMesh.ResolveHierarchyPaths resolve a bare name to the scene root itself. Neither root has a
                // frame-named object, so every direct child is deactivated at raid start and every collider below
                // is left out of the bake. BUS_DOORS: 6 bus-door assemblies sealing the south spawn pocket;
                // PASSPORT_CONTROL_DOORS: 16 lane doors sealing the north spawn field, plus the GREEN/RED fog sheets.
                // Mirrors tools/maps/airpit.json (AirPitMapTests.OpenedBarriersAndBakeInputsMatchTheSidecar).
                "BUS_DOORS",
                // PASSPORT_CONTROL_DOORS stays CLOSED (user decision 2026-10-12, host raid 4/5): the 16 lane "doors"
                // are waist-high turnstile gates (1.09 x 1.35 m) in eight lanes with 7-13 m of open floor between
                // them, so they are the closed-passport-control LOOK (red PASSPORT_CONTROL_EMISSIVE/RED lane lights
                // above the gate posts), not a barrier - the walk around them is open, host raid 5. The north limit
                // is the authored facade wall below; the north player and bot spawns sit south of the gate line
                // (tools/maps/airpit.bots.json) and the bake box ends at z 70, so bots have no business in the pocket.
                // ArenaMode may open the gates itself (Spawns/OpenSpawnBarriers lists them no more; the GateEntity
                // roots are documented in docs/progress/airpit-release.md).
                // The two metal-detector portals in the z 50.5 facade between the passport hall and the main hall
                // (host raid 1, 2026-10-12): each Prison_Metal_Fence_01 gate holds an active closed fence and two
                // inactive swung-open halves (_PART_L/_PART_R), i.e. a gate Arena closes for ShootOut and opens for
                // TeamFight. With them closed the whole north spawn field plus the east wing is sealed from the hall
                // for players and bots alike (the only NavMesh route ran through a shop shutter). Deactivating the
                // direct children leaves the portal frame standing; the open halves stay inactive.
                "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)",
                "SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (3)",
                // The six closed shop shells of the ShootOut build: the furnished shops of Arena_Airpit_Main_hall carry the
                // rolled-up shutters, so BoothDoorRepairs opens these at raid start and the bake leaves them out.
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/Airport_Shop_BS_closed_closed",
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed",
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)",
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed",
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_02_closed",
                "SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_03_closed",
                // Three white blockout panels (layer Default, 12.7-14 x 3.3 x 0.16 m, BoxCollider) that the ShootOut
                // build lays over the south-west shop's three roll-door lines on top of the shells. Arena's bake mask
                // never saw them (layer 0), so the NavMesh runs through while the player collides (host raid 2026-10-13,
                // F10 at (-44, -7.5) facing south). With the furnished shop they go the way of the shells.
                "SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group",
            },
            // No NavMeshBakeExclusions: the four bake-only cutters of 2026-10-11 (DonB_Ramen_closed x2,
            // TH_Roll_door_01_closed, fence_ema_01_white (18)) let the NavMesh run through shutters that stay solid;
            // host raid 1 walked the player into the DonB_Ramen shutter at (-46.2, 0, 43.4). Every solid is baked.
            // Play space: the AABB of the authored borders below (Arena's TeamFight outline, x -56.8..36.2,
            // z -82.5..83.6) cut at z 70, just south of the closed passport-gate line (z 69.4), y -2..8 = ground
            // floor plus wing mezzanine (user decision 2026-10-11, 4.3). Arena's own mesh has no polygons in the
            // gate band z 66..69.8 either (its bake predates the current gates); the pocket behind it stays without
            // mesh. The 2026-10-11 box (x -80..56, z -96..92) baked the pocket, 7 m of the dark entrance hall and
            // strips of runway and forecourt (host raids 1-4, 2026-10-12).
            BakeBounds: new[] { -57.0f, 37.0f, -2.0f, 8.0f, -83.0f, 70.0f },
            // Arena's own NavMeshSurface layer mask (decoded from the three AI scenes, mask 0x40042A00):
            // DoorLowPolyCollider, Terrain, Triggers, LowPolyCollider, TransparentCollider. EFT's movement test mask
            // (LayersMaskController.PlayerCollisionTestMask) is LowPolyCollider + DoorLowPolyCollider + Terrain; the
            // HighPolyCollider (12) meshes are the ballistic layer. The owner rule of BakeNavMesh.NonPhysicalLayerReason
            // still drops non-trigger boxes left on the Triggers layer (AirPit has none).
            NavMeshBakeLayers: new[] { 9, 11, 13, 18, 30 },
            // Invisible walls (AuthoredBorderRepairs, 2026-10-12). Arena bounds its TeamFight area by a data polygon
            // (`ExcludeLevelBorders` in the culling scene, 12 vertices; every NavMesh vertex and anchor lies inside),
            // not by colliders: the passport-hall facade has a 32 m opening (x -28..4 at z 82.5) into the unlit
            // entrance hall, and the apron opens onto the runway. The ShootOut LevelBorder cubes all lie inside the
            // outline and stay removed. The north edge is pulled from the polygon's (-32.4, 88.3)-(-2.2, 89.9)-
            // (8.8, 83.6) onto the facade line so the wall sits in the doorway; it is what closes the unlit entrance
            // hall (host raid 5: F10 from the spawn hits 'north-facade-gap' at 23.7 m). Mirrors tools/maps/airpit.json.
            AuthoredBorders: new[]
            {
                new ArenaBorderDescriptor("north-west-approach", -33.86f, 74.16f, -28.5f, 82.5f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("north-facade-gap", -28.5f, 82.5f, 4.6f, 82.5f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("north-east-jog", 4.6f, 82.5f, 8.84f, 83.6f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("east-upper", 8.84f, 83.6f, 36.24f, 41.15f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("east", 36.24f, 41.15f, 36.24f, -48.3f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("south-east", 36.24f, -48.3f, 1.44f, -70.11f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("south-pocket-east", 1.44f, -70.11f, 0.0f, -81.96f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("south", 0.0f, -81.96f, -21.03f, -82.54f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("south-pocket-west", -21.03f, -82.54f, -26.22f, -72.6f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("south-west", -26.22f, -72.6f, -56.03f, -41.66f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("west", -56.03f, -41.66f, -56.81f, 44.28f, -1.0f, 12.0f),
                new ArenaBorderDescriptor("north-west", -56.81f, 44.28f, -33.86f, 74.16f, -1.0f, 12.0f),
            },
            // PLAN-AIRPIT item 11: the four Arena shader names AirPit's materials use that SPT does not ship (fog sheet,
            // both cloth shaders, the mountain rock). Behind MaterialFixes/AirPitShaderStandins (default on). Mirrors
            // tools/maps/airpit.json (ShaderStandinPolicyTests).
            ShaderFallbackNames: new[]
            {
                "Custom/Billboard_FogSheet_Simple",
                "Cloth/ClothShader",
                "Cloth/ClothShader_backface",
                "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)",
            },
            // Time policy (user decision 2026-10-11, PLAN-AIRPIT 4.2): Bay5-style native server time with the ordinary
            // CURR/PAST choice, an advancing sky and backend weather. No fixed hours, no sun (the map has no
            // Directional light). Night is uncertified until a host look-test (509 of 813 lights are saved off).
            // Mirrors tools/maps/airpit.json (FixedRaidTimeWeatherTests).
            UsesServerTime: true);

        // Source scene selection: tools/extract/plans/airpit2.json. Identity is permanent.
        public static readonly ArenaMapDescriptor AirPit2 = new(
            Key: "arena_airpit2",
            IdField: "191e2e3e2e4add9f785844cb",
            PresetBundleKey: "maps/arena_airpit2_preset.bundle",
            PresetAssetName: "arena_airpit2.scenespreset.asset",
            ScenesBundleKey: "arena_airpit2/arena_airpit2_scenes.bundle",
            NavMeshBundleKey: "arena_airpit2/arena_airpit2_navmesh.bundle",
            ScriptsSceneName: "Arena_AirPit2_Scripts",
            AllSceneNames: new[] { "Arena_AirPit2_Scripts", "Arena_AirPit_2.0", "Arena_AirPit_2.0_Light", "Arena_AirPit_2.0_DesignStuff", "Arena_AirPit_2.0_CheckPoint_DesignStuff" },
            InfiltrationName: "ArenaAirPit2",
            LocaleName: "Arena: Air Pit 2.0 (spike)",
            LocaleDescription: "Internal test build — CheckPoint geometry/collision/NavMesh port.",
            IconX: 295,
            IconY: 830,
            DbBaseJsonPath: "arena_airpit2/base.json");

        // Source scene selection: tools/extract/plans/autoservice.json. Identity is permanent.
        public static readonly ArenaMapDescriptor AutoService = new(
            Key: "arena_autoservice",
            IdField: "e6aa4591d3e1a59835d625b3",
            PresetBundleKey: "maps/arena_autoservice_preset.bundle",
            PresetAssetName: "arena_autoservice.scenespreset.asset",
            ScenesBundleKey: "arena_autoservice/arena_autoservice_scenes.bundle",
            NavMeshBundleKey: "arena_autoservice/arena_autoservice_navmesh.bundle",
            ScriptsSceneName: "Arena_AutoService_Scripts",
            AllSceneNames: new[] { "Arena_AutoService_Scripts", "Arena_AutoService", "Arena_AutoService_DesignStuff", "Arena_AutoService_TeamFight_DesignStuff", "Arena_AutoService_Light" },
            InfiltrationName: "ArenaAutoService",
            LocaleName: "Arena: Auto Service (spike)",
            LocaleDescription: "Internal test build — TeamFight geometry/collision/NavMesh port.",
            IconX: 440,
            IconY: 760,
            DbBaseJsonPath: "arena_autoservice/base.json");

        // Source scene selection: tools/extract/plans/bowl.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Bowl = new(
            Key: "arena_bowl",
            IdField: "3151df68a9deac47a2f83afe",
            PresetBundleKey: "maps/arena_bowl_preset.bundle",
            PresetAssetName: "arena_bowl.scenespreset.asset",
            ScenesBundleKey: "arena_bowl/arena_bowl_scenes.bundle",
            NavMeshBundleKey: "arena_bowl/arena_bowl_navmesh.bundle",
            ScriptsSceneName: "Arena_Bowl_Scripts",
            AllSceneNames: new[] { "Arena_Bowl_Scripts", "Arena_Bowl", "Arena_Bowl_Building", "Arena_Bowl_Grass", "Arena_Bowl_Light", "Arena_Bowl_DesignStuff", "Arena_Bowl_TeamFight", "Arena_Bowl_TeamFight_DesignStuff" },
            InfiltrationName: "ArenaBowl",
            LocaleName: "Arena: Bowl (spike)",
            LocaleDescription: "Internal test build — TeamFight geometry/collision/NavMesh port.",
            IconX: 440,
            IconY: 830,
            DbBaseJsonPath: "arena_bowl/base.json");

        // Source scene selection: tools/extract/plans/prison.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Prison = new(
            Key: "arena_prison",
            IdField: "1da5bcb15506b795f1e8bd82",
            PresetBundleKey: "maps/arena_prison_preset.bundle",
            PresetAssetName: "arena_prison.scenespreset.asset",
            ScenesBundleKey: "arena_prison/arena_prison_scenes.bundle",
            NavMeshBundleKey: "arena_prison/arena_prison_navmesh.bundle",
            ScriptsSceneName: "Arena_Prison_Scripts",
            AllSceneNames: new[] { "Arena_Prison_Scripts", "Arena_Prison", "Arena_Prison_Grass", "Arena_Prison_DesignStuff", "Arena_Prison_TeamFight", "Arena_Prison_Light", "Arena_Prison_TeamFight_DesignStuff" },
            InfiltrationName: "ArenaPrison",
            LocaleName: "Arena: Prison (spike)",
            LocaleDescription: "Internal test build — TeamFight geometry/collision/NavMesh port.",
            IconX: 585,
            IconY: 760,
            DbBaseJsonPath: "arena_prison/base.json");

        // Source scene selection: tools/extract/plans/railwaystation.json. Identity is permanent.
        public static readonly ArenaMapDescriptor RailwayStation = new(
            Key: "arena_railwaystation",
            IdField: "94a181a24efce46d1e7eb359",
            PresetBundleKey: "maps/arena_railwaystation_preset.bundle",
            PresetAssetName: "arena_railwaystation.scenespreset.asset",
            ScenesBundleKey: "arena_railwaystation/arena_railwaystation_scenes.bundle",
            NavMeshBundleKey: "arena_railwaystation/arena_railwaystation_navmesh.bundle",
            ScriptsSceneName: "Arena_RailwayStation_Scripts",
            AllSceneNames: new[] { "Arena_RailwayStation_Scripts", "Arena_Train", "Arena_Train_DesignStuff", "Arena_Train_Grass", "Arena_Train_TeamFight", "Arena_Train_TeamFight_DesignStuff", "Arena_Train_Light_TeamFight" },
            InfiltrationName: "ArenaRailwayStation",
            LocaleName: "Arena: Railway Station (spike)",
            LocaleDescription: "Internal test build — TeamFight geometry/collision/NavMesh port.",
            IconX: 585,
            IconY: 830,
            DbBaseJsonPath: "arena_railwaystation/base.json");

        // Source scene selection: tools/extract/plans/yard.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Yard = new(
            Key: "arena_yard",
            IdField: "187d5cbc21744c97e8348e76",
            PresetBundleKey: "maps/arena_yard_preset.bundle",
            PresetAssetName: "arena_yard.scenespreset.asset",
            ScenesBundleKey: "arena_yard/arena_yard_scenes.bundle",
            NavMeshBundleKey: "arena_yard/arena_yard_navmesh.bundle",
            ScriptsSceneName: "Arena_Yard_Scripts",
            AllSceneNames: new[] { "Arena_Yard_Scripts", "Arena_Yard", "Arena_Yard_Grass", "Arena_Yard_DesignStuff", "Arena_Yard_TeamFight", "Arena_Yard_TeamFight_DesignStuff", "Arena_Yard_Light" },
            InfiltrationName: "ArenaYard",
            LocaleName: "Arena: Yard (spike)",
            LocaleDescription: "Internal test build — TeamFight geometry/collision/NavMesh port.",
            IconX: 730,
            IconY: 760,
            DbBaseJsonPath: "arena_yard/base.json");

        // Source scene selection: tools/extract/plans/iceberg.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Iceberg = new(
            Key: "arena_iceberg",
            IdField: "2e2d6dadf4ebb29e53a69aae",
            PresetBundleKey: "maps/arena_iceberg_preset.bundle",
            PresetAssetName: "arena_iceberg.scenespreset.asset",
            ScenesBundleKey: "arena_iceberg/arena_iceberg_scenes.bundle",
            NavMeshBundleKey: "arena_iceberg/arena_iceberg_navmesh.bundle",
            ScriptsSceneName: "Arena_Iceberg_Scripts",
            AllSceneNames: new[] { "Arena_Iceberg_Scripts", "Arena_Sanatorium", "Arena_Sanatorium_Grass", "Arena_Sanatorium_Light", "Arena_Sanatorium_DesignStuff", "Arena_Sanatorium_CheckPoint", "Arena_Sanatorium_CheckPoint_DesignStuff" },
            InfiltrationName: "ArenaIceberg",
            LocaleName: "Arena: Iceberg (spike)",
            LocaleDescription: "Internal test build — CheckPoint geometry/collision/NavMesh port.",
            IconX: 730,
            IconY: 830,
            DbBaseJsonPath: "arena_iceberg/base.json");

        // Source scene selection: tools/extract/plans/factory.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Factory = new(
            Key: "arena_factory",
            IdField: "8c710054b674251da2e5bf02",
            PresetBundleKey: "maps/arena_factory_preset.bundle",
            PresetAssetName: "arena_factory.scenespreset.asset",
            ScenesBundleKey: "arena_factory/arena_factory_scenes.bundle",
            NavMeshBundleKey: "arena_factory/arena_factory_navmesh.bundle",
            ScriptsSceneName: "Arena_Factory_Scripts",
            AllSceneNames: new[] { "Arena_Factory_Scripts", "Arena_Factory", "Arena_Factory_Light", "Arena_Factory_DesignStuff", "Arena_Factory_SO_DesignStuff" },
            InfiltrationName: "ArenaFactory",
            LocaleName: "Arena: Factory (spike)",
            LocaleDescription: "Internal test build — ShootOutDuo geometry/collision/NavMesh port.",
            IconX: 875,
            IconY: 760,
            DbBaseJsonPath: "arena_factory/base.json");

        // Source scene selection: tools/extract/plans/custom.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Custom = new(
            Key: "arena_custom",
            IdField: "8d293d9d2a8fd179128bea17",
            PresetBundleKey: "maps/arena_custom_preset.bundle",
            PresetAssetName: "arena_custom.scenespreset.asset",
            ScenesBundleKey: "arena_custom/arena_custom_scenes.bundle",
            NavMeshBundleKey: "arena_custom/arena_custom_navmesh.bundle",
            ScriptsSceneName: "Arena_Custom_Scripts",
            AllSceneNames: new[] { "Arena_Custom_Scripts", "Arena_Custom_LH", "Arena_Custom_LH_Light", "Arena_Custom_LH_Terrain", "Arena_NY_Custom_LH_DesignStuff", "Arena_NY_Custom_LastHero_DesignStuff" },
            InfiltrationName: "ArenaCustom",
            LocaleName: "Arena: Custom (spike)",
            LocaleDescription: "Internal test build — LastHero geometry/collision/NavMesh port.",
            IconX: 875,
            IconY: 830,
            DbBaseJsonPath: "arena_custom/base.json");

        // Source scene selection: tools/extract/plans/shoreline.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Shoreline = new(
            Key: "arena_shoreline",
            IdField: "ac3fe0b02d506baeb62f6de4",
            PresetBundleKey: "maps/arena_shoreline_preset.bundle",
            PresetAssetName: "arena_shoreline.scenespreset.asset",
            ScenesBundleKey: "arena_shoreline/arena_shoreline_scenes.bundle",
            NavMeshBundleKey: "arena_shoreline/arena_shoreline_navmesh.bundle",
            ScriptsSceneName: "Arena_Shoreline_Scripts",
            AllSceneNames: new[] { "Arena_Shoreline_Scripts", "Arena_Shoreline_LH", "Arena_Shoreline_LH_Terrain", "Arena_Shoreline_LH_Light", "Arena_Shoreline_LH_DesignStuff", "Arena_NY_Shoreline_LastHero_DesignStuff" },
            InfiltrationName: "ArenaShoreline",
            LocaleName: "Arena: Shoreline (spike)",
            LocaleDescription: "Internal test build — LastHero geometry/collision/NavMesh port.",
            IconX: 1020,
            IconY: 760,
            DbBaseJsonPath: "arena_shoreline/base.json");

        // Source scene selection: tools/extract/plans/woods.json. Identity is permanent.
        public static readonly ArenaMapDescriptor Woods = new(
            Key: "arena_woods",
            IdField: "88ca77b5e9af71bf064f8666",
            PresetBundleKey: "maps/arena_woods_preset.bundle",
            PresetAssetName: "arena_woods.scenespreset.asset",
            ScenesBundleKey: "arena_woods/arena_woods_scenes.bundle",
            NavMeshBundleKey: "arena_woods/arena_woods_navmesh.bundle",
            ScriptsSceneName: "Arena_Woods_Scripts",
            AllSceneNames: new[] { "Arena_Woods_Scripts", "Arena_Woods_LH", "Arena_Woods_LH_Terrain", "Arena_Woods_LH_Light", "Arena_NY_Woods_LH_DesignStuff", "Arena_NY_Woods_LastHero_DesignStuff" },
            InfiltrationName: "ArenaWoods",
            LocaleName: "Arena: Woods (spike)",
            LocaleDescription: "Internal test build — LastHero geometry/collision/NavMesh port.",
            IconX: 1020,
            IconY: 830,
            DbBaseJsonPath: "arena_woods/base.json");

        /// <summary>Complete development catalog. Parked maps retain their stable identities and inputs.</summary>
        public static readonly IReadOnlyList<ArenaMapDescriptor> Catalog = new[] { Equator, Bay5, AirPit, AirPit2, AutoService, Bowl, Prison, RailwayStation, Yard, Iceberg, Factory, Custom, Shoreline, Woods };

        /// <summary>Playable and distributable maps. Runtime consumers must iterate this release subset.</summary>
        public static readonly IReadOnlyList<ArenaMapDescriptor> All = new[] { Equator, Bay5, AirPit };
    }
}
