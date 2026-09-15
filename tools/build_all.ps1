<#
.SYNOPSIS
  ArenaMapPort pipeline orchestrator (task label `identity-fix`, item 2) — the ONLY sanctioned way
  to produce an S1/S2 build that is safe to copy into dist/. Wraps GameRefs sync -> Unity
  BuildBundles -> the MANDATORY MonoScript-identity post-build patch -> all three SS B.WP6
  validators, and fails loudly (non-zero exit) the moment any step does.

.DESCRIPTION
  BuildBundles.cs (the Unity -executeMethod pipeline) builds arena_equator_scenes.bundle /
  arena_equator_navmesh.bundle using GameRefs' Editor-time-renamed
  SptGameRefs-AssemblyCSharp[Firstpass].dll (tools/gamerefs-fixup / tools/sync-game-refs.ps1,
  docs/progress/s1-fix-r1.md) — required to get past Unity's AddComponent blocker at BUILD time.
  That rename LEAKS into the built bundle's MonoScript.m_AssemblyName for every SS D EFT
  component, which the REAL EFT client cannot resolve (see
  tools/bundle/patch_monoscript_identity.py's module docstring for the full defect writeup) — so a
  bundle straight out of BuildBundles.cs is NOT safe to ship. This script makes the fix-up step
  IMPOSSIBLE to skip by accident: it always runs immediately after the Unity build, against every
  bundle the build produced, before any validator or dist-copy step sees the output.

  Steps (each one gates the next — this script `exit`s non-zero the moment one fails, it never
  silently continues past a failure):
    1. tools/sync-game-refs.ps1                                (skip with -SkipGameRefsSync)
    2. Unity.exe -batchmode ... BuildBundles.Run                (skip with -SkipUnityBuild, e.g.
       to only re-patch+re-validate an already-built work/build/<slice>)
       Passes only if Unity exited 0, logged `[BuildBundles] DONE`, and its log has no compile
       error, batchmode abort, crash signal or non-allowlisted exception -- see
       tools/unity-log-scan.ps1 for the exact rules and the one allowlisted editor-startup source.
    3. tools/bundle/patch_monoscript_identity.py against every
       *.bundle BuildBundles.Run produced (scenes AND navmesh)  -- MANDATORY, never skippable
    4. tools/validate/validate_extract.py
       tools/validate/validate_bundle_roundtrip.py (strict, default flags)
       tools/validate/validate_preset.py --allow-missing-scenes
       tools/validate/validate_map_descriptors.py -- MANDATORY, never skippable. The last one
       cross-checks tools/maps/*.json against ArenaMapDescriptor.cs's ArenaMapRegistry entries and
       ArenaPaths.cs's EquatorDefault() (not just the map under test) -- it is a whole-registry
       consistency check, independent of $MapKey/$MapDescriptor.

.PARAMETER PackageDir
  WP1 package to build from. Defaults to the S1 slice (O7).

.PARAMETER OutputDir
  Where BuildBundles.Run writes bundles + editor_verify.json. Defaults to the S1 slice (O7).

.PARAMETER BakeNavMesh
  Passed through to BuildBundles.Run's -bakeNavMesh. Default $true.

.PARAMETER Verify
  Passed through to BuildBundles.Run's -verify (writes editor_verify.json). Default $true.

.PARAMETER SkipGameRefsSync
  Skip step 1 (assume GameRefs/ is already synced+fixed-up).

.PARAMETER SkipUnityBuild
  Skip step 2 entirely (assume $OutputDir already holds a built arena_equator_scenes.bundle /
  arena_equator_navmesh.bundle from a prior run) — steps 3+4 (the mandatory identity patch +
  validators) still run against whatever is already there. Useful for re-running just the
  mandatory patch/validate steps without paying Unity's ~5 minute startup+import cost again.

.PARAMETER UnityExe
  Path to Unity.exe. Defaults to the Unity 2022.3.43f1 install this workspace's tooling note
  documents.

.EXAMPLE
  # full real build, S1 slice, in the background (Unity takes several minutes):
  pwsh tools/build_all.ps1

.EXAMPLE
  # already have a work/build/s1 from a prior Unity run -- just (re-)apply the mandatory identity
  # patch and re-run all validators against it:
  pwsh tools/build_all.ps1 -SkipGameRefsSync -SkipUnityBuild
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # Multimap generalization (docs/progress/multimap-generalize.md): -MapKey defaults to
    # "arena_equator" so every existing invocation (no -MapKey passed) is byte-for-byte
    # unchanged. -MapDescriptor defaults to tools/maps/<key-without-"arena_"-prefix>.json (the
    # naming convention tools/maps/equator.json / tools/maps/bay5.json already follow) and is
    # passed to BuildBundles.Run as -mapDescriptor; PackageDir/OutputDir stay independent
    # parameters (still S1-Equator defaults) since which extraction package to build FROM is
    # orthogonal to which map identity to build AS.
    [string]$MapKey = "arena_equator",
    [string]$MapDescriptor = $null,
    [string]$PackageDir = "D:/ArenaMapPort/work/extract/equator_s1",
    [string]$OutputDir = "D:/ArenaMapPort/work/build/s1",
    [bool]$BakeNavMesh = $true,
    [bool]$Verify = $true,
    [switch]$SkipGameRefsSync,
    [switch]$SkipUnityBuild,
    [string]$UnityExe = "C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Unity.exe"
)

$ErrorActionPreference = "Stop"

if (-not $MapDescriptor) {
    $mapFileStem = $MapKey -replace '^arena_', ''
    $MapDescriptor = Join-Path $RepoRoot "tools/maps/$mapFileStem.json"
}
if (-not (Test-Path -LiteralPath $MapDescriptor)) {
    Write-Host "[build_all] FATAL: map descriptor not found: $MapDescriptor (pass -MapDescriptor explicitly, or add tools/maps/<key>.json)" -ForegroundColor Red
    exit 1
}
$ScenesBundleName = "${MapKey}_scenes.bundle"
$NavmeshBundleName = "${MapKey}_navmesh.bundle"
$MapScriptsSceneName = (Get-Content -LiteralPath $MapDescriptor -Raw | ConvertFrom-Json).scripts_scene_name

. (Join-Path $PSScriptRoot "unity-log-scan.ps1")

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==== [build_all] $msg ====" -ForegroundColor Cyan
}

function Fail($msg) {
    Write-Host "[build_all] FATAL: $msg" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Step 1 — GameRefs sync (+ the Cecil identity/SIGSEGV fixups it owns)
# ---------------------------------------------------------------------------
if (-not $SkipGameRefsSync) {
    Write-Step "1/4 tools/sync-game-refs.ps1"
    & pwsh (Join-Path $RepoRoot "tools/sync-game-refs.ps1")
    if ($LASTEXITCODE -ne 0) { Fail "sync-game-refs.ps1 exited $LASTEXITCODE" }
} else {
    Write-Step "1/4 SKIPPED (-SkipGameRefsSync)"
}

# ---------------------------------------------------------------------------
# Step 2 — Unity BuildBundles.Run (the real headless pipeline)
# ---------------------------------------------------------------------------
if (-not $SkipUnityBuild) {
    Write-Step "2/4 Unity BuildBundles.Run -> $OutputDir"
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $logFile = Join-Path $OutputDir "unity_build_all_$stamp.log"
    $unityArgs = @(
        "-batchmode", "-nographics",
        "-projectPath", (Join-Path $RepoRoot "UnityProject"),
        "-executeMethod", "ArenaMapPort.Editor.BundleBuild.BuildBundles.Run",
        "-packageDir", $PackageDir,
        "-outputDir", $OutputDir,
        "-mapDescriptor", $MapDescriptor,
        "-bakeNavMesh", $(if ($BakeNavMesh) { "true" } else { "false" }),
        "-verify", $(if ($Verify) { "true" } else { "false" }),
        "-logFile", $logFile
        # NOTE: deliberately NO -quit -- BuildBundles.Run owns EditorApplication.Exit() itself
        # (s1-finish fix, see BuildBundles.cs's file header) because the Scripts-scene authoring
        # step needs real EditorApplication.update ticks to run at all.
    )
    Write-Host "[build_all] $UnityExe $($unityArgs -join ' ')"
    $proc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru -Wait -NoNewWindow
    $scan = Test-UnityBuildLog -LogFile $logFile -ExitCode $proc.ExitCode
    foreach ($l in $scan.IgnoredLines) {
        Write-Host "[build_all] ignored benign Unity log line $($l.Line) ($($l.Category)): $($l.Text)" -ForegroundColor DarkYellow
    }
    if (-not $scan.Ok) {
        if (Test-Path -LiteralPath $logFile) {
            Write-Host "[build_all] Unity log tail ($logFile):" -ForegroundColor Yellow
            Get-Content $logFile -Tail 60
        }
        foreach ($l in ($scan.FatalLines | Select-Object -First 20)) {
            Write-Host "[build_all] fatal Unity log line $($l.Line): $($l.Text)" -ForegroundColor Red
        }
        if ($scan.FatalLines.Count -gt 20) { Write-Host "[build_all] ... and $($scan.FatalLines.Count - 20) more fatal line(s)" -ForegroundColor Red }
        Fail "Unity BuildBundles.Run failed ($($scan.Problems -join '; ')) -- see $logFile"
    }
    Write-Host "[build_all] Unity BuildBundles.Run OK -- log: $logFile"
} else {
    Write-Step "2/4 SKIPPED (-SkipUnityBuild) -- using whatever is already in $OutputDir"
}

# ---------------------------------------------------------------------------
# Step 3 — MANDATORY MonoScript identity patch, every *.bundle BuildBundles produced.
# This step is NEVER skippable via a script switch -- it is the whole point of this
# orchestrator (task label `identity-fix`, item 2): no unpatched bundle may reach dist.
# ---------------------------------------------------------------------------
Write-Step "3/4 tools/bundle/patch_monoscript_identity.py (MANDATORY, every bundle)"
$bundles = Get-ChildItem -Path $OutputDir -Filter "*.bundle" -File -ErrorAction SilentlyContinue
if (-not $bundles -or $bundles.Count -eq 0) {
    Fail "no *.bundle files found under $OutputDir to patch -- did step 2 actually produce output?"
}
foreach ($b in $bundles) {
    Write-Host "[build_all] patching $($b.FullName)"
    & python (Join-Path $RepoRoot "tools/bundle/patch_monoscript_identity.py") --bundle $b.FullName
    if ($LASTEXITCODE -ne 0) { Fail "patch_monoscript_identity.py FAILED for $($b.FullName) (exit $LASTEXITCODE) -- see output above" }
}

# ---------------------------------------------------------------------------
# Step 4 — all three SS B.WP6 validators, strict/default flags. MANDATORY.
# ---------------------------------------------------------------------------
Write-Step "4/4 validators (strict/default flags)"

& python (Join-Path $RepoRoot "tools/validate/validate_extract.py") --package $PackageDir
if ($LASTEXITCODE -ne 0) { Fail "validate_extract.py FAILED (exit $LASTEXITCODE)" }

$scenesBundle = Join-Path $OutputDir $ScenesBundleName
$navmeshBundle = Join-Path $OutputDir $NavmeshBundleName
$materialShaders = Join-Path $OutputDir "material-shaders.json"
$editorVerify = Join-Path $OutputDir "editor_verify.json"
& python (Join-Path $RepoRoot "tools/validate/validate_bundle_roundtrip.py") `
    --scenes-bundle $scenesBundle --navmesh-bundle $navmeshBundle --package $PackageDir `
    --material-shaders $materialShaders --editor-verify $editorVerify `
    --scripts-scene-name $MapScriptsSceneName
if ($LASTEXITCODE -ne 0) { Fail "validate_bundle_roundtrip.py FAILED (exit $LASTEXITCODE) -- a SptGameRefs-* assembly name surviving to this point is now a hard FAIL, not a warning" }

& python (Join-Path $RepoRoot "tools/validate/validate_preset.py") --map $MapDescriptor --scenes-bundle $scenesBundle --allow-missing-scenes
if ($LASTEXITCODE -ne 0) { Fail "validate_preset.py FAILED (exit $LASTEXITCODE)" }

# Whole-registry check, independent of which map this invocation built -- catches tools/maps/*.json
# drifting from ArenaMapDescriptor.cs's ArenaMapRegistry or ArenaPaths.cs's EquatorDefault().
& python (Join-Path $RepoRoot "tools/validate/validate_map_descriptors.py")
if ($LASTEXITCODE -ne 0) { Fail "validate_map_descriptors.py FAILED (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "[build_all] OK -- build + mandatory identity patch + all validators passed. $OutputDir is safe to copy into dist/." -ForegroundColor Green
exit 0
