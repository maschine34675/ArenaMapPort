<#
.SYNOPSIS
  Extract and build the eleven parked Arena layouts, one Unity build at a time.
.DESCRIPTION
  Skips maps whose build outputs already pass validate_port. Existing extraction packages are reused. An incomplete extraction
  directory is never overwritten. -Rebuild rebuilds bundles from complete existing packages.
  -Stage is reserved and refused: the release distribution contains only the released maps (Equator, Bay5 and AirPit).
  AirPit (arena_airpit) is not built here any more (PLAN-AIRPIT item 8): this runner derives the package as work/extract/<stem>,
  which for airpit is the superseded Arena 0.5.4 spike package. AirPit is built from work/extract/airpit-native-v5 (Arena 0.6.0)
  with an explicit -PackageDir through tools/build_all.ps1; see docs/progress/airpit-release.md.
  This script does not deploy to the live SPT installation.
.EXAMPLE
  .\tools\build_remaining.ps1
.EXAMPLE
  .\tools\build_remaining.ps1 -Maps autoservice,airpit2 -Rebuild
.EXAMPLE
  .\tools\build_remaining.ps1 -Stage
#>
[CmdletBinding()]
param(
    [string[]]$Maps = @('autoservice','airpit2','bowl','prison','railwaystation','yard','iceberg','factory','custom','shoreline','woods'),
    [ValidateRange(1,86400)][int]$ReproTimeoutSeconds = 1800,
    [switch]$Rebuild,
    [switch]$Stage
)
$ErrorActionPreference = 'Stop'
if ($Stage) { throw 'Additional maps are parked. -Stage cannot write them into the release (Equator, Bay5, AirPit); omit -Stage to retain development builds in work/.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$allMaps = @('autoservice','airpit2','bowl','prison','railwaystation','yard','iceberg','factory','custom','shoreline','woods')
if (-not $Maps.Count -or (@($Maps | Sort-Object -Unique).Count -ne $Maps.Count)) { throw 'Specify a nonempty unique map list.' }
if ($Maps -ccontains 'airpit') { throw 'airpit is no longer built by this runner: it would use the superseded Arena 0.5.4 package work/extract/airpit. Build it from work/extract/airpit-native-v5 with an explicit -PackageDir through tools/build_all.ps1 (docs/progress/airpit-release.md).' }
foreach ($stem in $Maps) { if ($stem -cnotin $allMaps) { throw "Unknown additional map: $stem" } }

Push-Location -LiteralPath $repo
try {
    New-Item -ItemType Directory -Force -Path 'work/logs/remaining-maps' | Out-Null
    foreach ($stem in $Maps) {
        $log = "work/logs/remaining-maps/$stem-pipeline.log"
        if (-not $Rebuild -and (Test-Path -LiteralPath "work/build/$stem/unity-run.json")) {
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
        if ($LASTEXITCODE -ne 0) { throw "Build output validation failed for $stem; see $log" }
        Write-Output "PORT_COMPLETE $stem $(Get-Date -Format o)"
    }
} finally {
    Pop-Location
}
