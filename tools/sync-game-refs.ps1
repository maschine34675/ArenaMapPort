<#
.SYNOPSIS
  ArenaMapPort WP2 — syncs the Editor-only GameRefs Plugin DLLs (SPEC.md §B.WP2.1, LEAD OVERRIDE O2).

.DESCRIPTION
  Copies the TRANSITIVE reference closure of Assembly-CSharp.dll (+ Assembly-CSharp-firstpass.dll)
  from D:/SPT41/EscapeFromTarkov_Data/Managed into
  UnityProject/Assets/ArenaMapPort/Editor/GameRefs/, excluding assemblies the Unity 2022.3.43f1
  editor already provides (UnityEngine*.dll, UnityEditor*, mscorlib, netstandard, System*.dll,
  Mono.*) and resolving duplicates of installed project packages.

  The closure itself is computed from real PE/ECMA-335 assembly metadata (never by guessing, never
  by loading/executing the candidate DLLs) via tools/gamerefs-closure (System.Reflection.Metadata).

  GameRefs/*.dll is NEVER committed (BSG-derived, decision #9) — this script is the reproducible
  setup step every fresh checkout runs once (and re-runs whenever the live SPT install's Managed
  folder changes).

  After copying, this script also regenerates
  UnityProject/Assets/ArenaMapPort/Editor/ArenaMapPort.Editor.asmdef's `precompiledReferences`
  array so it exactly matches what actually landed in GameRefs/ (every other asmdef field is left
  untouched) — this is the "single source of truth is the real DLL set" guarantee O2 asks for.

.PARAMETER ManagedDir
  Source, read-only. D:/SPT41/EscapeFromTarkov_Data/Managed by default.

.PARAMETER RepoRoot
  ArenaMapPort repo root. Defaults to the parent of this script's directory.

.PARAMETER GameRefsDir
  Destination. Defaults to UnityProject/Assets/ArenaMapPort/Editor/GameRefs under RepoRoot.

.EXAMPLE
  pwsh tools/sync-game-refs.ps1
#>
[CmdletBinding()]
param(
    [string]$ManagedDir = "D:/SPT41/EscapeFromTarkov_Data/Managed",
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$GameRefsDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GameRefsDir)) {
    $GameRefsDir = Join-Path $RepoRoot "UnityProject/Assets/ArenaMapPort/Editor/GameRefs"
}

Write-Host "== ArenaMapPort: sync-game-refs =="
Write-Host "RepoRoot:    $RepoRoot"
Write-Host "ManagedDir:  $ManagedDir"
Write-Host "GameRefsDir: $GameRefsDir"

if (-not (Test-Path $ManagedDir)) {
    throw "Managed dir not found: $ManagedDir (read-only source, O8 — is D:/SPT41 mounted/present?)"
}

# O4: this script must never write into D:/SPT41 or X:/. Guard defensively.
foreach ($p in @($GameRefsDir)) {
    $full = [System.IO.Path]::GetFullPath($p)
    if ($full -like "D:\SPT41*" -or $full -like "X:\*" -or $full -like "X:/*") {
        throw "Refusing to write into a read-only/protected source path: $full (O4/O8)"
    }
}

# ---------------------------------------------------------------------------
# 1. Build the closure tool (System.Reflection.Metadata-based, tools/gamerefs-closure) if needed.
# ---------------------------------------------------------------------------
$closureToolDir = Join-Path $RepoRoot "tools/gamerefs-closure"
$closureToolDll = Join-Path $closureToolDir "bin/Release/net9.0/GameRefsClosure.dll"

if (-not (Test-Path $closureToolDll)) {
    Write-Host "Building GameRefsClosure tool..."
    dotnet build $closureToolDir -c Release | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "GameRefsClosure build failed (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------------------
# 2. Compute the transitive closure against the real Managed folder metadata.
# ---------------------------------------------------------------------------
$work = Join-Path ([System.IO.Path]::GetTempPath()) "arenamapport-gamerefs"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$closureFile = Join-Path $work "closure.tsv"
$roots = "Assembly-CSharp;Assembly-CSharp-firstpass"

Write-Host "Computing transitive closure from roots: $roots"
dotnet $closureToolDll $ManagedDir $roots $closureFile | Write-Host
if ($LASTEXITCODE -ne 0) { throw "GameRefsClosure run failed (exit $LASTEXITCODE)" }

$closureLines = Get-Content $closureFile | Where-Object { $_.Trim() -ne "" }
Write-Host "Closure computed: $($closureLines.Count) assemblies present under $ManagedDir"

$unresolvedFile = "$closureFile.unresolved.txt"
if (Test-Path $unresolvedFile) {
    $unresolved = Get-Content $unresolvedFile | Where-Object { $_.Trim() -ne "" }
    if ($unresolved.Count -gt 0) {
        Write-Warning "Referenced but not found under ${ManagedDir}: $($unresolved -join ', ')"
    }
}

# ---------------------------------------------------------------------------
# 3. Filter: exclude what the Editor already provides (O2's literal exclude list), plus
#    documented duplicates of installed UPM packages.
# ---------------------------------------------------------------------------
$excludePrefixes = @("UnityEngine", "UnityEditor", "System", "Mono.")
$excludeExact = @("mscorlib", "netstandard")

# O2 says "do not guess" and §G.S1 exists specifically to verify this empirically — this pass's
# first real S1 batchmode run DID surface two false positives from the coarse prefix rule above,
# via the exact Editor-log error text O2 calls out to check for ("will not be loaded due to
# errors" / "Unable to resolve reference"):
#   Assembly 'Assets/.../GameRefs/Assembly-CSharp.dll' will not be loaded due to errors:
#     Unable to resolve reference 'UnityEngine.UI'.
#     Unable to resolve reference 'System.Runtime.CompilerServices.Unsafe'.
#   (same 'UnityEngine.UI' failure also broke Assembly-CSharp-firstpass.dll, Cinemachine.dll,
#   Unity.TextMeshPro.dll, DOTween.Modules.dll, which all reference it too)
# Neither is actually provided by this Editor install out of the box, despite matching an
# "editor-provided" prefix — both are real files under $ManagedDir and already flow through the
# closure computation above; carved out of the prefix exclusion so they get copied like any other
# required dependency.
#
# Tried and reverted: routing 'UnityEngine.UI' through the com.unity.ugui@1.0.0 built-in package
# instead of copying SPT's own DLL (to sidestep a native Editor crash seen with the raw DLL, see
# below) — that package's own bundled source fails to compile against this exact Editor install
# (Library/PackageCache/com.unity.ugui@1.0.0/Runtime/.../MaskUtilities.cs etc.: CS0117
# "ListPool<Canvas> does not contain a definition for 'Get'/'Release'" — a real, reproducible
# compile error in Unity's own bundled package source, not something this project can fix), so the
# raw-DLL route is what's actually in use below despite the crash noted next; see docs/progress
# for the current status of isolating/working around it (Library wipe + retest in progress).
$includeOverrides = @("UnityEngine.UI", "System.Runtime.CompilerServices.Unsafe")

# Duplicate-of-installed-project-package exclusions (O2: "resolving duplicates of project
# packages ... or skip the duplicate DLL - document which"). Each entry names the DLL and WHY.
$excludePackageDuplicates = @{
    "Unity.AI.Navigation" = "duplicates UnityProject/Packages 'com.unity.ai.navigation' 1.1.5 (pinned, §B.WP2.7); none of WP2's required EFT types (LevelSettings/LocationScene/BotZone/LocationExportInfo/SpawnPointMarker/SpawnPoint) expose a member typed as a Unity.AI.Navigation type, so the game's own copy is not needed in GameRefs — the NavMesh bake driver uses the installed UPM package's assembly instead. Kept OUT of GameRefs to avoid a duplicate-assembly-name error in the Editor."
}

$toCopy = New-Object System.Collections.Generic.List[object]
$excluded = New-Object System.Collections.Generic.List[object]

foreach ($line in $closureLines) {
    $parts = $line -split "`t", 2
    if ($parts.Count -ne 2) { continue }
    $name = $parts[0]
    $path = $parts[1]

    $isExcluded = $false
    $reason = $null

    if ($includeOverrides -contains $name) {
        $toCopy.Add([pscustomobject]@{ Name = $name; Path = $path })
        continue
    }

    foreach ($p in $excludePrefixes) {
        if ($name -like "$p*") { $isExcluded = $true; $reason = "editor-provided (prefix '$p')"; break }
    }
    if (-not $isExcluded -and ($excludeExact -contains $name)) {
        $isExcluded = $true; $reason = "editor-provided (exact)"
    }
    if (-not $isExcluded -and $excludePackageDuplicates.ContainsKey($name)) {
        $isExcluded = $true; $reason = $excludePackageDuplicates[$name]
    }

    if ($isExcluded) {
        $excluded.Add([pscustomobject]@{ Name = $name; Reason = $reason })
    } else {
        $toCopy.Add([pscustomobject]@{ Name = $name; Path = $path })
    }
}

Write-Host "To copy: $($toCopy.Count)   Excluded: $($excluded.Count)"

# ---------------------------------------------------------------------------
# 4. Clean + copy into GameRefs/.
# ---------------------------------------------------------------------------
if (Test-Path $GameRefsDir) {
    Get-ChildItem $GameRefsDir -Filter "*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force
} else {
    New-Item -ItemType Directory -Force -Path $GameRefsDir | Out-Null
}

foreach ($e in $toCopy) {
    $dest = Join-Path $GameRefsDir (Split-Path $e.Path -Leaf)
    Copy-Item -Path $e.Path -Destination $dest -Force
}

$manifest = $toCopy | Sort-Object Name | ForEach-Object { "$($_.Name).dll" }
$manifestFile = Join-Path $GameRefsDir "_gamerefs-manifest.txt"
"# Generated by tools/sync-game-refs.ps1 — DO NOT EDIT. $(Get-Date -Format o)" | Set-Content -Path $manifestFile -Encoding UTF8
$manifest | Add-Content -Path $manifestFile -Encoding UTF8

$excludedFile = Join-Path $GameRefsDir "_gamerefs-excluded.txt"
"# Generated by tools/sync-game-refs.ps1 — assemblies in the closure that were NOT copied, and why." | Set-Content -Path $excludedFile -Encoding UTF8
$excluded | Sort-Object Name | ForEach-Object { "$($_.Name)`t$($_.Reason)" } | Add-Content -Path $excludedFile -Encoding UTF8

Write-Host "Copied $($manifest.Count) DLLs to $GameRefsDir"

# ---------------------------------------------------------------------------
# 5. Regenerate ArenaMapPort.Editor.asmdef's precompiledReferences to match exactly.
# ---------------------------------------------------------------------------
$asmdefPath = Join-Path $RepoRoot "UnityProject/Assets/ArenaMapPort/Editor/ArenaMapPort.Editor.asmdef"
if (Test-Path $asmdefPath) {
    $asmdefObj = Get-Content $asmdefPath -Raw | ConvertFrom-Json
    $asmdefObj.precompiledReferences = @($manifest)
    ($asmdefObj | ConvertTo-Json -Depth 10) | Set-Content -Path $asmdefPath -Encoding UTF8
    Write-Host "Updated $asmdefPath -> precompiledReferences ($($manifest.Count) entries)"
} else {
    Write-Warning "asmdef not found at $asmdefPath — skipping precompiledReferences sync (create it first)."
}

Write-Host "== sync-game-refs complete =="
Write-Host "Next: open UnityProject in Unity (or run the batchmode pipeline) — GameRefsPostprocessor.cs"
Write-Host "will set each DLL's PluginImporter to Editor + StandaloneWindows64 compatibility on import (O2)."
