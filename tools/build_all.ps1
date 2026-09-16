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
       validate_preset.py itself now also asserts this map's preset bundle carries a unique,
       deterministic internal CAB name (fix-preset-cab-and-bundle-lifetime,
       docs/progress/fix-preset-cab-and-bundle-lifetime.md) -- the whole-DISTRIBUTION check that
       no two STAGED preset/scenes/navmesh bundles share an internal name either lives in
       tools/validate/validate_bundle_names.py, wired into tools/validate/validate_distribution.py
       (run by tools/build_remaining.ps1 -Stage, the multi-map "safe to ship dist/" gate) since
       that is the earliest point a full dist/ actually exists to check.

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

.PARAMETER FullMap
  Require a verified NavMesh bake and every descriptor scene in the preset's scene bundle.
  Use for complete ports; omit only for the existing intentional slice workflow.

.PARAMETER ReproTimeoutSeconds
  Maximum duration of each independent extraction reproducibility run. Larger maps need more
  than the legacy 300-second default; this changes the time allowance, not the checks.

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
    [switch]$FullMap,
    [ValidateRange(1, 86400)][int]$ReproTimeoutSeconds = 300,
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
$mapData = Get-Content -LiteralPath $MapDescriptor -Raw | ConvertFrom-Json
if ($mapData.key -cne $MapKey) {
    throw "MapKey '$MapKey' does not match descriptor key '$($mapData.key)'"
}
if ($FullMap -and (-not $BakeNavMesh -or -not $Verify)) {
    throw '-FullMap requires -BakeNavMesh and -Verify to remain enabled.'
}
$MapScriptsSceneName = $mapData.scripts_scene_name
if ($FullMap) {
    # An explicit build/revalidation attempt supersedes earlier acceptance even if
    # it fails before producing new bundles. Keep the old receipt as audit history,
    # under a name the resume/staging checks never accept.
    $oldReceipt = Join-Path $OutputDir 'full-map-validation.json'
    if (Test-Path -LiteralPath $oldReceipt -PathType Leaf) {
        $supersededReceipt = Join-Path $OutputDir ('full-map-validation.superseded-' + [Guid]::NewGuid().ToString('N') + '.json')
        Move-Item -LiteralPath $oldReceipt -Destination $supersededReceipt
    }
    $oldInputs = Join-Path $OutputDir 'validation-inputs.json'
    if (Test-Path -LiteralPath $oldInputs -PathType Leaf) {
        $supersededInputs = Join-Path $OutputDir ('validation-inputs.superseded-' + [Guid]::NewGuid().ToString('N') + '.json')
        Move-Item -LiteralPath $oldInputs -Destination $supersededInputs
    }
    $inputManifest = Get-Content -LiteralPath (Join-Path $PackageDir 'manifest.json') -Raw | ConvertFrom-Json
    if ($inputManifest.source.map_key -and $inputManifest.source.map_key -cne $MapKey) {
        throw "Extraction identity '$($inputManifest.source.map_key)' does not match '$MapKey'."
    }
    $expectedOrder = @($MapScriptsSceneName) + @($inputManifest.scenes)
    if (($expectedOrder | ConvertTo-Json -Compress) -cne (@($mapData.all_scene_names) | ConvertTo-Json -Compress)) {
        throw 'The full-map descriptor must contain exactly the authored Scripts scene followed by the extraction scenes.'
    }
}

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
$unityLease = $null
$unityLeaseAcquired = $false
try {
    if (-not $SkipGameRefsSync -or -not $SkipUnityBuild) {
        # Independent map validators may run concurrently; this Unity project
        # and its GameRefs import must have exactly one writer at a time.
        $unityLease = [System.Threading.Mutex]::new($false, 'Local\ArenaMapPort.UnityBatch')
        try { $unityLeaseAcquired = $unityLease.WaitOne(0) }
        catch [System.Threading.AbandonedMutexException] { $unityLeaseAcquired = $true }
        if (-not $unityLeaseAcquired) {
            Write-Host '[build_all] Waiting for the current Unity import/build to finish.'
            try { $unityLeaseAcquired = $unityLease.WaitOne() }
            catch [System.Threading.AbandonedMutexException] { $unityLeaseAcquired = $true }
        }
    }
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
    if ($FullMap -and $inputManifest.source.map_key) {
        & python -u (Join-Path $RepoRoot 'tools/validate/target_physics.py') --output (Join-Path $OutputDir 'target-physics.json')
        if ($LASTEXITCODE -ne 0) { Fail 'Could not read target SPT collision rules.' }
    }
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
    [pscustomobject]@{schema_version=1;map_key=$MapKey;exit_code=$proc.ExitCode;log_file=[IO.Path]::GetFileName($logFile)} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'unity-run.json') -Encoding utf8
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
    if ($FullMap) {
        $priorRun = Get-Content -LiteralPath (Join-Path $OutputDir 'unity-run.json') -Raw | ConvertFrom-Json
        if ($priorRun.map_key -cne $MapKey -or [IO.Path]::GetFileName($priorRun.log_file) -cne $priorRun.log_file) {
            Fail 'Prior Unity run provenance does not match the selected map.'
        }
        $logFile = Join-Path $OutputDir $priorRun.log_file
        $scan = Test-UnityBuildLog -LogFile $logFile -ExitCode $priorRun.exit_code
        if (-not $scan.Ok) { Fail "Prior Unity build failed current log validation: $($scan.Problems -join '; ')" }
    }
}

