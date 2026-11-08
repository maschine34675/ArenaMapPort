# Autoservice release log (sixth map, branch `feature/autoservice`)

The plan is [docs/PLAN-AUTOSERVICE.md](../PLAN-AUTOSERVICE.md); this file is the dated record of what landed, with command output
and host evidence. Appended, never rewritten.

## 2026-11-08 - start: request, surveys, plan

- Request (user, German): "dann wuerde ich gerne mit der naechsten map weitermachen: Autoservice", the day after the 0.5.0
  publication (five maps) and after the user found the score monitors. A go without further constraints, as Iceberg's was.
- Branch `feature/autoservice` from `main` = 52d0a4d, clean tree. Nothing was built, deployed, committed or pushed in this
  step; the four surveys wrote only under the ignored `work/tmp/autoservice-survey/` (s1 to s4). Arena was only read.
- Four read-only surveys on the VM's Arena 0.6.2.0.48473 and the spike package. Findings that shaped the plan:
  - **S1 (source delta and identity).** Installed BuildSettings (388 scenes) lists the Autoservice scenes at the same indices as
    the spike's 0.5.4 plan: 62 main, 63 AI, 65 DesignStuff, 66 Light, 69 Scripts, 112 Sound, 135 Culling, 136 TeamFight_DesignStuff,
    243-246 Winter set (also 64/68/71 temp scenes, 67 Loot, 70 ShootOut_DesignStuff, 172 LastHero_DesignStuff). The unchanged plan
    re-extracts read-only to 32,097 objects, 4,270 meshes, 402 materials, 872 textures, 116 warnings, 0 errors. Real 0.6.x delta:
    `DesignStuff` 124 -> 497 objects (the new `Arena_AutoService_LevelBorder` root, 372 `Cube` children on layer 29, `BoxCollider`
    4 -> 376) and two new `metal_sheet_hanging_01_rusted` props in the main scene (25,678 -> 25,692); `TeamFight_DesignStuff` 4,710
    and `Light` 1,198 identical. All object, mesh (730) and texture (110) ids rehash, so the spike package cannot be reused. The
    preset offers ShootOutSolo/Duo/Trio, TeamFight, Coop and LastHero; no MongoId field, only the ServerName. Key source: three
    streams, `sharedassets62.assets.resS` 458,749,216 B, `sharedassets62.resource` 2,036,672 B, `sharedassets66.assets.resS`
    4,156,800 B (464,942,688 B), equal in size to 0.5.4. Identity art under the name `chop-shop`: banner path 1738 (769 x 460),
    loading screen path 1731 (1920 x 1080), `Arena_MainMenu_Paralax_ChopShop-1..4`; official name and description rows exist in
    en, ge and ru under Arena's own id `6441004f8ab20b218807f14b` (confirmed by a lookup in `TestBackendLocaleEn`: the `Name` row of that id and the key `Arena_AutoService` both read "Chop Shop"; no text copied).
  - **S2 (gameplay objects, modes, navigation).** TeamFight is the natural mode: 20 `SpawnPointMarker`s (all "Usec house") in an
    east pocket (x about -337) and a west pocket (x about -409), two `SPAWN_BOUND_Fighter` bounds, one capture point at (-369.29,
    -3.80, 499.49), four gates, no kill, spawn-protection or minefield triggers, no resupply crates (LastHero only). Pocket split
    ratio 2.1 against the limit 2.0, names north/south are east/west. Arena's NavMeshData (`sharedassets63.assets` path 2): 695
    polygons, 1,212 triangles, 19 of 30 tiles, main component 1,915.6 m2 with every marker and AI point; surface volume x -421.2..
    -319.4, z 446.0..527.65, mask 0x40042A00; nothing above y -0.43 is on Arena's mesh. 372 border cubes (AABB 80.6 x 47.3 m)
    plus props do not close the perimeter in plan view; the `ExcludeLevelBorders` polygon is a culling outline. Gates: Mecanim
    clips with the spelling `clossed`, controllers of level 136 live in `sharedassets70.assets`, `equator_roller_shutters_01`'s
    scene collider sits 0.978 m below its clip's closed pose; decoded drives: big gate +4.40 m in 8.0 s, rollers +4.11 m in 6.733
    s, equator shutter +2.56 m in 4.5 s; four lamp `DotAnimator`s. Four `EntitySoundPlayer` gate clips (10.43, 4.68, 8.01, 7.90 s).
    Four score-monitor holders with faces (2 timer, 2 alive). AI scene: 16 bot born points, 7 core points, a 5-hub PatrolWay with
    59 points, 23 mine points; stale far-field leftovers (2,696 bearing points, 44 inactive navmesh blocks) lie 400 m or more out.
    ShootOut has 27 markers (15 on tribunes) and four more TVs, LastHero 24 markers, one bound and two refill crates.
  - **S3 (environment, rendering, audio).** Fully realtime lighting: 189 + 12 `Light`s, 0 lightmaps, 0 probes, no Directional
    light in the imported scenes (Scripts 69 holds the sun at 0.5); existing decoders accept 304 decals, 184 culling lights, 78
    volumetric lights, 0 flicker. Strongest emissive 3.88 (no cap needed). 33 distinct shaders (census of 409 materials; the earlier 34 is not reproducible), all present in EFT 0.16.9.5; four need a
    check (FogSheet and two Cloth shaders resolve from Customs per the AirPit evidence; `SMap Baked` lives in `sharedassets216`).
    No water, grass or terrain; puddle material `0877f2335044042e` (42 slots) plus one wet mold decal. Rain cover: 1
    `IndoorTrigger`, 0 `DryPlane`. Spatial exporter runs clean with no repairs (7 rooms, 16 portals, 9 areas, 139 routes). Ambient:
    42 players incl. 3 `SeasonLoopAmbientSoundPlayer` (a class the repo does not know), 9 `water_drops` with `minDistance` 0, forced
    mixers and an inactive spline chain: new reviewed contract work. 30 `SCAV_SPECTATORS` fans are full player-body rigs in the
    TeamFight scene, `DesignStuff` has none. 21 `FlareLight`s pass the existing checks (tuple (21, 8, 77, 32)).
  - **S4 (pipeline gap).** Autoservice is Catalog-only with a spike identity ("Arena: Auto Service (spike)",
    `ArenaMapDescriptor.cs:1210-1224`), parked in `park-unreleased-dist.ps1:31` and `build_remaining.ps1:27/35`. A sixth released
    map needs about 12 sidecar files, 12 extractor tables, 8 client arrays or switches, the client csproj staging and key-create
    guard (lines 190-507), the lock-step release lists and 20+ tests; Iceberg's promotion `1bd5868` (79 files) is the checklist.
    The server C# is data-driven (base.json, csproj rows, banners, description pack). `Bay5AudioImporter` throws for an unknown map
    (two switch rows). Size: spike scenes 708,353,294 B, growth 1.31-1.34x on Bowl and Iceberg, so about 0.93-0.95 GB (0.86-0.88 GiB) plaintext
    (inferred); `dist/` is 359 files, 5.03 GiB now and about 6 GiB after.
