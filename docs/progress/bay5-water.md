# Bay5 harbor water and puddle footsteps — 2026-09-28

## Harbor diagnosis and source recovery

The nearly white harbor was reproduced on GPU-2 at midnight, viewed from
`(146.5,-3.4,20.6)`, yaw 90, pitch 10. The earlier `SeaPlaceholderRepairs` only
constructed `FX/SimpleWater4`. Its installed shader defaults include a white
reflection texture, flat normal map, bright cyan base/reflection colors and a
constant light direction. None of these inputs tracked the night lighting.

Arena stores the real surface on `EFT.Water.WaterContainer`, not in the mesh's
empty material array. That newer component is absent from this EFT version,
but its complete settings block matches native `WaterSettings`.
`tools/extract/extract_water.py` recovers those settings, source target path and
four original texture mip chains into ignored `work/extract/bay5-water`.
Two independent exports were byte-identical. See `tools/extract/WATER.md` for
the serialized contract, texture formats, source references and provenance.
These are BSG assets from the user's installation; no redistribution licence
is asserted and no generated data is committed.

The client uses one owned `WaterSSR.WaterRendererv3` and `WaterForSSRv3`, the
existing `Arena_Bay5_Main/Sea/Plane` mesh and installed `Hidden/WaterForSSR`.
Native deferred rendering supplies world lighting and reflections. The shader's
two passes were checked in the installed binary: four G-buffer outputs, then
one alpha-restore output. The wrapper captures/restores native snow mode,
suppresses only the mesh's automatic placeholder draw, and explicitly disposes
command buffers omitted by native cleanup. The live switch restores the prior
renderer state. No map bundle rebuild or synthetic texture is involved.

## Puddle investigation and scope

Historical packages `bay5`, `bay5-background`, `bay5-native-features`,
`bay5-native-dust`, `bay5-native-v3/v4` were compared with v5. Existing object
transforms and collider definitions did not change. From native-features onward
there is one Water ballistic surface (the sea), no WaterPuddle surfaces, and
54 named puddle objects with rendering/LOD components but no collider.
This does **not** establish what caused the user's earlier perceived sound.

The installed `Player.CalculateMovementSurface` chooses the nearest collider
using a three-hit native buffer and looks for `BaseBallistic` on that exact
GameObject. Water/WaterPuddle select the existing Puddle audio set. Rain and
rendered puddle decals are not inputs to that query. A separate seasonal audio
layer exists; it does not implement a rain-to-Puddle override.

GPU-2 baseline, 08:52:35 and 08:56:25, process `feaf45fc`: the yard and pier
puddle centers queried Concrete and hit the original
`Bay5_floor/Bay5_floor_BALLISTIC_concrete`, with one hit and a direct Concrete
BallisticCollider. This was not the unrelated parent-only ballistic-twin issue.
At the center the puddle's own visual mask is also transparent, so wet points
must be compared separately from simply standing inside the bounding square.

`PuddleFootstepRepairs` translates only successful Concrete/Asphalt results
within the source water material's actual triangles and visible mask. Oil is
excluded by reviewed material identity in the map descriptor. The native shader
formula, recovered from installed shader bytecode, is:

```
coverage = saturate((MainTex.red + vertexAlpha * waterLevel - 1) * FadeStrength)
waterLevel = USERAIN ? global_WaterLevel : material_EditorWaterLevel
```

UV transforms and vertex alpha are retained. A one-time GPU readback samples
the actual imported texture in the game's color space (Gamma on this install),
with no incorrect additional sRGB conversion. Only coverage >= 0.1 and a
foot-to-plane distance <= 0.16 m qualify. This deliberately excludes faint
edges, dry holes and elevated platforms. No physical collider, penetration
property, custom audio clip or broad rectangular water region is added.
The native player audio path selects its existing Puddle bank normally.

## Host evidence collected during implementation

