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

    /// <summary>What the raid-start opening does with an OpenedBarriers entry (ArenaMapDescriptor.GateRoleOf).</summary>
    public enum ArenaGateRole
    {
        Plain,  // switched off at raid start, never offered to SpawnGates
        Start,  // offered to SpawnGates: held closed only while a mode plugin holds the gates
        Held,   // closed at raid start in every mode, sealed for bots, opened only by SpawnGates.Release
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

    /// <summary>
    /// The empty cells of a ceiling lamp lattice that LampFillRepairs fills at raid start with copies of one authored
    /// template lamp: <see cref="TemplatePath"/> is the hierarchy path of the template (root scene object first, as
    /// <c>BoothDoorRepairs.FindByPath</c> reads it), <see cref="Y"/> the world height of every copy and
    /// <see cref="Cells"/> the flat list x0, z0, x1, z1, ... of their world positions. A flat float array keeps the
    /// registry literal short; <see cref="Count"/>, <see cref="CellX"/> and <see cref="CellZ"/> read it as pairs.
    /// AirPit's passport hall is the only user. Mirrors the map sidecar's "lamp_fill"
    /// (AirPitMapTests.LampFillMatchesTheSidecar).
    /// </summary>
    public sealed class ArenaLampFillDescriptor
    {
        public string TemplatePath { get; }
        public float Y { get; }
        public float[] Cells { get; }

        /// <summary>Number of cells. A trailing unpaired value is not a cell (the consistency tests refuse an odd array).</summary>
        public int Count => Cells.Length / 2;

        public float CellX(int index) => Cells[2 * index];

        public float CellZ(int index) => Cells[2 * index + 1];

        /// <summary>Name of the copy for cell <paramref name="index"/>; "(fill 0)" doubles as the idempotence marker.</summary>
        public static string CloneName(string templateName, int index) => templateName + " (fill " + index + ")";

        public ArenaLampFillDescriptor(string TemplatePath, float Y, float[] Cells)
        {
            this.TemplatePath = TemplatePath;
            this.Y = Y;
            this.Cells = Cells;
        }
    }

    /// <summary>
    /// One node an Arena gate moves (tools/maps/&lt;stem&gt;.doors.json, docs/progress/door-poses.md): the closed pose it
    /// has in the scene and the open END pose of Arena's own animation. <see cref="Barrier"/> is the path of the gate's
    /// root (an OpenedBarriers entry; Equator's booth doors are not barriers, their path is the assembly root under
    /// SPAWN_DOORS or Respawn_2; a LAMP GROUP is no barrier either: Equator's EXIT_LIGHT_EMISSIVE and AirPit's BUS_LIGHT carry
    /// switch rows only, DoorPoseModel.LampBarriers), <see cref="Part"/> the barrier root's direct child (the object BoothDoorRepairs switches
    /// off; "" = the barrier root itself moves: Bay5's rollers), <see cref="Node"/> the transform path below the part that
    /// is animated ("" = the part itself). Positions and euler angles are LOCAL to the node's parent (Unity localPosition
    /// / localEulerAngles).
    ///
    /// Source: <see cref="SourceClip"/> (a Mecanim clip, Bowl: the angles are the clip's raw values, multi-turn spins
    /// included, meant to be interpolated component-wise as Quaternion.Euler of a lerped triple),
    /// <see cref="SourceDotAnimator"/> (Arena's DOTween script: the targets are absolute local values, rotations are
    /// meant to be interpolated as a quaternion slerp between Quaternion.Euler(ClosedEuler) and Quaternion.Euler(OpenEuler))
    /// or <see cref="SourceBorrowed"/> (Arena authors no motion for this gate, the neighbour's is used).
    /// <see cref="Seconds"/> is Arena's own duration, unscaled. <see cref="Ease"/> maps the elapsed fraction k of the
    /// duration to the fraction of the path: smoothstep k*k*(3-2k) (the client's stand-in for Bowl's clips), linear k,
    /// inOutQuint (DOTween: k2 = 2k; k2 &lt; 1 ? 0.5*k2^5 : 0.5*((k2-2)^5 + 2)) or curve (Unity AnimationCurve of
    /// <see cref="Curve"/>: a flat array of time, value, inSlope, outSlope per key on a 0..1 timeline, cubic Hermite between
    /// neighbouring keys with the left key's out slope and the right key's in slope; null for the other eases).
    /// <see cref="At"/> is null, or the WORLD position of the barrier root instance the pose is for (the same hierarchy
    /// path occurs twice: Equator's two unnumbered Door_Sliding_Respawn, Bay5's two Gates_Depot_Frame); it matches an
    /// instance within <see cref="AtTolerance"/>. Mirrors the sidecar's "poses" number for number (DoorPoseSidecarTests,
    /// validate_map_descriptors.py).
    ///
    /// A SWITCH row (<see cref="IsSwitch"/>, AirPit's passport lamps RED / GREEN, 2026-10-24; the exit lamps of Equator and AirPit, 2026-10-25) does not move its node: it
    /// sets the node's active state, <see cref="ClosedActive"/> while the gate is closed (the node's state in the scene) and
    /// <see cref="OpenActive"/> once it is open. Its closed and open transforms are the node's scene transform on both
    /// sides, <see cref="Seconds"/> is Arena's duration word (0), <see cref="Ease"/> is linear and unused. The two switch
    /// parameters are the constructor's last optional ones (named arguments in the registry), so every transform row is
    /// unchanged. A part NAME may repeat among one barrier's direct children (AirPit's 16 passport leaves reuse 6 names): the
    /// lookup returns every row of the name and the client picks the rows of a node by the closed position
    /// (DoorPoseModel.SelectRowsForNode, within <see cref="ClosedPositionTolerance"/>).
    /// </summary>
    public sealed class ArenaDoorPose
    {
        public const string SourceClip = "clip";
        public const string SourceDotAnimator = "dotanimator";
        public const string SourceBorrowed = "borrowed";
        public const string EaseSmoothstep = "smoothstep";
        public const string EaseLinear = "linear";
        public const string EaseInOutQuint = "inOutQuint";
        public const string EaseCurve = "curve";

        /// <summary>A barrier root closer than this (metres) to <see cref="At"/> is the instance the pose is for.</summary>
        public const float AtTolerance = 0.05f;

        /// <summary>Rows that share a part (and node) name are told apart by the closed local position: a node whose local
        /// position lies within this many metres of <see cref="ClosedPosition"/> takes the row (AirPit's passport leaves).</summary>
        public const float ClosedPositionTolerance = 0.05f;

        public string Barrier { get; }
        public string Part { get; }
        public string Node { get; }
        public string Clip { get; }
        public float Seconds { get; }
        public float[] ClosedPosition { get; }
        public float[] ClosedEuler { get; }
        public float[] OpenPosition { get; }
        public float[] OpenEuler { get; }
        public string Source { get; }
        public string Ease { get; }
#nullable enable
        public float[]? Curve { get; }
        public float[]? At { get; }

        /// <summary>Switch rows only (null on a transform row): the node's active state while the gate is closed / once it is
        /// open. Both are set or both are null (the validators and the sidecar tests pin that).</summary>
        public bool? ClosedActive { get; }
        public bool? OpenActive { get; }
#nullable restore

        /// <summary>Is this row a switch (SetActive) rather than a transform pose?</summary>
        public bool IsSwitch => ClosedActive.HasValue;

        /// <summary>Number of keys of <see cref="Curve"/> (4 floats each); 0 when there is none.</summary>
        public int CurveKeyCount => Curve == null ? 0 : Curve.Length / 4;

        /// <summary>Is this pose for the barrier root instance at the world position (x, y, z)? A pose without <see cref="At"/>
        /// is for every instance of its path.</summary>
        public bool AppliesTo(float x, float y, float z)
        {
            if (At == null) return true;
            float dx = At[0] - x, dy = At[1] - y, dz = At[2] - z;
            return dx * dx + dy * dy + dz * dz <= AtTolerance * AtTolerance;
        }

#nullable enable
        public ArenaDoorPose(string Barrier, string Part, string Node, string Clip, float Seconds,
            float[] ClosedPosition, float[] ClosedEuler, float[] OpenPosition, float[] OpenEuler,
            string Source = SourceClip, string Ease = EaseSmoothstep, float[]? Curve = null, float[]? At = null,
            bool? ClosedActive = null, bool? OpenActive = null)
#nullable restore
        {
            this.Barrier = Barrier;
            this.Part = Part;
            this.Node = Node;
            this.Clip = Clip;
            this.Seconds = Seconds;
            this.ClosedPosition = ClosedPosition;
            this.ClosedEuler = ClosedEuler;
            this.OpenPosition = OpenPosition;
            this.OpenEuler = OpenEuler;
            this.Source = Source;
            this.Ease = Ease;
            this.Curve = Curve;
            this.At = At;
            this.ClosedActive = ClosedActive;
            this.OpenActive = OpenActive;
        }
    }

    /// <summary>
    /// One Arena TV prop whose <c>ArenaMonitorHolder</c> shows a mode's timer, score or alive count (tools/maps/&lt;stem&gt;.monitors.json, written by
    /// tools/extract/extract_score_monitors.py; docs/progress/score-monitors.md). <see cref="Path"/> is the PROP's hierarchy path (the holder's parent,
    /// "TV/Arena_Home_theater (3)") inside <see cref="Scene"/> (Equator's two DesignStuff scenes repeat the same paths). <see cref="At"/> is the holder's
    /// WORLD position in the scene data, the canvas centre: it tells apart a path that repeats inside one scene (Bowl's two "... SCORE" props) and is
    /// checked against the loaded prop (<see cref="AppliesTo"/>). <see cref="ArenaType"/> is the holder's raw type word: 0 timer, 1 score, 2 alive
    /// (confirmed by Bowl's prop names), 6 final score and 7 player board (inferred), 10 unknown; what the port draws for a word is
    /// <c>ScoreMonitorLayout.FaceOf</c>, and the sidecar's <c>face</c> says the same. <see cref="ActiveInScene"/> is the prop's activity in the imported scene
    /// data: only an active prop gets a screen (AirPit's two team-box screens and Bowl's inactive base set stay dark). The sidecar also carries the
    /// template name, the confirmed flag, the prop scale, the canvas size and the reader's side; the client reads those from the prop itself.
    /// </summary>
    public sealed class ArenaScoreMonitor
    {
        /// <summary>A prop whose mount point lies closer than this (metres) to <see cref="At"/> is the prop the row is for.</summary>
        public const float AtTolerance = 0.05f;

        public string Scene { get; }
        public string Path { get; }
        public float[] At { get; }
        public int ArenaType { get; }
        public bool ActiveInScene { get; }

        /// <summary>Is a prop whose <see cref="ArenaScoreMonitorMount"/> point stands at the world position (x, y, z) the one this row is for?</summary>
        public bool AppliesTo(float x, float y, float z)
        {
            float dx = At[0] - x, dy = At[1] - y, dz = At[2] - z;
            return dx * dx + dy * dy + dz * dz <= AtTolerance * AtTolerance;
        }

        public ArenaScoreMonitor(string Scene, string Path, float[] At, int ArenaType, bool ActiveInScene)
        {
            this.Scene = Scene;
            this.Path = Path;
            this.At = At;
            this.ArenaType = ArenaType;
            this.ActiveInScene = ActiveInScene;
        }
    }

    /// <summary>
    /// The one pose every <c>ArenaMonitorHolder</c> has below its TV prop (43 of 43 holders of the four maps agree; the sidecars' <c>mount</c> block):
    /// prop-local position and rotation (quaternion x, y, z, w), the uniform local scale and the canvas rect in canvas pixels, pivot in the middle. The
    /// canvas is readable from the prop's +X side (its forward, local +Z, is the prop's -X). World size = prop scale x <see cref="Scale"/> x rect.
    /// </summary>
    public static class ArenaScoreMonitorMount
    {
        public static readonly float[] Position = { 0f, 0f, 0.369285f };
        public static readonly float[] Rotation = { -0.5f, 0.5f, 0.5f, -0.5f };
        public const float Scale = 0.00054f;
        public const float Width = 1930f;
        public const float Height = 1163f;
        public const float PivotX = 0.5f;
        public const float PivotY = 0.5f;
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
        // Spawn gates (SpawnGates.cs, 2026-10-17). StartGates: the OpenedBarriers a mode plugin may hold closed through
        // SpawnGates (offered to it before the raid-start opening switches them off); empty = every opened barrier is
        // offered (Equator, Bay5). HeldGates: OpenedBarriers that stay CLOSED at raid start in every mode, sealed for
        // bots by SpawnGates' carvers from the NavMesh install on, and opened only by SpawnGates.Release (AirPit's
        // passport control, the north team box's gate). Both are subsets of OpenedBarriers, so the bake leaves them out.
        public string[] StartGates { get; }
        public string[] HeldGates { get; }
        // Bowl (2026-10-18, docs/PLAN-BOWL.md): Arena bounds the stadium with 354 layer-29 LevelBorder cubes and
        // has no data polygon, so the cubes are the only play-area bound. true keeps them: MapBorderRepairs leaves
        // their colliders enabled (its RemoveTeamFightBorders key defaults to false for this map) and BakeNavMesh
        // treats layer 29 as physical (the map's navmesh_bake_layers must list 29). Equator, Bay5 and AirPit keep
        // false: their TeamFight walls open at raid start and the bake drops layer 29.
        public bool KeepLevelBorders { get; }
        // Door poses (2026-10-22 Bowl, 2026-10-23 Equator and Bay5, docs/progress/door-poses.md): the open END pose of every
        // node Arena's gates move (Bowl: Mecanim clips; Equator and Bay5: the DotAnimator script, with the duration and the
        // ease), so the raid-start opening can park a gate part in its open position instead of hiding it. Empty = the
        // map's barriers are hidden as before. Mirrors tools/maps/<stem>.doors.json. Look poses up with DoorPosesOf /
        // DoorPosesOfBarrier; where a hierarchy path repeats they need the barrier root's world position.
        public ArenaDoorPose[] DoorPoses { get; }
        // Score monitors (2026-10-25, docs/progress/score-monitors.md): the TV props of Arena's design scenes whose ArenaMonitorHolder shows a mode's
        // timer, score or alive count. The client draws its own face on every ACTIVE row's prop while a mode plugin has claimed the monitors
        // (ScoreMonitors); empty = the map has none. Mirrors tools/maps/<stem>.monitors.json.
        public ArenaScoreMonitor[] ScoreMonitors { get; }
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
        // Puddle footsteps (PuddleFootstepRepairs, docs/progress/puddle-footsteps-all-maps.md): the repair turns a native ground
        // result of Concrete/Asphalt into the Puddle set inside the visible mask of the listed water materials. PuddleExtraSurfaces
        // names further BaseBallistic.ESurfaceSound members (as text, this project does not reference EFT) that may be replaced
        // the same way; empty = exactly Concrete/Asphalt (Bay5, Equator, Air Pit). Bowl lists "Soil": its whole field is one
        // Bowl_floor_BALLISTIC_soil collider, so with the Concrete/Asphalt rule alone its puddles would stay silent.
        public string[] PuddleExtraSurfaces { get; }
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
        public ArenaLampFillDescriptor? LampFill { get; } // empty ceiling-lamp cells filled at raid start; null = none (AirPit only)
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
            string[]? PuddleExtraSurfaces = null,
            string[]? DistantShadowCasterPaths = null,
            bool InheritIndoorFloorDetails = false,
            ArenaNeonFixture[]? NeonFixtures = null,
            ArenaNightAccent[]? NightAccents = null,
            bool UsesServerTime = false,
            string[]? NavMeshBakeExclusions = null,
            float[]? BakeBounds = null,
            int[]? NavMeshBakeLayers = null,
            string[]? ShaderFallbackNames = null,
            ArenaBorderDescriptor[]? AuthoredBorders = null,
            ArenaLampFillDescriptor? LampFill = null,
            string[]? StartGates = null,
            string[]? HeldGates = null,
            bool KeepLevelBorders = false,
            ArenaDoorPose[]? DoorPoses = null,
            ArenaScoreMonitor[]? ScoreMonitors = null)
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
            this.StartGates = StartGates ?? new string[0];
            this.HeldGates = HeldGates ?? new string[0];
            this.KeepLevelBorders = KeepLevelBorders;
            this.DoorPoses = DoorPoses ?? new ArenaDoorPose[0];
            this.ScoreMonitors = ScoreMonitors ?? new ArenaScoreMonitor[0];
            this.NavMeshBakeExclusions = NavMeshBakeExclusions ?? new string[0];
            this.AuthoredBorders = AuthoredBorders ?? new ArenaBorderDescriptor[0];
            this.BakeBounds = BakeBounds ?? new float[0];
            this.NavMeshBakeLayers = NavMeshBakeLayers ?? new int[0];
            this.BackgroundSceneNames = BackgroundSceneNames ?? new string[0];
            this.RestoredShadowCasterRoots = RestoredShadowCasterRoots ?? new string[0];
            this.DistantShadowCasterPaths = DistantShadowCasterPaths ?? new string[0];
            this.Dust = Dust;
            this.LampFill = LampFill;
            this.FixedDayHour = FixedDayHour;
            this.FixedNightHour = FixedNightHour;
            this.UsesServerTime = UsesServerTime;
            this.SunScene = SunScene ?? string.Empty;
            this.SunPaths = SunPaths ?? new string[0];
            this.PuddleWaterMaterialNames = PuddleWaterMaterialNames ?? new string[0];
            this.PuddleExtraSurfaces = PuddleExtraSurfaces ?? new string[0];
            this.ShaderFallbackNames = ShaderFallbackNames ?? new string[0];
            this.InheritIndoorFloorDetails = InheritIndoorFloorDetails;
            this.NeonFixtures = NeonFixtures ?? new ArenaNeonFixture[0];
            this.NightAccents = NightAccents ?? new ArenaNightAccent[0];
        }

        public bool IsBackgroundScene(string sceneName) =>
            System.Array.IndexOf(BackgroundSceneNames, sceneName) >= 0;

        /// <summary>How the raid-start opening treats the OpenedBarriers entry <paramref name="path"/>: Held = stays
        /// closed until SpawnGates.Release; Start = offered to SpawnGates (held closed only on a mode plugin's Hold),
        /// which every opened barrier is when StartGates is empty; Plain = switched off at raid start, never offered.</summary>
        public ArenaGateRole GateRoleOf(string path)
        {
            if (System.Array.IndexOf(HeldGates, path) >= 0) return ArenaGateRole.Held;
            if (StartGates.Length == 0 || System.Array.IndexOf(StartGates, path) >= 0) return ArenaGateRole.Start;
            return ArenaGateRole.Plain;
        }

        /// <summary>The poses of one gate part (the barrier path and the part name BoothDoorRepairs sees) that hold for EVERY
        /// instance of the barrier path (<see cref="ArenaDoorPose.At"/> == null), in sidecar order: the part itself first
        /// (Node == ""), then the animated nodes below it. Empty when Arena animates nothing there. A pose with an
        /// <c>At</c> is only returned by the overload that names the barrier root's world position.</summary>
        public ArenaDoorPose[] DoorPosesOf(string barrier, string part) => Select(barrier, part, false, 0f, 0f, 0f);

        /// <summary>The poses of one gate part of the barrier root instance standing at the world position (x, y, z): the poses
        /// without an <c>At</c> and those whose <c>At</c> lies within <see cref="ArenaDoorPose.AtTolerance"/> of it. Use this
        /// where a hierarchy path repeats (Equator's booth doors, Bay5's depot gates). part "" = the barrier root itself.</summary>
        public ArenaDoorPose[] DoorPosesOf(string barrier, string part, float x, float y, float z) => Select(barrier, part, true, x, y, z);

        /// <summary>Every pose of a barrier (any part) that holds for every instance of the path, in sidecar order. Non-empty
        /// means Arena animates something of this barrier: its parts without a pose of their own are structure that stays.</summary>
        public ArenaDoorPose[] DoorPosesOfBarrier(string barrier) => Select(barrier, null, false, 0f, 0f, 0f);

        /// <summary>Every pose of the barrier root instance at the world position (x, y, z), any part, in sidecar order.</summary>
        public ArenaDoorPose[] DoorPosesOfBarrier(string barrier, float x, float y, float z) => Select(barrier, null, true, x, y, z);

