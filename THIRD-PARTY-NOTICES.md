# Third-party notices

ArenaMapPort adapts small, specific pieces of other MIT-licensed SPT mods' code. This
file lists exactly what was reused and from where, per docs/SPEC.md's attribution
requirement (§A, §B.WP5). Nothing under `ManimalLighthouse-master` is used anywhere in
this repository (no license — excluded entirely, per the workspace's own instructions).

---

## ManimalIcebreaker (WP5 — server location registration)

Source: `D:/sptmaps/_sources/ManimalIcebreaker-main`

```
MIT License

Copyright (c) 2026 danauraborealis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `ArenaMapPort.Server/ArenaEquatorRegistration.cs`:

- **Registration mechanism** — `EnsureAvailable`/`Register` against SPT's
  `LocationTable.GetDictionary()`, ported near-verbatim from
  `icebreaker-server/IcebreakerLocationRegistration.cs:11-29`
  (`IcebreakerLocationRegistration.EnsureAvailable`/`.Register`).
- **Loot-LazyLoad shape** — the empty-but-non-null `LooseLoot`/`StaticLoot`/
  `StaticContainers` construction that satisfies SPT's raid-start loot generator
  without shipping real loot, ported from `icebreaker-server/IcebreakerMod.cs:196-204`
  (the "loot-free" branch of `IcebreakerMod.OnLoadAsync`).
- **Load-order priority** — `[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]`,
  matching `IcebreakerMod.cs`'s own class attribute (`icebreaker-server/IcebreakerMod.cs:56`).
- **Locale-transformer pattern** — registering `"<Id> Name"`/`"<Id> Description"` via
  `LocaleTable.Global[*].AddTransformer(...)`, following the shape of
  `icebreaker-server/IcebreakerMod.cs:246-276`, reduced to the two keys SPEC §E.5
  actually requires for the spike.

Everything else in `ArenaMapPort.Server` (base.json's field values, the xunit test
suite, the csproj) is original to this repository, informed by Icebreaker's approach
but not copied from its code.

---

## ManimalIcebreaker (PLAN-TWO-MAPS item 10 — location-select banner routing)

Source: `D:/sptmaps/_sources/ManimalIcebreaker-main`

```
MIT License

Copyright (c) 2026 danauraborealis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `ArenaMapPort.Server/ArenaMapBanners.cs` (called from `ArenaMapRegistration.cs`'s
`ArenaMapPortMod.OnLoadAsync`):

- **The whole ImageRouter wiring approach** — inject `ImageRouter` as a plain DI constructor
  parameter into the mod class, resolve each banner's physical file from the mod's own deployed
  folder (`SysPath.GetDirectoryName(typeof(...).Assembly.Location)`), and on a hit call
  `imageRouter.AddRoute("/files/banners/<file stem>", <full path>)` — ported (adapted, not
  verbatim) from the banner-wiring block of `icebreaker-server/IcebreakerMod.cs:367-397`
  (`IcebreakerMod.OnLoadAsync`'s "LOADING SCREEN BANNERS" section). Icebreaker's own comment there
  (`IcebreakerMod.cs:374-376`) — "ImageRouter lowercases the key and strips the extension off the
  REQUEST, so the extension in base.json only has to be plausible, not exact" — is the reasoning
  `ArenaMapBanners.Resolve`'s own multi-extension probe (`.jpg`/`.jpeg`/`.png`) relies on.
- **Locale key convention** — `"<bannerId> Name"` / `"<bannerId> Description"`, one literal space,
  no underscore, matching `IcebreakerMod.cs:352-353`'s own comment and `BannerLocales` array
  (`IcebreakerMod.cs:86-97`), written into the locale table via the same
  `LazyLoad<GlobalLocaleDictionary>.AddTransformer` loop this file's own WP5 section already
  attributes.

**Diverges deliberately** from Icebreaker's version in one respect: Icebreaker iterates
`newBase.Banners` (already populated from its own `db/base.json`) and only skips *routing* a
missing file — the `Banner` entry itself stays in the array either way, which can still produce
the "[UNHANDLED]"/empty-dialog landmine its own comment warns about
(`ArenaMapPort.Server/ArenaMapBanners.cs`'s class doc comment). `ArenaMapPort.Server/db/*.json`
keeps shipping `Banners: []` for every map instead; `ArenaMapBanners.Resolve` only ever hands back
a `Banner` entry — and `ArenaMapPortMod.OnLoadAsync` only ever assigns one to
`LocationBase.Banners` — once the backing file has already been confirmed to exist on disk, so a
missing file can never produce a dangling entry.

---

## ManimalIcebreaker (WP3 — preset bundle patcher)

Source: `D:/sptmaps/_sources/ManimalIcebreaker-main/analysis/patch_icebreaker_preset.py`

```
MIT License

Copyright (c) 2026 danauraborealis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `tools/preset/patch_preset.py`:

- **Overall patch pattern** — load the donor `ScenesPreset` bundle with UnityPy, find the
  `MonoBehaviour` whose `m_Name` contains `"ScenesPreset"`, rewrite `_scenesResourceKeys` to
  a controlled scene list (`path` = our served scene-bundle key, `rcid` = scene name), trim/pad
  `ScenesGuids` to match, `save_typetree` + re-save the bundle file — ported near-verbatim from
  `patch_icebreaker_preset.py:18-51`.
- **"Keep the donor's own `MonoScript`" approach** — the script's header comment
  (`patch_icebreaker_preset.py:7-8`, *"the MonoBehaviour's script reference stays BSG's own ...
  binds to EFT.ScenesPreset by class identity in the SPT client"*) is the reasoning
  `patch_preset.py` follows for never touching `m_Script`.

`patch_preset.py` differs materially: a different donor bundle (SPT's own `develop_preset.bundle`
rather than a retail rip), 6 scenes in ArenaMapPort's own order/naming (SPEC §E.1), and one step
not present in the Icebreaker script — renaming the `AssetBundle` object's own `m_Container`
entry so the renamed preset asset is still resolvable via `AssetBundle.LoadAsset(rcid)` at
runtime (see `patch_preset.py`'s module docstring "CORRECTION" section, and
`docs/progress/wp3.md`, for the full trace/evidence).

---

## ManimalIcebreaker (impl-raidstart-repairs — client raid-start crash fixes)

Source: `D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/RaidFixPatches.cs`

```
MIT License

Copyright (c) 2026 danauraborealis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `ArenaMapPort.Client/SpatialAudioRepairs.cs`:

- **Skip `SpatialAudioSystem.Initialize` when unstaged** — the "staging unavailable" branch of
  `Patch_SpatialAudioInit` (`RaidFixPatches.cs:36-51`): a Harmony prefix that sets
  `__result = Task.CompletedTask; return false;` instead of running the original body. We always take
  this branch (no acoustics sidecar/bake data authored for the spike — audio explicitly out of scope),
  unlike their newer, richer version which first tries a "staged real init" branch we have no data to
  feed.
- **Degrade `ProcessSourceOcclusion` to -1 when uninitialized** — `Patch_OcclusionWhenUninitialized`
  (prefix, gate on `!SpatialAudioSystem.Initialized`, `RaidFixPatches.cs:60-76`) and
  `Patch_OcclusionNeverThrows` (finalizer belt-and-braces, `RaidFixPatches.cs:133-153`), both ported
  near-verbatim including the `TargetMethods()` reflection technique (match every public instance
  overload named `ProcessSourceOcclusion` by name, rather than hard-coding each overload's parameter
  types) and the "log at most 5 times" spam guard.

Reused in `ArenaMapPort.Client/EnvironmentRepairs.cs`:

- **Ensure `EnvironmentManager.Instance` exists before use** — the bare-manager fallback line from
  `Patch_EnsureEnvironmentManager.EnsureEnvAndWeather()` (`RaidFixPatches.cs:262-263`,
  `new GameObject(...).AddComponent<EnvironmentManager>()`), anchored directly on `Player.Init` — the
  same "universal safety net" reasoning as their separate `Patch_EnsureEnvBeforeAnyPlayerInit`
  (`RaidFixPatches.cs:275-280`), collapsed into one prefix since this spike has no Fika-specific
  player-creation path to also cover. Their weather rebuild and full indoor-trigger rehydration
  (needs sidecar data we do not have) are NOT ported.

Reused in `ArenaMapPort.Client/EffectsControllerRepairs.cs`:

- **Add a missing `FrostbiteEffect` to the Cam2 fallback camera** — the `FrostbiteEffect` half of
  `Patch_EffectsControllerFrostbite` (`RaidFixPatches.cs:378-389`,
  `if (GetComponent<FrostbiteEffect>()==null) AddComponent<FrostbiteEffect>()` as an
  `EffectsController.Awake` prefix). The rest of that patch (TOD_Camera/PostProcessResources healing,
  `RaidFixPatches.cs:391-474`) is NOT ported — gated behind their weather/TOD system, out of scope here.

Reused in `ArenaMapPort.Client/BotsDataRepairs.cs`:

- **Ensure `AIStationaryController` exists before `BotsController.Init` uses it** — the
  `AIStationaryController` half of the prefix inside `Patch_EnsureStationaryController`
  (`RaidFixPatches.cs:4118-4126`), as an additional `BotsController.Init` prefix. Their companion
  `ObservedCullingManager` half and the whole Postfix (flares/sealed-doors/spawn-trigger rebuild) are
  NOT ported — `ObservedCullingManager` is already ensured earlier by `EnvironmentRepairs.cs`, and
  doors/flares are out of scope.
- **Assign the nearest core point when `BotOwner.StartCorePoint` is null** — the postfix of
  `Patch_BotStartCorePoint` (`RaidFixPatches.cs:4622-4647`), adapted as `AICorePointFallback.CreatePostfix`
  in `ArenaMapPort.Client/AICorePointRepairs.cs` (same nearest-search loop, our own gate/logging). The
  primary fix in that file — creating the `AICorePointHolder` list and one synthetic `AICorePoint` before
  `BotsController.Init` so `GetCorePoint(0)` resolves — is original: Icebreaker generates its cores from
  recovered bake data, we have none.
- The `AIVoxelesData.VoxelsList` null-list fix in the same file is NOT a port — the equivalent
  Icebreaker patch (`Patch_CoversRestore`, `RaidFixPatches.cs:3711-3884`) is a broad
  exception-swallowing Finalizer that reconstructs partial recovered bake data; we have no bake data at
  all, so a one-line "ensure the list is a non-null empty list before the original method runs" is both
  simpler and (verified against our own decompiled `AIVoxelesData.cs`) sufficient — an original-source
  analysis, not adapted code.

---

## ManimalIcebreaker (weather-rig-stage0 — frozen-skybox fallback)

Source: `D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/IcebreakerSky.cs`

```
MIT License

Copyright (c) 2026 danauraborealis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `ArenaMapPort.Client/WeatherRig.cs`:

- **"Disable dome painter GameObjects" pattern** — `WeatherRig.ApplySkyboxMode`'s `FrozenStatic` mode
  adapts the dome-freezing half of `IcebreakerSky.FreezeSkybox` (`icebreaker-client/IcebreakerSky.cs:
  65-83`): `SetActive(false)` on `TOD_Components.Atmosphere/Clear/Space/Sun/Moon` and the
  `CloudRenderer`'s `GameObject`, leaving `AmbientLight`/`TOD_Time`/wind/rain/the `ReflectionProbe`
  running. Their reasoning (the procedural dome painters are what render a broken flat-white/black
  atmosphere with a horizon seam on a cross-build sky rip) is the same failure mode `SkyboxMode` exists
  to work around here.
- **NOT ported**: `FreezeSkybox`'s own `RenderSettings.skybox = skybox_night` line (assigning a
  replacement retail skybox material) — `ApplySkyboxMode` deliberately does not source/author a
  replacement skybox material for Stage 0 (see its own doc comment for why); and none of
  `IcebreakerSky.TryApply`'s retail-TOD-JSON-reflection-apply half (`Apply`/`Convert`, the bulk of
  `IcebreakerSky.cs`) — not needed here, since this project clones a live, already-correctly-valued
  donor `TOD_Sky` instance rather than reconstructing one field-by-field from a JSON dump of a
  different build's sky.

---

## SPT-InterchangeRework (WP4 — client NavMesh install + shader rebind structure)

Source: `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client`

```
MIT License

Copyright (c) 2026 LennoxP90

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Reused in `ArenaMapPort.Client/NavMeshInstaller.cs`:

- **The whole mechanism** — near-verbatim port of `InterchangeRework.Client/NavMeshExtension.cs`:
  a Harmony prefix on `BotsController.Init` that loads a `NavMeshData` asset from a bundle
  alongside the plugin (by type, not name, via `bundle.LoadAllAssets(typeof(NavMeshData))`),
  keeps it alive across scene sweeps with `HideFlags.DontUnloadUnusedAsset`, and on install calls
  `NavMesh.RemoveAllNavMeshData()` **before** `NavMesh.AddNavMeshData(data)` (removing after would
  drop ours along with the map's own — `NavMeshExtension.cs`'s own documented ordering note),
  cleaning up via `NavMeshDataInstance.Remove()` on `SceneManager.sceneUnloaded` for the owned
  scene. Adapted only in the load-gate (`ArenaEquatorRaid.IsLoaded()` in place of their
  `IrRaid.IsLoaded()`) and the bundle file name/location.

Reused in `ArenaMapPort.Client/ShaderRebind.cs`:

- **Overall structure** — per-raid `Resources.FindObjectsOfTypeAll<Shader>()` name index built
  once and reset on scene unload, an immediate rebind pass on `SceneManager.sceneLoaded` plus a
  delayed sweep pass via a `HideFlags.HideAndDontSave` + `DontDestroyOnLoad` coroutine host, and a
  sidecar JSON file (`material-shaders.<map key>.json`) read once per process and keyed by material name
  (with the `" (Instance)"` suffix stripped) — following the shape of
  `InterchangeRework.Client/MaterialShaderRebind.cs`'s `_byName`/`IntendedShader()`/`Sweeper`/
  `MaterialKey()`.
- **The rebind algorithm itself is NOT reused** — LEAD OVERRIDE O1 (this repo's own design)
  intentionally diverges from `MaterialShaderRebind.cs`'s error-shader/name-collision handling
  (their stand-ins reuse the REAL BSG shader name, so `Shader.Find` can return their own rip and
  they must detect+recover from that after the fact via a `Hidden/InternalErrorShader` sweep and a
  hand-maintained `Substitutes` table) — ours are named `"Hidden/ArenaMapPort/" + <original name>`
  specifically so that collision is structurally impossible, so `ShaderRebind.cs` needs neither an
  error-shader sweep nor a substitution table (§F.3: "no substitution table is needed for the
  spike's material set").

Everything else in `ArenaMapPort.Client` (`BundleHost.cs`, `PerfectCullingSkip.cs`,
`HarmonyPatches.cs`, `Plugin.cs`, `ArenaEquatorRaid.cs`, `SceneDiagnostics.cs`) is original to this
repository — `BundleHost.cs`'s mechanism is independently derived from this project's own
decompiled `BundlesManager.cs` (§F.1's own note: simpler than either reference mod's bundle-hosting
code, since the relevant members are public on this specific decompiled build and need no
`AccessTools` reflection at all). `HarmonyPatches.cs`'s target-resolution/install wiring for the
impl-raidstart-repairs hooks follows this file's own pre-existing AccessTools+Verify convention and is
original; the small pieces of those hooks' own logic that ARE adapted from ManimalIcebreaker are
attributed in the "impl-raidstart-repairs" ManimalIcebreaker section above, per file.

## RaiRaiTheRaichu — HiddenCaches (approach only, no code copied)

Source: https://github.com/RaiRaiTheRaichu/HiddenCaches (tag 1.4.0). The repository carries no LICENSE file;
the Forge listing (sp-mod.com/mod/1265) declares the University of Illinois/NCSA Open Source License.

`ArenaMapPort.Client/ExfilSignals.cs` follows the same idea for the exit smoke signals — retain EFT's airdrop
crate bundle (`assets/content/location_objects/lootable/prefab/scontainer_crate.bundle`), take the
`Flare_Smoke` particle material and colour curve and the crate's `FlareSound` clip, and build fresh Light /
AudioSource / ParticleSystem components — but is an independent re-implementation against the public EFT and
Unity APIs, written from the decompile (`EFT/EasyAssetsExtensions.cs`, `EFT.Airdrop/ClientAirDrop.cs:238-245`).
No line of HiddenCaches is reproduced here; credit to RaiRaiTheRaichu for showing that the crate's effect is
the one reusable always-on smoke in the game.


## UnityPy — chunk compressor guard (build tool, nothing shipped)

Source: https://github.com/K0lb3/UnityPy (version 1.25.2, MIT License).

`tools/bundle/chunk_compressor_guard.py` (`corrected_chunk_based_compress`) is a copy of UnityPy's
`helpers/CompressionHelper.py` `chunk_based_compress` with its two loops merged into one and its size comparisons changed from
`>` to `>=`, so that a chunk whose compressed form is exactly as long as the chunk is stored raw (the 2026-10-29 Equator noise). It is a
build-time tool: it ships in no release ZIP, which is why this licence text is indented here and
is not one of the fenced `MIT License` blocks that `tools/release/make_release_zips.py` (`notices_text`) copies into the
package notices (that gate demands exactly the two blocks above and fails on a third). The permission notice of the source:

    MIT License

    Copyright (c) 2019-2021 K0lb3

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