- Baseline white night plane: 08:46, old process `4d438dd4`.
- First native client SHA-256:
  `21BBCA004F8CF05FAD4AC43986F7C18B8D7F2BFAEEB1C649C53A3D0091AA8A39`.
  GPU-2 fresh process `feaf45fc`, 08:49:19: all 23 machine plugins matched
  VM build, host disk and loaded MVID before entering a raid.
- Daytime native water at 08:51:47: visible waves, harbor reflections and
  shoreline shading. Probe: one buffer, four textures, one target,
  `drawReady=True`, `DeferredShading`. Live off at 08:52:13 restored the white
  plane; on at 08:52:28 recreated one renderer. No rendering errors.
- Midnight repeat raid: native start=00:00 at 08:54:14, new water renderer at
  08:54:19. Same viewpoint showed dark water with subdued surface detail.
  Live off at 08:55:14 reproduced the white plane; on restored dark water.
  Unload at 08:57:06 released its single buffer and restored the renderer.
- The fresh process initially read legacy hour 12, so noon and midnight were
  verified from the actual `[TimeOfDay]` log rather than an assumed saved value.

Final build, puddle acceptance, settings restoration and deployment evidence follow.

## Puddle native acceptance — final client

Client SHA-256 `A0BE94D2F86319CAD390C2238421B2A61072538C8E10999C8C08F90D916F4440`,
fresh GPU-2 process `8afea561` started 09:01:51. All 23 machine plugins matched
VM, disk and loaded MVID. Bay5 noon/Rain entered at 09:03; no bots.

- 09:03:58: cache contains 45 active reviewed water surfaces, one readback mask,
  Gamma color space. A full-coverage sample at `(77.486,-3.890,18.913)` hits a
  thin authored Soil decal over the concrete. Its Soil set and `soil_walk_01`
  are deliberately preserved by the ground-material guard.
- 09:04:28: pier wet point `(132.923,-3.890,34.318)`, UV `(0.1377,0.8135)`,
  sampled red `0.6784`, shader coverage `1`, plane distance `-0.0039`.
  Native query Concrete -> corrected Puddle. After a real jump/landing,
  native `CurrentSurface` and `_currentSet.Surface` are Puddle, with existing
  `walk_puddle_07` / `sprint_puddle_08` clips in the actual selected banks.
- 09:04:57: live correction off, same wet point, rain=1/wetting=1:
  Concrete -> Concrete; the player selects `walk_concrete1`. Rain alone does
  not recover the Puddle set on this tested surface.
- 09:05:21: correction on, dry center of the **same** pier quad,
  `(134.780,-3.890,31.440)`, UV `(0.5,0.5)`, red `0.1147`, coverage `0`:
  Concrete remains Concrete and `walk_concrete1` is retained.
- No errors during these comparisons. Runtime surface and actual sound-bank
  selection were measured; no subjective listening acceptance is claimed
  (host audio output was muted). The original reported historical difference
  remains unproven; this is an explicit new mapping of existing visual puddles.

The cache uses the base texture mip for stable classification under the player.
Faint/mip-filtered visual boundaries can differ slightly at a distance. This
is not a benchmark or an inspection of every puddle. Oil, projection puddles
using another shader, soil patches and non-concrete/asphalt surfaces are outside
this deliberately narrow translation.

### Repeat raid and dry-weather check

In the same final process, a second Bay5 raid started at 00:00 with Clear at
09:07:01. The prior renderer released one buffer on unload, and the new raid
created one new renderer with four textures and one target. At 09:07:40 the
same pier wet point again selected `Puddle` and the real `walk_puddle_07` bank.
Rain was zero; native Wetting still decayed from the preceding rain raid. This
static puddle material does not use the USERAIN keyword or global water level.

At 09:08:16 the harbor viewpoint still showed dark water; the probe confirmed
one buffer and `drawReady=True`. The elevated metal container surface remained
Metal with `walk_metal3` / `sprint_metal4`, and no puddle match. This establishes
wet/dry distinctions, different base-material preservation and repeated loading.

