# Bay5 ArenaMode integration

## 2026-09-21 — implementation and geometry checkpoint

The user accepted Equator and requested Bay5 next. The companion ArenaMode change
`75a7430` admits `arena_bay5` for individual Deathmatch and Gun Game, with an optional
MapPort dependency, independent availability from Equator, and guarded revision-2
migration of the exact previous default map lists. Custom lists remain unchanged.
Team modes are outside this map's current acceptance scope. No loot or Scav raids.

### Map data and round-open geometry

`bay5.bots.json` provides four All-side player points and ten separate Bot/BotPmc
points in `BotZone_Main`, a cap of eight, five scav waves and four PMC wave entries.
Coordinates were derived from the source AI bearing points and checked through
UiBridge on the old installed map before the rebuild. These checks establish candidate
ground positions, not acceptance of the new bake or bot combat.

The 2,208 source AI bearing points define a combat envelope of x=44.415..146.295 and
z=-14.037..71.174. Sampling the containing rectangle at 1 m leaves at least six bot
points 25 m away; the regression requires at least three distinct candidates for a
two-bot replenishment burst. Closest player-to-bot spacing is 9.31 m and closest
player-to-exit spacing is 29.78 m. Standing room and connectivity remain native checks.

The old NavMesh (15,420 vertices / 7,142 triangles) disconnected the BEAR depot spawn
from the central yard. Bay5 has 200 active, non-trigger source colliders on numeric
layer 29, named `Cube` rather than `LevelBorder`. Both Arena and installed EFT name
that layer `LevelBorder`; the importer preserves numbers, while the Unity project's
TagManager leaves that slot unnamed. Runtime removal worked but name-only bake
filtering retained those invisible barriers. The bake now excludes the collider's
own layer 29, matching runtime ownership rather than inheriting its parent's layer.

Three descriptor paths are shared by runtime opening and NavMesh exclusion:

- `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)`
- `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)`
- `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame`

The warehouse paths are unique. The depot contains two siblings of the same name;
both existing resolvers select the first, northern assembly at approximately
(50.574, -3.895, 7.525). Its eastern duplicate remains closed. Frame geometry is kept.
The final host test must demonstrate travel through the selected openings.

### Ordinary test exits

Two always-available, eight-second individual exits are mirrored in the descriptor
and sidecar, each with a 4 x 3 x 4 m trigger:

| Exit | Position |
|---|---|
| Bay5 North Loading Bay | (91.2427, -2.9252, 65.142) |
| Bay5 South Container Lane | (99.029, -3.8574, -2.8513) |

These are ordinary-raid test vehicles. ArenaMode uses its protected session return.
The initial ordinary geometry raid was disconnected through the native result and
treatment screens; the character was healed there before subsequent acceptance.
Take a fresh profile baseline for protected ArenaMode tests after ordinary tests.

### Independent review and offline verification

Independent source/data review found no P0-P2 issue. It checked numeric-layer owner
parity, frame retention, duplicate-path behavior, 14 unique spawn IDs, exact generated
zone/category/infiltration fields, spacing and coverage. New Python tests cover the
distance and generated-data contracts; C# tests cover bake/descriptor barriers and
exit transform/eligibility parity.

ArenaMode's full verifier passed 691 server tests (one optional APBS skip), source-linked
client harnesses and five compiled fault controls. The parent reran both leaf commands;
all four leaf gates passed. Equator's 34 material-cap tests and native client Release
build passed independently. Deployment and native acceptance are appended below when
completed; this checkpoint alone does not claim a playable release.