- Plan decisions in short (agent): TeamFight alone; `UsesServerTime`; summer set only, no winter; no grass; keep the 372 border
  cubes (`keep_level_borders`, layers 9/11/13/18/29/30); bake bounds from Arena's NavMeshSurface volume; team sides by bound; four
  gates opened and holdable, door tool patched (`clossed`, level 70 clips, equator-shutter override); four gate sounds; four monitor
  faces; crowdless (CROWDLESS_MAP_KEYS); official description once the id is confirmed; chop-shop art after looking at it; ambient
  contract with the new season-loop class; spatial acoustics without repairs; no emission cap; no native water; MultiFlare yes;
  DynamicMaps overview in Phase C; stays Catalog-only until item 12.
- Open questions: (1) closed: the id is confirmed by the table lookup, only the check against the port `id_field` remains (item 9); (2) which season Arena loads
  live and whether a winter variant is wanted (user); (3) crowd of the 30 body-rig fans or crowdless (user); (4) do the frame
  colliders of the roller gates leave a doorway after the posed leaves are dropped (`navmesh_diff.py --doors`, then a host walk);
  (5) is the 0.978 m closed-pose gap of the equator shutter a scene inconsistency or a clip offset; (6) who closes the north edge
  and the second island at z 521-528; (7) are the tribunes (y 4.4 and above) walkable in the port (the fresh bake decides);
  (8) the rain heuristic with one `IndoorTrigger`; (9) `SMap Baked` shader at raid start; (10) the wet mold decal as a puddle
  surface; (11) the 7 textures with the same id but different size between the spike and 0.6.2; (12) `ArenaLevelSettings` and the
  mixer group name of the gate players are not decoded. Survey discrepancy to settle in item 5: S1 lists three key-source files
  (scenes 62 and 66 only) while S2 calls `sharedassets112.resource` a likely key-source file; the key source is the port's plan
  scenes (112 is not one), and the gate-clip copy check runs anyway. S4's note that the description-count test holds only if
  Autoservice has no official text is wrong for `All.Count - 1`: five of six maps with a text is still `All.Count - 1`.
- Next step: Phase A items 1 to 3 (housekeeping and plan roles, package v1 with `PYTHONUTF8=1`, the native contract), then the
  `SeasonLoop` audio work (item 4) and the key-source rows (item 5). Nothing builds or deploys before item 12.
