# Bay5 original harbor background

## Source and implementation checkpoint — 2026-09-21

The user confirmed that Bowl already has its stadium, and asked to add Bay5's missing
background. This change selects the original `Arena_Bay5_Background` (source `level79`)
after the existing eight extracted Bay5 scenes. It contains three-dimensional harbor
and city geometry, not a replacement skybox texture.

The original scene contributes 7,090 objects, including 4,055 MeshRenderers and 1,026
LODGroups. Source hierarchy activity, renderer flags and collider states are preserved;
Arena-only gameplay/culling scripts remain excluded by the existing component allowlist.
The fresh package is `work/extract/bay5-background`: 59,422 objects, 3,911 meshes,
453 materials and 945 textures, zero extraction errors. Source level79 SHA-256:
`F959EF0B92374EF7CE888E923F6FE0A199BEB0EB3F7D60AF0694DC9D292AF5C0`.

`background_scene_names` is mirrored by Shared `BackgroundSceneNames`, with empty
defaults for other maps. The descriptor validator rejects unloaded/duplicate names,
the authored Scripts scene and JSON/runtime disagreement. The preset loads the
background and runtime ownership includes it for rendering, shader repair and teardown.

Background renderer bounds are excluded from the NavMesh bake volume and from
rain-cover/indoor-floor classification. Its original colliders remain NavMesh sources:
some shipping containers occupy real space along the south lane. Omitting those sources
would let bots plan paths through visible solid objects. The distant skyline reaches
roughly 1.7 km southwest and must not enlarge the bake or roof-camera footprint.

An independent source and production review found no functional P0–P2 defects.
All four player starts, nine bot starts and north exit have no conservative body-box
overlap with source background colliders. The southeast bot and south exit are near
container walls (about 0.422 m and 0.341 m from their vertical body axes); these source
measurements require native navigation, standing-room and extraction checks.

Before-build reference views from the old loaded process `c48f7fdd`:

- `analysis/screenshots/bay5-background/before-north-lane.png`: (112.518, -3.8574,
  15.134), yaw 0, pitch -30.
- `analysis/screenshots/bay5-background/before-gantry.png`: (77.7537, -3.5284,
  61.7423), looking toward (82, 18, 115).

These images show the old open sky. They are comparison evidence, not acceptance of
the new bundles. The original south exit completed during this reference raid.
Build, deployed identity and native acceptance are recorded in the appended checkpoint.