# ---------------------------------------------------------------------------
} finally {
    if ($unityLeaseAcquired) { $unityLease.ReleaseMutex() }
    if ($null -ne $unityLease) { $unityLease.Dispose() }
}

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
    & python -u (Join-Path $RepoRoot "tools/bundle/patch_monoscript_identity.py") --bundle $b.FullName
    if ($LASTEXITCODE -ne 0) { Fail "patch_monoscript_identity.py FAILED for $($b.FullName) (exit $LASTEXITCODE) -- see output above" }
}

# ---------------------------------------------------------------------------
# Step 4 — all three SS B.WP6 validators, strict/default flags. MANDATORY.
# ---------------------------------------------------------------------------
Write-Step "4/4 validators (strict/default flags)"

if ($FullMap) {
    if ($inputManifest.source.map_key) {
        # Regenerate cheap descriptor outputs from the current actual SPT donors,
        # including an explicit validation pass that reuses existing Unity bundles.
        & python -u (Join-Path $RepoRoot 'tools/server/make_base_json.py') --map $MapDescriptor
        if ($LASTEXITCODE -ne 0) { Fail 'Current server base generation failed.' }
        & python -u (Join-Path $RepoRoot 'tools/preset/patch_preset.py') --map $MapDescriptor
        if ($LASTEXITCODE -ne 0) { Fail 'Current preset generation failed.' }
    }
    $validationInputs = Join-Path $OutputDir 'validation-inputs.json'
    & python -u (Join-Path $RepoRoot 'tools/validate/port_inputs.py') capture --package $PackageDir --output $validationInputs
    if ($LASTEXITCODE -ne 0) { Fail 'Could not bind the complete package/source/build/validation inputs.' }
}

& python -u (Join-Path $RepoRoot "tools/validate/validate_extract.py") --package $PackageDir --extractor-timeout $ReproTimeoutSeconds
if ($LASTEXITCODE -ne 0) { Fail "validate_extract.py FAILED (exit $LASTEXITCODE)" }

$scenesBundle = Join-Path $OutputDir $ScenesBundleName
$navmeshBundle = Join-Path $OutputDir $NavmeshBundleName
$materialShaders = Join-Path $OutputDir "material-shaders.json"
$editorVerify = Join-Path $OutputDir "editor_verify.json"
$roundtripOptions = @()
if ($FullMap) { $roundtripOptions += '--require-navmesh' }
& python -u (Join-Path $RepoRoot "tools/validate/validate_bundle_roundtrip.py") `
    --scenes-bundle $scenesBundle --navmesh-bundle $navmeshBundle --package $PackageDir `
    --material-shaders $materialShaders --editor-verify $editorVerify `
    --scripts-scene-name $MapScriptsSceneName @roundtripOptions
if ($LASTEXITCODE -ne 0) { Fail "validate_bundle_roundtrip.py FAILED (exit $LASTEXITCODE) -- a SptGameRefs-* assembly name surviving to this point is now a hard FAIL, not a warning" }

$presetOptions = @()
if (-not $FullMap) { $presetOptions += '--allow-missing-scenes' }
& python (Join-Path $RepoRoot "tools/validate/validate_preset.py") --map $MapDescriptor --scenes-bundle $scenesBundle @presetOptions
if ($LASTEXITCODE -ne 0) { Fail "validate_preset.py FAILED (exit $LASTEXITCODE)" }

# Whole-registry check, independent of which map this invocation built -- catches tools/maps/*.json
# drifting from ArenaMapDescriptor.cs's ArenaMapRegistry or ArenaPaths.cs's EquatorDefault().
& python (Join-Path $RepoRoot "tools/validate/validate_map_descriptors.py")
if ($LASTEXITCODE -ne 0) { Fail "validate_map_descriptors.py FAILED (exit $LASTEXITCODE)" }

if ($FullMap) {
    & python -u (Join-Path $RepoRoot 'tools/validate/port_inputs.py') verify --package $PackageDir --snapshot $validationInputs
    if ($LASTEXITCODE -ne 0) { Fail 'Inputs changed during full validation; no accepted receipt will be written.' }
    # Bind the completed validations to the exact outputs and source selection. Later staging
    # verifies these hashes; the receipt is written only after every mandatory validator passes.
    $packageManifest = Get-Content -LiteralPath (Join-Path $PackageDir 'manifest.json') -Raw | ConvertFrom-Json
    $receiptPaths = @(
        $MapDescriptor, (Join-Path $PackageDir 'manifest.json'),
        $scenesBundle, $navmeshBundle, $materialShaders, $editorVerify,
        (Join-Path $OutputDir 'unity-run.json'), $logFile, $validationInputs,
        (Join-Path $RepoRoot "work/build/preset/${MapKey}_preset.bundle"),
        (Join-Path $RepoRoot "ArenaMapPort.Server/db/$($mapData.db_base_json_path)")
    )
    if ($packageManifest.source.source_plan) {
        $receiptPaths += Join-Path $RepoRoot $packageManifest.source.source_plan
        $receiptPaths += Join-Path $OutputDir 'target-physics.json'
        $receiptPaths += Join-Path $OutputDir 'spawn-placement.json'
    }
    $receiptFiles = foreach ($receiptPath in $receiptPaths) {
        $resolved = (Resolve-Path -LiteralPath $receiptPath).Path
        [pscustomobject]@{
            path = [System.IO.Path]::GetRelativePath($RepoRoot, $resolved).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [pscustomobject]@{
        schema_version = 2
        full_map = $true
        map_key = $MapKey
        validated_utc = [DateTime]::UtcNow.ToString('o')
        reproducibility_timeout_seconds = $ReproTimeoutSeconds
        files = @($receiptFiles)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDir 'full-map-validation.json') -Encoding utf8
}

Write-Host ""
Write-Host "[build_all] OK -- build + mandatory identity patch + all validators passed. $OutputDir is safe to copy into dist/." -ForegroundColor Green
exit 0