#nullable enable
        private ArenaDoorPose[] Select(string barrier, string? part, bool positioned, float x, float y, float z)
#nullable restore
        {
            var result = new List<ArenaDoorPose>();
            foreach (ArenaDoorPose pose in DoorPoses)
            {
                if (pose.Barrier != barrier) continue;
                if (part != null && pose.Part != part) continue;
                if (positioned ? !pose.AppliesTo(x, y, z) : pose.At != null) continue;
                result.Add(pose);
            }
            return result.ToArray();
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
            LocaleName: "Arena: Equator",
            LocaleDescription: "The Equator shopping mall from EFT Arena. Ground floor playable; the upper rings are scenery.",
            IconX: 788, IconY: 521,
            DbBaseJsonPath: "arena_equator/base.json",
            Exits: new[]
            {
                // exfils-stage2 — ground-floor edges beyond the player spawns (Usec/All spawn north at z 38–49,
                // Bear south at z -33.5); mirrors tools/maps/equator.exits.json, guarded by
                // BaseJsonTests.Exits_DescriptorMirrorsSidecar. Human-readable names double as locale keys.
                // Host test 1 (20.09.): the floor ends at z ≈ 49.6 / -44; the spawn booths are NavMesh islands
                // (physically open), and a box within 5 m of a spawn point would extract a fresh spawn — so the
                // gates sit on the far side of each spawn zone, 10 m from the nearest spawn.
                // 2026-10-23 (docs/progress/equator-booth-spawns.md): the player spawns now stand on Arena's own markers INSIDE
                // the booths (north z 45.4 and 48.6, south z -39.1), so the boxes share the booths with them. The nearest spawn
                // point is 2.6 m outside a box (Usec, North Gate) and EFT places a player on the point itself, so nobody starts
                // inside a trigger; walking out through the door crosses a box in well under its 8 s.
                new ArenaExitDescriptor("Equator North Gate", 2.0f, 0.1f, 46.0f),
                new ArenaExitDescriptor("Equator South Gate", 5.0f, 0.1f, -44.0f),
            },
            // No opened barriers (2026-10-23, user, docs/progress/equator-booth-spawns.md). Until then the roller shutter
            // "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)" at (-9.7, 0, 36.0) was opened because the Usec
            // spawn (-11.7, 48.6) stood inside the FCK hall behind it. Arena never opens it (LevelBorder (83) walls it off;
            // all 20 of its spawn markers lie in the two booths), so the shutter is a baked wall again, the spawns sit on
            // Arena's own markers behind the booth doors and the FCK hall is outside the play area. The booth doors are not
            // barriers: BoothDoorRepairs opens them by the name of their leaves (DoorPoses below).
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
            PuddleWaterMaterialNames: new[]
            {
                // Both are the original water decals of the mall floor (Decal/Water Deferred Decal, 17 + 21 active quads in
                // Arena_Equator): "puddle indoor static" (a fixed water level) and the rain-driven "puddle" (USERAIN, global
                // _WaterLevel). Equator has no oil decal. Mirrors tools/maps/equator.json (2026-10-25).
                "70f045bb011600e7",
                "0877f2335044042e",
            },
            DistantShadowCasterPaths: new[]
            {
                // Original active ceiling proxies lack LOD membership required by EFT DistantShadow.
                // Floor 04 already has it. Keep original apertures, materials and meshes.
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_01/equator_floor_01_SHADOW_LOD0",
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_02/equator_floor_02_SHADOW_LOD0",
                "SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors/equator_floor_03/equator_floor_03_SHADOW_LOD0"
            },
            // Arena's sliding booth doors (2026-10-23, tools/maps/equator.doors.json, docs/progress/door-poses.md "DotAnimator"):
            // the four assemblies under SPAWN_DOORS (their DotAnimator end poses; the two unnumbered ones are told apart by
            // the world position of their root) plus the fifth door under Respawn_2, which Arena gives no motion (borrowed).
            // Since 2026-10-25 the sidecar's first eight rows are the exit lamps (a LAMP GROUP, EXIT_LIGHT_EMISSIVE: switch rows
            // only, no opened_barriers entry): the two Point lights of each of the four green emergency-exit lamps are off in the
            // scene and on once the gates are open (Arena's DotAnimator, duration 0; door-poses.md "Exit lamps").
            DoorPoses: new[]
            {
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (1)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (1)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (2)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (2)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (4)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (4)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (5)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("EXIT_LIGHT_EMISSIVE", "emergency_exit_light_emissive (5)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)", "Door_Sliding_Respawn_L", "", "borrowed from SPAWN_DOORS/Door_Sliding_Respawn (1)", 5.0f,
                    new[] { 0.683f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "borrowed", "inOutQuint", null, new[] { -3.753f, 0.048f, -45.87f }),
                new ArenaDoorPose("SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)", "Door_Sliding_Respawn_R", "", "borrowed from SPAWN_DOORS/Door_Sliding_Respawn (1)", 5.0f,
                    new[] { -0.686f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "borrowed", "inOutQuint", null, new[] { -3.753f, 0.048f, -45.87f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_L", "", "DotAnimator", 5.0f,
                    new[] { 0.717f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { -4.328f, 0.048f, 40.02f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_R", "", "DotAnimator", 5.0f,
                    new[] { -0.709f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { -4.328f, 0.048f, 40.02f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_L", "", "DotAnimator", 5.0f,
                    new[] { 0.709f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { 4.535f, 0.048f, 40.02f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_R", "", "DotAnimator", 7.0f,
                    new[] { -0.722f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { 4.535f, 0.048f, 40.02f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn (1)", "Door_Sliding_Respawn_L", "", "DotAnimator", 5.0f,
                    new[] { 0.711f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { -4.46f, 0.048f, -35.045f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn (1)", "Door_Sliding_Respawn_R", "", "DotAnimator", 5.0f,
                    new[] { -0.722f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { -4.46f, 0.048f, -35.045f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn (2)", "Door_Sliding_Respawn_L", "", "DotAnimator", 5.0f,
                    new[] { 0.714f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { 4.403f, 0.048f, -35.045f }),
                new ArenaDoorPose("SPAWN_DOORS/Door_Sliding_Respawn (2)", "Door_Sliding_Respawn_R", "", "DotAnimator", 5.0f,
                    new[] { -0.706f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, new[] { 4.403f, 0.048f, -35.045f }),
            },
            // Score monitors (2026-10-25, tools/maps/equator.monitors.json, docs/progress/score-monitors.md): the 11 TV props of the two design scenes,
            // 7 in Arena_Equator_DesignStuff (three on the west atrium wall, two on the east one, a score screen in each booth) and 4 in
            // Arena_Equator_TeamFight_DesignStuff (two final-score boards, two player boards). All 11 are active; the two player boards (type 7)
            // get no face and stay dark.
            ScoreMonitors: new[]
            {
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater", new[] { -17.848f, 10.3089f, -1.496f }, 1, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (1)", new[] { -17.848f, 10.3089f, 3.25f }, 0, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (2)", new[] { -17.848f, 10.3089f, 7.75f }, 2, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (3)", new[] { 17.265f, 10.3699f, -1.343f }, 2, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (4)", new[] { 17.265f, 10.3191f, 7.6757f }, 0, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (5)", new[] { 0.104f, 2.2133f, 40.6855f }, 1, true),
                new ArenaScoreMonitor("Arena_Equator_DesignStuff", "TV/Arena_Home_theater (6)", new[] { 0.551f, 2.2275f, -35.133f }, 1, true),
                new ArenaScoreMonitor("Arena_Equator_TeamFight_DesignStuff", "TV/Arena_Home_theater (5)", new[] { 17.6673f, 11.568f, 4.027f }, 7, true),
                new ArenaScoreMonitor("Arena_Equator_TeamFight_DesignStuff", "TV/Arena_Home_theater (6)", new[] { 27.177f, 12.3886f, 1.612f }, 6, true),
                new ArenaScoreMonitor("Arena_Equator_TeamFight_DesignStuff", "TV/Arena_Home_theater (7)", new[] { -18.334f, 11.7685f, 1.362f }, 7, true),
                new ArenaScoreMonitor("Arena_Equator_TeamFight_DesignStuff", "TV/Arena_Home_theater (8)", new[] { -26.405f, 12.7166f, 4.903f }, 6, true),
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
            IconX: 469, IconY: 92,
            DbBaseJsonPath: "arena_bay5/base.json",
            Exits: new[]
            {
                new ArenaExitDescriptor("Bay5 North Loading Bay", 96.0f, -2.9252f, 61.0f),
                new ArenaExitDescriptor("Bay5 South Container Lane", 99.029f, -3.8574f, -2.8513f),
            },
            OpenedBarriers: new[]
            {
                // Every gate Arena moves (2026-10-23, user: "alle Tore aufmachen"; docs/progress/equator-booth-spawns.md): the
                // three warehouse rollers (Door_01 (2), (4) and One_man_Door) and the two depot gates. Door_01 (3) is NOT here
                // (user decision 2026-10-24): Arena authors no motion and no sound for it and a tarp with a carton stack stands in
                // its doorway, so it stays shut like Door_01 and Door_01 (1) (a baked wall again). The depot path exists TWICE as
                // sibling objects (go 821 at world (50.574, -3.895, 7.525) and go 822 at (69.045, -3.883, -6.317)) and is listed
                // ONCE: the client and the bake open every instance of a path (DoorPosesOf(barrier, part, x, y, z) tells them
                // apart by `at`).
                "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)",
                "WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)",
                "WAREHOUSE_DOORS (1)/One_man_Door",
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
            UsesServerTime: true,
            // Arena's roller gates and depot gates (2026-10-23, tools/maps/bay5.doors.json): the DotAnimator end poses of the
            // warehouse rollers (the whole root lifts, part "") and the swing of both depot gates' leaves (told apart by the
            // world position of the Gates_Depot_Frame root). Seven rows; Door_01 (3) has none (shut, 2026-10-24).
            DoorPoses: new[]
            {
                new ArenaDoorPose("OLD_WAREHOUSE_DOORS/Gates_Depot_Frame", "Gates_Depot_Door_L", "Gates_Depot_Door_L_Leaf", "DotAnimator", 7.0f,
                    new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 3.8f },
                    new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 162.432f },
                    "dotanimator", "curve", new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.79929f, 0.89601f, 0.89601f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 50.574f, -3.895f, 7.525f }),
                new ArenaDoorPose("OLD_WAREHOUSE_DOORS/Gates_Depot_Frame", "Gates_Depot_Door_R", "Gates_Depot_Door_R_Leaf", "DotAnimator", 7.0f,
                    new[] { -2.432f, -0.115f, 0.282f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -2.432f, -0.115f, 0.282f }, new[] { 0.0f, 0.0f, -141.759f },
                    "dotanimator", "curve", new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.8f, 0.88513f, 0.88513f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 50.574f, -3.895f, 7.525f }),
                new ArenaDoorPose("OLD_WAREHOUSE_DOORS/Gates_Depot_Frame", "Gates_Depot_Door_L", "Gates_Depot_Door_L_Leaf", "DotAnimator", 7.0f,
                    new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 2.377f, -0.112f, 0.282f }, new[] { 0.0f, 0.0f, 138.837f },
                    "dotanimator", "curve", new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.80016f, 0.99404f, 0.99404f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 69.045f, -3.883f, -6.317f }),
                new ArenaDoorPose("OLD_WAREHOUSE_DOORS/Gates_Depot_Frame", "Gates_Depot_Door_R", "Gates_Depot_Door_R_Leaf", "DotAnimator", 7.0f,
                    new[] { -2.432f, -0.115f, 0.282f }, new[] { 0.0f, 0.0f, -2.042f },
                    new[] { -2.432f, -0.115f, 0.282f }, new[] { 0.0f, 0.0f, -98.974f },
                    "dotanimator", "curve", new[] { 0.0f, 0.0f, 2.0f, 2.0f, 0.4f, 0.80194f, 0.88599f, 0.88599f, 1.0f, 1.0f, 0.0f, 0.0f }, new[] { 69.045f, -3.883f, -6.317f }),
                new ArenaDoorPose("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)", "", "", "DotAnimator", 4.0f,
                    new[] { 9.822f, 0.13f, -27.705f }, new[] { -90.0f, -90.0f, 0.0f },
                    new[] { 9.822f, 3.222f, -27.705f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "linear", null, null),
                new ArenaDoorPose("WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)", "", "", "DotAnimator", 4.0f,
                    new[] { -6.227f, 0.124f, -21.562f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { -6.227f, 3.213f, -21.562f }, new[] { -90.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null),
                new ArenaDoorPose("WAREHOUSE_DOORS (1)/One_man_Door", "", "", "DotAnimator", 4.0f,
                    new[] { -6.076f, 0.15f, 0.858f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -6.076f, 3.323f, 0.858f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null),
            },
            // Score monitors (2026-10-25, tools/maps/bay5.monitors.json, docs/progress/score-monitors.md): the 8 TV props of
            // Arena_Bay5_TeamFight_DesignStuff (3 timers, 2 scores, 3 alive counts), all active.
            ScoreMonitors: new[]
            {
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (5)", new[] { 105.79f, 10.3899f, -16.741f }, 0, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (7)", new[] { 67.183f, 5.1336f, 24.203f }, 0, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (9)", new[] { 130.5764f, 1.8432f, 71.0636f }, 1, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (14)", new[] { 61.788f, 5.3759f, 67.848f }, 0, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (16)", new[] { 98.159f, 6.8189f, -14.178f }, 2, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (18)", new[] { 61.1756f, 4.1859f, 44.536f }, 2, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (22)", new[] { 113.448f, 2.3362f, 35.697f }, 2, true),
                new ArenaScoreMonitor("Arena_Bay5_TeamFight_DesignStuff", "TV/Arena_Home_theater (24)", new[] { 67.63f, -1.1154f, -10.065f }, 1, true),
            });

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
            // Original Arena screenshot pin, converted to native 0..1000 anchors.
            // Provenance and the other measured maps: tools/maps/selection/markers.json.
            IconX: 792,
            IconY: 212,
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
                // frame-named object, so every direct child is a gate part and every collider below is left out of
                // the bake. BUS_DOORS: 6 bus-door assemblies sealing the south spawn pocket (opened at raid start unless
                // held); PASSPORT_CONTROL_DOORS: 16 lane turnstiles sealing the north spawn pocket plus the GREEN/RED
                // lane-light groups; PASSPORT_CONTROL_EMISSIVE: the 18 red lane sheets (held gates, see below).
                // Mirrors tools/maps/airpit.json (AirPitMapTests.OpenedBarriersAndBakeInputsMatchTheSidecar).
                "BUS_DOORS",
                // PASSPORT_CONTROL_DOORS + PASSPORT_CONTROL_EMISSIVE are the HeldGates (2026-10-17, the original north
                // team box): the 16 lane turnstiles (1.09 x 1.35 m, eight lanes at z 66.3 and 69.1) stay closed at
                // raid start in every mode and open only on SpawnGates.Release, together with the 18 red lane sheets.
                // Since package v8 the furnished passport booth row (Arena_Airpit_Main_hall PASSPORT_CONTROL, layer-18
                // colliders x -31.35..8.37, z 66.0..69.4) fills the floor between the lanes, so the turnstiles are the
                // only way through (host raid 5 of 2026-10-12 saw open floor there on package v6). 2a1e0ce's "the
                // closed passport control is the north limit" stays true for plain raids; SpawnGates' carvers now keep
                // bots out of the meshed pocket instead of the bake box. The north player and bot spawns stay south of
                // the gate line (tools/maps/airpit.bots.json).
                "PASSPORT_CONTROL_DOORS",
                "PASSPORT_CONTROL_EMISSIVE",
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
            // Only the bus doors are a start gate a mode plugin may hold; the portal fences, shop shells and blockout
            // panels are plain openings that must not come back during a hold. The passport control is the held gate.
            StartGates: new[] { "BUS_DOORS" },
            HeldGates: new[] { "PASSPORT_CONTROL_DOORS", "PASSPORT_CONTROL_EMISSIVE" },
            // Puddle footsteps (2026-10-25): "Airpit_puddle_white" (static, 14 active quads) and the rain-driven "puddle" (USERAIN,
            // 26 active quads). The third water material of the extract, "City_puddle_atlas_OUTDOOR" (823c859e2c5d11bc), has only
            // two inactive runway decals off the map and stays out. Mirrors tools/maps/airpit.json.
            PuddleWaterMaterialNames: new[] { "1b8546c701e1d1dc", "0877f2335044042e" },
            // No NavMeshBakeExclusions: the four bake-only cutters of 2026-10-11 (DonB_Ramen_closed x2,
            // TH_Roll_door_01_closed, fence_ema_01_white (18)) let the NavMesh run through shutters that stay solid;
            // host raid 1 walked the player into the DonB_Ramen shutter at (-46.2, 0, 43.4). Every solid is baked.
            // Play space: the AABB of the authored borders below (Arena's TeamFight outline, x -56.8..36.2,
            // z -82.5..83.6), y -2..8 = ground floor plus wing mezzanine (user decision 2026-10-11, 4.3). From
            // 2026-10-12 (2a1e0ce) to 2026-10-17 the box ended at z 70 to keep bots out of the north spawn pocket;
            // the original north team box (SPAWN_BOUND_FIGHTER z 69.16..85.16) needs the pocket meshed, so the box is
            // the outline's AABB again and the held passport gate seals the pocket for bots instead. The 2026-10-11
            // box (x -80..56, z -96..92) baked 7 m of the dark entrance hall and strips of runway and forecourt
            // (host raids 1-4, 2026-10-12); z 83 stops 0.5 m past the facade-gap wall (z 82.5).
            BakeBounds: new[] { -57.0f, 37.0f, -2.0f, 8.0f, -83.0f, 83.0f },
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
            // Passport-hall ring lamps (LampFillRepairs, user decision 2026-10-13: "the map is our own interpretation anyway;
            // fill the ceiling, it looks broken; only the lamps that are lit now stay lit, all additional ones OFF").
            // Arena 0.6.0 level37 authors 45 `Airport_lamp_02` units on a 17 x 9 lattice over the passport hall (pivots at
            // y 6.83; columns x -36.95 .. 13.81, rows z 56.44 .. 83.01); the dense ceiling of the older build is gone. The
            // 82 cells below are the lattice cells that hold no authored lamp, lie 3.0 m or more from every one and sit
            // fully under the flat ceiling plane of airport_passport_control (the 26 cells at the curved flanks and the
            // north edge stay empty). (-30.37, 73.13) is filled on the user's decision (2026-10-13 midday) although its
            // 1.28 m lamp disc dips 0.13 m into the shaft of the round column Airport_column_02 at (-32.44, 73.18): the
            // mirrored column at (10.05, 73.34) has the lattice cell (7.42, 73.13) filled right beside it and the user
            // judged that look fine. Each cell gets a copy of the authored dark lamp (`_off` emissive material), one row
            // of the lattice per group, x then z. Mirrors tools/maps/airpit.json (AirPitMapTests.LampFillMatchesTheSidecar).
            LampFill: new ArenaLampFillDescriptor(
                "SBG_Airpit_Light/OO/Airport_lamp_02/Airport_lamp_02_disabled",
                6.83f,
                new[]
                {
                    // row z 56.44: 12 cell(s)
                    -33.65f, 56.44f, -30.37f, 56.44f, -27.29f, 56.44f, -24.13f, 56.44f,
                    -17.87f, 56.44f, -14.71f, 56.44f, -11.57f, 56.44f, -8.43f, 56.44f,
                    -5.21f, 56.44f, 1.11f, 56.44f, 7.42f, 56.44f, 10.64f, 56.44f,
                    // row z 59.72: 12 cell(s)
                    -36.95f, 59.72f, -33.65f, 59.72f, -27.29f, 59.72f, -21.01f, 59.72f,
                    -17.87f, 59.72f, -14.71f, 59.72f, -11.57f, 59.72f, -8.43f, 59.72f,
                    1.11f, 59.72f, 4.28f, 59.72f, 10.64f, 59.72f, 13.81f, 59.72f,
                    // row z 63.13: 11 cell(s)
                    -33.65f, 63.13f, -30.37f, 63.13f, -27.29f, 63.13f, -24.13f, 63.13f,
                    -17.87f, 63.13f, -14.71f, 63.13f, -8.43f, 63.13f, -5.21f, 63.13f,
                    -2.14f, 63.13f, 1.11f, 63.13f, 4.28f, 63.13f,
                    // row z 66.38: 11 cell(s)
                    -33.65f, 66.38f, -30.37f, 66.38f, -24.13f, 66.38f, -21.01f, 66.38f,
                    -14.71f, 66.38f, -8.43f, 66.38f, -5.21f, 66.38f, -2.14f, 66.38f,
                    7.42f, 66.38f, 10.64f, 66.38f, 13.81f, 66.38f,
                    // row z 69.70: 7 cell(s)
                    -27.29f, 69.7f, -17.87f, 69.7f, -14.71f, 69.7f, -11.57f, 69.7f,
                    -5.21f, 69.7f, 1.11f, 69.7f, 10.64f, 69.7f,
                    // row z 73.13: 10 cell(s)
                    -30.37f, 73.13f, -24.13f, 73.13f, -21.01f, 73.13f, -17.87f, 73.13f,
                    -11.57f, 73.13f, -8.43f, 73.13f, -5.21f, 73.13f, 1.11f, 73.13f,
                    4.28f, 73.13f, 7.42f, 73.13f,
                    // row z 76.47: 7 cell(s)
                    -30.37f, 76.47f, -27.29f, 76.47f, -24.13f, 76.47f, -17.87f, 76.47f,
                    -2.14f, 76.47f, 4.28f, 76.47f, 7.42f, 76.47f,
                    // row z 79.76: 7 cell(s)
                    -30.37f, 79.76f, -17.87f, 79.76f, -14.71f, 79.76f, -5.21f, 79.76f,
                    -2.14f, 79.76f, 1.11f, 79.76f, 7.42f, 79.76f,
                    // row z 83.01: 5 cell(s)
                    -21.01f, 83.01f, -14.71f, 83.01f, -11.57f, 83.01f, -5.21f, 83.01f,
                    -2.14f, 83.01f,
                }),
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
            UsesServerTime: true,
            // Arena's own gate motions (2026-10-24, tools/maps/airpit.doors.json, docs/progress/door-poses.md "AirPit"; thirty
            // rows, in the sidecar's order). BUS_DOORS: the three front-row bus assemblies (Arena authors no clip and no duration;
            // its sound level 39 carries sound players for exactly these three, but no bus clip is installed) open to the pose the
            // STATIC back row already stands in, hinge and leaf of each door, 3.0 s
            // (the port's seconds). PASSPORT_CONTROL_DOORS: the 16 turnstile leaves turn about 90 degrees about the vertical
            // axis in Arena's 1.7 s (InOutQuint); their names repeat (6 names for 16 leaves), so the client picks the rows of a
            // leaf by the closed position; RED / GREEN carry the switch rows of the lamp flip (RedFog off, GreenFog on, the
            // ClosedActive / OpenActive arguments). Since 2026-10-25 thirty-six rows: BUS_LIGHT (between the two) is a LAMP
            // GROUP of switch rows only, the two Point lights of each of the bus's three green exit lamps (off in the scene, on
            // once the gates are open, duration 0).
            DoorPoses: new[]
            {
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors", "Apron_bus_MAZ_door_L_01_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors", "Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, -0.404f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, -0.328f, -1.78f }, new[] { 0.24f, -0.181f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors", "Apron_bus_MAZ_door_L_02_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors", "Apron_bus_MAZ_door_L_02_part_01/Apron_bus_MAZ_door_L_02", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, 0.403f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, 0.328f, -1.78f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_door_L_01_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, -0.404f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, -0.328f, -1.78f }, new[] { 0.24f, -0.181f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_door_L_02_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_door_L_02_part_01/Apron_bus_MAZ_door_L_02", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, 0.403f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, 0.328f, -1.78f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (2)", "Apron_bus_MAZ_door_L_01_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, 0.729f, 2.152f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (2)", "Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, -0.404f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, -0.328f, -1.78f }, new[] { 0.24f, -0.181f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (2)", "Apron_bus_MAZ_door_L_02_part_01", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.129f, -0.728f, 2.152f }, new[] { 0.0f, 0.0f, -127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_DOORS", "Apron_bus_MAZ_doors (2)", "Apron_bus_MAZ_door_L_02_part_01/Apron_bus_MAZ_door_L_02", "borrowed from the static open pose of BUS_DOORS/Apron_bus_MAZ_doors (3); seconds are the port's", 3.0f,
                    new[] { 0.01f, 0.403f, -1.78f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.076f, 0.328f, -1.78f }, new[] { 0.0f, 0.0f, 127.0f },
                    "borrowed", "inOutQuint", null, null),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive (1)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive (1)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive (2)", "emergency_exit_light 1/Point light", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.228f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("BUS_LIGHT", "emergency_exit_light_emissive (2)", "emergency_exit_light 1/Point light (1)", "DotAnimator", 0.0f,
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, -0.163f, 1.307f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "GREEN", "GreenFog", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: false, OpenActive: true),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (1)", "", "DotAnimator", 1.7f,
                    new[] { -0.481f, -3.23f, 3.099f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { -0.481f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (1)", "", "DotAnimator", 1.7f,
                    new[] { 4.483f, -3.23f, 3.099f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 4.483f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (1)", "", "DotAnimator", 1.7f,
                    new[] { 9.446f, -3.23f, 3.099f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 9.446f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (2)", "", "DotAnimator", 1.7f,
                    new[] { -0.481f, -3.23f, 5.938f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { -0.481f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (2)", "", "DotAnimator", 1.7f,
                    new[] { 4.483f, -3.23f, 5.938f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 4.483f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (2)", "", "DotAnimator", 1.7f,
                    new[] { 9.446f, -3.23f, 5.938f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 9.446f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (3)", "", "DotAnimator", 1.7f,
                    new[] { -5.089f, -3.23f, 3.099f }, new[] { -90.0f, 179.0f, 0.0f },
                    new[] { -5.089f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (3)", "", "DotAnimator", 1.7f,
                    new[] { -0.125f, -3.23f, 3.099f }, new[] { -90.0f, 179.0f, 0.0f },
                    new[] { -0.125f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (3)", "", "DotAnimator", 1.7f,
                    new[] { 4.838f, -3.23f, 3.099f }, new[] { -90.0f, 179.0f, 0.0f },
                    new[] { 4.838f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (3)", "", "DotAnimator", 1.7f,
                    new[] { 9.802f, -3.23f, 3.099f }, new[] { -90.0f, 179.0f, 0.0f },
                    new[] { 9.802f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", "", "DotAnimator", 1.7f,
                    new[] { -5.089f, -3.23f, 5.938f }, new[] { -90.0f, -179.0f, 0.0f },
                    new[] { -5.089f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", "", "DotAnimator", 1.7f,
                    new[] { -0.125f, -3.23f, 5.938f }, new[] { -90.0f, -179.0f, 0.0f },
                    new[] { -0.125f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", "", "DotAnimator", 1.7f,
                    new[] { 4.838f, -3.23f, 5.938f }, new[] { -90.0f, -179.0f, 0.0f },
                    new[] { 4.838f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (4)", "", "DotAnimator", 1.7f,
                    new[] { 9.802f, -3.23f, 5.938f }, new[] { -90.0f, -179.0f, 0.0f },
                    new[] { 9.802f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (5)", "", "DotAnimator", 1.7f,
                    new[] { 19.37f, -3.23f, 3.099f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 19.37f, -3.23f, 3.099f }, new[] { -90.0f, 90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "Passport_control_01_Door (6)", "", "DotAnimator", 1.7f,
                    new[] { 19.37f, -3.23f, 5.938f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 19.37f, -3.23f, 5.938f }, new[] { -90.0f, -90.0f, 0.0f },
                    "dotanimator", "inOutQuint", null, null),
                new ArenaDoorPose("PASSPORT_CONTROL_DOORS", "RED", "RedFog", "DotAnimator", 0.0f,
                    new[] { 0.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f },
                    "dotanimator", "linear", null, null, ClosedActive: true, OpenActive: false),
            },
            // Score monitors (2026-10-25, tools/maps/airpit.monitors.json, docs/progress/score-monitors.md): the 8 TV props of Arena_Airpit_DesignStuff.
            // The two team-box screens (9 north, 11 south) are inactive in the scene data and stay dark, so 6 screens (2 timers, 2 scores,
            // 2 alive counts) get a face.
            ScoreMonitors: new[]
            {
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater", new[] { -33.444f, 7.4179f, -6.44f }, 2, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (1)", new[] { -43.9198f, 8.9279f, 2.24f }, 0, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (2)", new[] { -34.5398f, 7.4179f, 14.7902f }, 1, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (3)", new[] { 9.3982f, 7.4179f, 5.345f }, 2, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (4)", new[] { 19.9602f, 8.9279f, -3.34f }, 0, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (5)", new[] { 10.3432f, 7.4179f, -16.3198f }, 1, true),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (9)", new[] { -31.6808f, 3.9988f, 77.6882f }, 1, false),
                new ArenaScoreMonitor("Arena_Airpit_DesignStuff", "TV/Arena_Home_theater (11)", new[] { -12.6215f, 1.6693f, -68.3997f }, 1, false),
            });

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
            IconX: 407,
            IconY: 471,
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
            LocaleName: "Arena: Bowl",
            LocaleDescription: "The Bowl from EFT Arena: the sunken field of an old sports arena with its stands, converted for death-fighting. Containers, barricades and a grounded Mi-26 fill the field between two gated team pockets at the east and west ends; the layer-29 border cubes of Arena bound the play space.",
            IconX: 919,
            IconY: 679,
            DbBaseJsonPath: "arena_bowl/base.json",
            // PLAN-BOWL item 5/7 (docs/progress/bowl-release.md, 2026-10-18). Exits mirror tools/maps/bowl.exits.json
            // (BowlMapTests): one gate beyond each pocket as seen from the pocket, on Arena's own NavMesh main component.
            Exits: new[]
            {
                new ArenaExitDescriptor("Bowl BTR Gate", 51.0f, 0.1f, -31.0f),
                new ArenaExitDescriptor("Bowl Army Gate", -51.0f, 0.1f, -49.0f),
            },
            // The two GateEntity roots of Arena_Bowl_TeamFight_DesignStuff (both at the origin): DOORS_SPAWN_A holds
            // Bowl_Gates_Respawn_01 (gate + rail) and BTR_82_closed, DOORS_SPAWN_B holds Gates_Army_01 (gate + two Telfer
            // posts) and the ambulance group. No part name contains "frame", so all four direct children switch off at
            // raid start (each pocket has two independent exits, so all four must open together). Both roots are start
            // gates: a mode plugin's hold keeps every part closed. Mirrors tools/maps/bowl.json opened_barriers/start_gates.
            OpenedBarriers: new[] { "DOORS_SPAWN_A", "DOORS_SPAWN_B" },
            StartGates: new[] { "DOORS_SPAWN_A", "DOORS_SPAWN_B" },
            // Bake inputs (item 7 analysis, 2026-10-18): Arena's NavMesh spans x -63.8..59.6, y 0.04..9.25, z -79.5..8.8;
            // the box adds about 2 m, y max 10 keeps the two guard-tower tops. Layers = Arena's own NavMeshSurface mask
            // (9, 11, 18, 30) plus 29 because the kept border cubes are baked as walls (KeepLevelBorders). The one bake
            // exclusion is the 165 x 144 m under-floor slab 'Arena_Bowl_LevelBorders/Cube' (y -1..0), which would
            // otherwise become a walkable plane at floor level; it stays active at runtime as a catch floor.
            BakeBounds: new[] { -66.0f, 62.0f, -2.0f, 10.0f, -82.0f, 11.0f },
            NavMeshBakeLayers: new[] { 9, 11, 18, 29, 30 },
            NavMeshBakeExclusions: new[] { "Arena_Bowl_LevelBorders/Cube" },
            // The one shader of Bowl's 23 that EFT 0.16.9.5 does not ship (Tarp_Tunnel, 12 renderers); the client's
            // ShaderStandinPolicy owns the substitute row (PLAN-BOWL item 10).
            ShaderFallbackNames: new[] { "p0/Reflective/Bumped Specular_TwoSided" },
            // Puddle footsteps (2026-10-25): the rain-driven "puddle" decal (USERAIN, 13 active quads in Arena_Bowl). The field is one
            // Bowl_floor_BALLISTIC_soil collider, so Soil is an eligible ground for the puddle sound (the Concrete/Asphalt rule
            // alone would leave every Bowl puddle silent); metal and plastic containers on top keep their own sound. EFT's
            // BaseBallistic.Associate gives Rubber, Fabric and GenericSoft no ground sound of their own (the serialized default,
            // normally Concrete), so a rubber mat inside a decal's mask may sound as puddle too. Mirrors tools/maps/bowl.json
            // puddle_water_material_names / puddle_extra_surfaces.
            PuddleWaterMaterialNames: new[] { "0877f2335044042e" },
            PuddleExtraSurfaces: new[] { "Soil" },
            // Bay5/AirPit-style native server time: no Directional light exists in the imported scenes (Bowl's sun and
            // sky sit in the excluded Scripts scene), the WeatherRig supplies both.
            UsesServerTime: true,
            // docs/PLAN-BOWL.md decision: the 354 layer-29 LevelBorder cubes are the stadium's only play-area bound;
            // they stay active at raid start and the bake treats them as walls (mirrors keep_level_borders).
            KeepLevelBorders: true,
            // Arena animates the four gate parts of the two team pockets (level48's Animators: BTR_82_closed and
            // Bowl_Gates_Respawn_01 at DOORS_SPAWN_A, Gates_Army_01 and the ambulance group at DOORS_SPAWN_B); these are the
            // closed pose in the scene and the END pose of each node the open clip moves, decoded by
            // tools/extract/extract_door_poses.py (Arena 0.6.1.0.48206). Mirrors tools/maps/bowl.doors.json (BowlMapTests).
            DoorPoses: new[]
            {
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { 51.182f, -0.035f, -21.498f }, new[] { -90.0f, 0.0f, 83.511f },
                    new[] { 45.61f, -0.04f, -22.14f }, new[] { -90.3f, 266.474f, -182.962f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_L_01", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { 1.334f, 2.25f, 0.629f }, new[] { 0.0f, 3.0f, 0.0f },
                    new[] { 1.334f, 2.25f, 0.629f }, new[] { -553.313f, 3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_L_02", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { 1.307f, 0.7f, 0.63f }, new[] { 0.0f, 3.0f, 0.0f },
                    new[] { 1.307f, 0.7f, 0.63f }, new[] { -553.313f, 3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_L_03", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { 1.334f, -1.224f, 0.629f }, new[] { 0.0f, 3.0f, 0.0f },
                    new[] { 1.334f, -1.224f, 0.659f }, new[] { -553.313f, 3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_L_04", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { 1.307f, -2.737f, 0.63f }, new[] { 0.0f, 3.0f, 0.0f },
                    new[] { 1.307f, -2.737f, 0.633f }, new[] { -553.313f, 3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_R_01", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { -1.342f, 2.25f, 0.629f }, new[] { 0.0f, -3.0f, 0.0f },
                    new[] { -1.342f, 2.25f, 0.629f }, new[] { -542.205f, -3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_R_02", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { -1.342f, 0.7f, 0.629f }, new[] { 0.0f, -3.0f, 0.0f },
                    new[] { -1.342f, 0.7f, 0.629f }, new[] { -537.425f, -3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_R_03", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { -1.342f, -1.224f, 0.629f }, new[] { 0.0f, -3.0f, 0.0f },
                    new[] { -1.342f, -1.224f, 0.636f }, new[] { -540.698f, -3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_R_04", "BTR_82_Respawn_Animation", 7.683f,
                    new[] { -1.342f, -2.737f, 0.629f }, new[] { 0.0f, -3.0f, 0.0f },
                    new[] { -1.342f, -2.737f, 0.651f }, new[] { -551.416f, -3.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", "Bowl_Gates_Respawn_01_Gate", "Bowl_Gates_Respawn_01_Animation", 13.5f,
                    new[] { -0.179f, 1.99f, 0.119f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -0.179f, -1.833f, 0.119f }, new[] { -0.089f, 0.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated", "Opened", 8.3f,
                    new[] { 0.0f, 0.0f, 0.0f }, new[] { -90.0f, 0.0f, 0.0f },
                    new[] { 4.773f, 0.0f, 0.0f }, new[] { -90.0f, 0.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Body/Bowl_Respawn_Ambulance_Body_HOOD", "Opened", 8.3f,
                    new[] { -2.034f, 0.022f, 1.306f }, new[] { -0.364f, -3.069f, -6.75f },
                    new[] { -2.034f, 0.022f, 1.306f }, new[] { -0.584f, -4.935f, -6.734f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Body/Bowl_Respawn_Ambulance_Body_tros_02", "Opened", 8.3f,
                    new[] { 6.276f, 0.006f, 0.556f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 0.724f, 0.006f, 2.199f }, new[] { 0.0f, 0.0f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_L", "Opened", 8.3f,
                    new[] { -1.71f, 0.826f, 0.331f }, new[] { 0.0f, 0.029f, 0.0f },
                    new[] { -1.71f, 0.826f, 0.331f }, new[] { 0.0f, 406.884f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_L_Back", "Opened", 8.3f,
                    new[] { 1.2f, 0.814f, 0.331f }, new[] { 0.0f, 0.029f, 180.0f },
                    new[] { 1.2f, 0.814f, 0.331f }, new[] { 0.0f, 406.884f, 180.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_R", "Opened", 8.3f,
                    new[] { -1.71f, -0.804f, 0.331f }, new[] { 0.0f, 0.032f, 0.0f },
                    new[] { -1.71f, -0.804f, 0.331f }, new[] { 0.0f, 406.884f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Bowl_Respawn_Ambulance_Animated Group", "Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_R_Back", "Opened", 8.3f,
                    new[] { 1.2f, -0.824f, 0.331f }, new[] { -0.015f, 0.032f, 0.0f },
                    new[] { 1.2f, -0.824f, 0.331f }, new[] { 0.0f, 406.884f, 0.0f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army/Gates_Army_door_01", "Gates_Army_02_Animation", 10.0f,
                    new[] { 1.95f, -0.001f, 1.1f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { 1.95f, -0.001f, 1.1f }, new[] { 0.0f, 0.0f, 142.922f }),
                new ArenaDoorPose("DOORS_SPAWN_B", "Gates_Army_01", "Gates_Army/Gates_Army_door_02", "Gates_Army_02_Animation", 10.0f,
                    new[] { -1.95f, -0.001f, 1.1f }, new[] { 0.0f, 0.0f, 0.0f },
                    new[] { -1.95f, -0.001f, 1.1f }, new[] { 0.0f, 0.0f, -161.335f }),
            },
            // Score monitors (2026-10-25, tools/maps/bowl.monitors.json, docs/progress/score-monitors.md): 16 TV props. The 10 of
            // Arena_Bowl_DesignStuff's base set are inactive (the TeamFight set stands at the same spots) and stay dark; the 6 of
            // Arena_Bowl_TeamFight_DesignStuff (2 timers, 2 scores, 2 alive counts, 20.5 x 12.4 m on the stadium walls) get a face.
            ScoreMonitors: new[]
            {
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater", new[] { -83.8338f, 32.7618f, 25.2892f }, 1, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (1)", new[] { -84.5238f, 32.2618f, -95.3208f }, 0, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (2)", new[] { 82.2962f, 32.3518f, -95.9808f }, 2, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (3)", new[] { 85.1534f, 31.1138f, 26.6207f }, 0, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (4)", new[] { 90.1961f, 32.504f, -34.7598f }, 1, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (5)", new[] { -92.943f, 32.3369f, -35.3581f }, 2, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (6)", new[] { 41.7962f, 1.6688f, -12.1358f }, 1, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (7)", new[] { -45.3298f, 1.6868f, -58.7758f }, 1, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (8)", new[] { 43.8112f, 1.6688f, -14.2218f }, 10, false),
                new ArenaScoreMonitor("Arena_Bowl_DesignStuff", "TV/Arena_Home_theater (9)", new[] { -43.5238f, 1.6868f, -61.0628f }, 10, false),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater ALIVE", new[] { -84.37f, 32.2618f, -95.17f }, 2, true),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater ALIVE", new[] { 84.9772f, 31.1138f, 26.4015f }, 2, true),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater SCORE", new[] { -83.8338f, 32.7618f, 25.2892f }, 1, true),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater SCORE", new[] { 82.2962f, 32.3518f, -95.9808f }, 1, true),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater TIMER", new[] { -92.6392f, 32.2636f, -35.3672f }, 0, true),
                new ArenaScoreMonitor("Arena_Bowl_TeamFight_DesignStuff", "TV_Wires_TeamFight/Arena_Home_theater TIMER", new[] { 89.8399f, 32.4507f, -34.7589f }, 0, true),
            });

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
            IconX: 234,
            IconY: 731,
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
            IconX: 867,
            IconY: 401,
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
            IconX: 691,
            IconY: 799,
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
            IconX: 110,
            IconY: 608,
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
            IconX: 488,
            IconY: 580,
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
            IconX: 547,
            IconY: 402,
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
            IconX: 219,
            IconY: 307,
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
            IconX: 479,
            IconY: 690,
            DbBaseJsonPath: "arena_woods/base.json");

        /// <summary>Complete development catalog. Parked maps retain their stable identities and inputs.</summary>
        public static readonly IReadOnlyList<ArenaMapDescriptor> Catalog = new[] { Equator, Bay5, AirPit, AirPit2, AutoService, Bowl, Prison, RailwayStation, Yard, Iceberg, Factory, Custom, Shoreline, Woods };

        /// <summary>Playable and distributable maps. Runtime consumers must iterate this release subset.
        /// Equator, Bay5 and AirPit are the released ones (0.2.2); Bowl joined on feature/bowl for 0.3.0
        /// (PLAN-BOWL item 8, 2026-10-18) and is registered and staged, not yet released.</summary>
        public static readonly IReadOnlyList<ArenaMapDescriptor> All = new[] { Equator, Bay5, AirPit, Bowl };
    }
}
