# Equator shader-wide specular ceiling

## 2026-09-21 — implementation and offline checkpoint

The user's uncovered-atrium F10 targets (sandbags, table/bookcase, pallet, armored-car door and
mesh floor decals) still use bright original `p0/Reflective` materials. Roof coverage is not a
reliable selector for those objects. The previous host A/B established that this shader family's
deferred sky reflection is driven by `_SpecColor.rgb × _Specularness`; see
[donor-settings-camera-weather.md](donor-settings-camera-weather.md) §20.3–20.4.

### Behavior

New live F12 key: **Lighting (arena_equator) → ReflectiveSpecularCap**, range 0–1, default **0.30**.
Every other map, including Bay5, defaults to **1 (disabled)**. This is a new key; existing lighting
values and `DefaultsRevision` are not migrated or rewritten.

The repair scans the current map's own mesh renderers after raid start and shader rebinding.
It includes small props, mesh decals, and inactive LODs, regardless of cover or renderer name.
Player descendants, other maps' scenes, vanilla scenes, other shader families and incorrectly
typed shader properties are excluded. The weather donor can be Off; no roof photo is required.

The ceiling is applied to the strongest channel of the **effective specular product**. For example:

| Uncapped specular RGB | Specularness | Cap | Resulting RGB | Effective maximum |
|---|---:|---:|---|---:|
| (1, 0.5, 0.25) | 1 | 0.30 | (0.30, 0.15, 0.075) | 0.30 |
| (1, 0.5, 0.25) | 0.9 | 0.30 | (0.3333, 0.1667, 0.0833) | 0.30 |
| (0.3, 0.15, 0.075) | 0.27 | 0.30 | unchanged | 0.081 |

Only `_SpecColor` RGB is reduced, with hue and alpha preserved. `_Specularness`, diffuse color,
smoothness and `_ReflectColor` are not changed. Missing `_Specularness` means a direct specular
color (factor 1). Invalid/non-finite values are skipped. **1 disables the ceiling**, including for
authored values above 1; 0 removes the specular term and can make spec-only props appear black.

The existing `IndoorReflectionStrength` semantics stay unchanged: it already scales color and
strength individually, so its effective product includes both factors. The new ceiling works on
that result and does not multiply it again when it is already below 0.30. Resetting the new key to
1 therefore restores the previous indoor-tuned appearance, not the pre-indoor original look.

### Material ownership and live changes

`ReflectiveSpecularRepairs` owns an outer layer of renderer-local material copies. Shared material
assets are never written. It restores the underlying material slots before the indoor classifier
reads, rebuilds or updates its own copies, and before LightingLab changes ground smoothness. The
ceiling is then recomputed from those uncapped inputs. Nested changes are handled as one scope;
teardown cancels a scope rather than recreating material copies after the raid.

Changing 0.30 → 0.60 → 0.30 → 1 is original-relative and reversible. Unload restores the inputs
before destroying owned copies; an unrelated component's replacement slot is preserved. A scan
failure restores the partial changes and logs the failure. A later configuration change retries.

F9 includes a `SpecularCap` line with cap, readiness, matching slots, capped slots, mesh decals and
renderer count. F10 identifies changed materials with the `ArenaMapPort SpecularCap clone` suffix.

### Offline verification

Measured on 2026-09-21: **34 focused tests passed**, followed by the real client Release build with
**0 warnings and 0 errors**. The script printed `SPECULAR_CAP_CHECKS_PASSED`; leaf gate G1 recorded
the successful oracle. Test compilation additionally reports three nullability warnings in the
pre-existing scene gate compiled under the test project's stricter nullable context, and one existing
`BaseJsonTests` warning. These are not client build warnings.

`tools/tests/test_specular_cap.ps1` compiles the production `SpecularCapPolicy`, repair and scene
gate into the server test assembly with a small test-only Unity facade, then builds the real client
in Release with DLL-only staging. Cases cover effective product arithmetic; no second indoor
scaling; exact shader/property filtering; mesh decals; shared/vanilla/player/other-map isolation;
preserved alpha and non-specular properties; repeated live values; nested reclassification;
partial-failure rollback; unload and the next raid. The facade tests material identity and lifecycle,
not rendering, Unity's native destruction timing, batching, or the visual quality of 0.30.

Native acceptance still belongs to a restarted EFT process with the new DLL: compare an uncovered
prop/decal at cap 1, 0.30, then 1; verify the existing lounge appearance stays unchanged when already
below the ceiling, and test Bay5 with its default disabled ceiling. Append actual timestamps and
observations below after that run. No host visual proof is claimed by this implementation checkpoint.
