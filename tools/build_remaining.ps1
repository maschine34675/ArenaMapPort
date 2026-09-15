<#
.SYNOPSIS
  Extract and build the twelve additional Arena layouts, one Unity build at a time.
.DESCRIPTION
  Resumes only from verified full-map receipts. Existing extraction packages are reused and
  checked by build_all's mandatory source/reproducibility validators. An incomplete extraction
  directory is never overwritten. -Rebuild rebuilds bundles from complete existing packages.
  -Stage runs server tests, builds the client Release distribution and verifies every staged map.
  This script does not deploy to the live SPT installation.
.EXAMPLE
  .\tools\build_remaining.ps1
.EXAMPLE
  .\tools\build_remaining.ps1 -Maps autoservice,airpit -Rebuild
.EXAMPLE
  .\tools\build_remaining.ps1 -Stage
#>
[CmdletBinding()]
param(
    [string[]]$Maps = @('autoservice','airpit','airpit2','bowl','prison','railwaystation','yard','iceberg','factory','custom','shoreline','woods'),
    [ValidateRange(1,86400)][int]$ReproTimeoutSeconds = 1800,
    [switch]$Rebuild,
    [switch]$Stage
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$allMaps = @('autoservice','airpit','airpit2','bowl','prison','railwaystation','yard','iceberg','factory','custom','shoreline','woods')
if (-not $Maps.Count -or (@($Maps | Sort-Object -Unique).Count -ne $Maps.Count)) { throw 'Specify a nonempty unique map list.' }
foreach ($stem in $Maps) { if ($stem -cnotin $allMaps) { throw "Unknown additional map: $stem" } }

Push-Location -LiteralPath $repo
try {
    New-Item -ItemType Directory -Force -Path 'work/logs/remaining-maps' | Out-Null
    foreach ($stem in $Maps) {
        $log = "work/logs/remaining-maps/$stem-pipeline.log"
        if (-not $Rebuild -and (Test-Path -LiteralPath "work/build/$stem/full-map-validation.json")) {
            & python -u tools/validate/validate_port.py --map $stem *>> $log
            if ($LASTEXITCODE -eq 0) { Write-Output "PORT_RESUMED $stem"; continue }
        }
        Write-Output "PORT_START $stem $(Get-Date -Format o)"
        $package = "work/extract/$stem"
        if (-not (Test-Path -LiteralPath "$package/manifest.json")) {
            if ((Test-Path -LiteralPath $package) -and (Get-ChildItem -LiteralPath $package -Force | Select-Object -First 1)) {
                throw "Incomplete extraction at $package. Inspect and archive that exact directory before retrying."
            }
            & python -u -B tools/extract/extract_map.py --plan "tools/extract/plans/$stem.json" --output $package *> $log
            if ($LASTEXITCODE -ne 0) { throw "Extraction failed for $stem; see $log" }
        }
        & pwsh -NoProfile -File tools/build_all.ps1 -MapKey "arena_$stem" -PackageDir "$repo/$package" `
            -OutputDir "$repo/work/build/$stem" -FullMap -ReproTimeoutSeconds $ReproTimeoutSeconds *>> $log
        if ($LASTEXITCODE -ne 0) { throw "Build/validation failed for $stem; see $log" }
        & python -u tools/validate/validate_port.py --map $stem *>> $log
        if ($LASTEXITCODE -ne 0) { throw "Full-map receipt validation failed for $stem; see $log" }
        Write-Output "PORT_COMPLETE $stem $(Get-Date -Format o)"
    }
    if ($Stage) {
        & python -u tools/validate/validate_map_descriptors.py
        if ($LASTEXITCODE -ne 0) { throw 'Runtime registry and map descriptors disagree; staging refused.' }
        foreach ($stem in $allMaps) {
            & python -u tools/validate/validate_port.py --map $stem
            if ($LASTEXITCODE -ne 0) { throw "Cannot stage until every registered map validates: $stem" }
        }
        & dotnet test ArenaMapPort.Server.Tests -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Server tests failed.' }
        & dotnet build ArenaMapPort.Client -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Client Release build/staging failed.' }
        foreach ($stem in $allMaps) {
            & python -u tools/validate/validate_port.py --map $stem --distribution
            if ($LASTEXITCODE -ne 0) { throw "Staged map validation failed: $stem" }
        }
        & python -u tools/validate/validate_distribution.py
        if ($LASTEXITCODE -ne 0) { throw 'Distribution contains missing, stale or unexpected map files.' }
        Write-Output 'ALL_ADDITIONAL_MAPS_STAGED'
    }
} finally {
    Pop-Location
}
