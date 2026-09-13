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
  sidecar JSON file (`material-shaders.json`) read once per process and keyed by material name
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
`AccessTools` reflection at all).