## Final verification and deployment

- Bay5 unload 09:09:26 released its one water buffer. Equator then started at
  12:00, 09:10:19. No Bay5 water renderer or puddle mapping was activated there.
  F10 at 09:10:38 confirmed the unchanged native Concrete result and
  `walk_concrete1`; the scene rendered normally and no new errors occurred.
- GPU-2 returned to MainMenu, control free. Last raid health was 440/440.
  Final status: all 23 machine plugin builds = host disk = loaded code.
  Clear weather, legacy Bay5 hour 0, both new water switches true. Other saved
  lighting/flare options were not changed. Test weather wetness decays natively.
- Client Release: 0 warnings/errors. Python: 425 tests, 28 expected skips.
  Server: 396 passed, including map scope/sidecar identity. Independent runtime
  review covered renderer cleanup, source settings, shader mask/color space and
  texture sampling. `git diff --check` passed.
- Both `D:\SPT41` and `D:\SPT41vanilla` received 91 staged files using the owned
  deploy helper. Every staged destination file was independently SHA-256 matched
  against `dist/`. The five new water files total 2,277,635 bytes; scene bundles
  retain the accepted v5 contents. Client hash is listed above. The rebuilt
  server hash is `E5DEB1CE6E055CAF3F5FDF22DD7CECC97D0B18FC7BCCDBDA3D2FD78AA84E9502`;
  its only source change is the shared per-map puddle identity table, with no new
  server gameplay logic. Restart Vanilla's game/server before testing; its
  in-game acceptance remains separate from GPU-2 acceptance.

Controls in `Water (arena_bay5)`:

| Setting | Default | Effect |
| --- | --- | --- |
| `NativeHarborWater` | true | Original water settings/textures on native deferred rendering; live off restores the old placeholder |
| `PuddleFootsteps` | true | Native puddle audio set on reviewed visible water regions; live off restores vanilla ground selection |

The old shared `Sea / EnableWaterPlaceholder` key retains its existing meaning
and saved value; no silent default migration was performed.

## Distribution gate for the water package (2026-10-17)

`tools/validate/validate_distribution.py` had no check for the staged water package: its
exact-set checks covered bundles, shader sidecars, db, acoustics, AI sidecars, art and
flares, so a client build with a missing, stale-named or parked-map `water/` directory
still printed `DISTRIBUTION_OK`, and only NativeWaterRepairs refused the package at raid
start (harbor back on the unlit placeholder). `validate_water` now runs next to the flare
block with `WATER_MAP_KEYS = ('arena_bay5',)`:

- `water/arena_bay5/water.json` must exist with schema 1, its own map key and
  `Hidden/WaterForSSR` (the identity the client checks).
- Its `textures` must be exactly the four WaterSettings fields of
  `extract_water.TEXTURE_FIELDS`, each a bare `textures/<name>.bin` name (the client's
  ReadTexture rule), no two sharing a file.
- Every listed file must exist with the recorded `byte_count` and `sha256`, and the
  recorded width/height/format/mip_count must explain that byte count through the
  extractor's own `texture_byte_count`.
- The set of files under `water/` must equal that list, so a stray file, a released map
  without water or a parked map's package (`water/arena_airpit2/...`) fails.

`test_validate_distribution.py` builds the fixture package with the extractor's mip-chain
arithmetic (8 x 8 px, four mips) and covers missing, stray, same-length-modified, longer,
mis-keyed, mis-described and mis-named packages. Evidence 2026-10-17: the real Bay5
package in `dist/` (five files, 2,277,635 bytes) passes `validate_water`; a scratch copy
with a parked map's directory or one flipped byte in `foam.bin` is refused with the
expected messages; the full run prints `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`;
Python suite 1722 tests, 78 expected skips.
