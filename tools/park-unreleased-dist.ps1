<# Reversibly remove only known parked-map outputs from this repository's distribution.
   Inputs, source data and live SPT installations are never touched. #>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Client','Server')][string]$Scope)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $repo 'dist'
$archiveBase = Join-Path $repo 'work/release-parked'
$sourceRoot = if ($Scope -eq 'Client') { Join-Path $dist 'BepInEx/plugins/ArenaMapPort' } else { Join-Path $dist 'SPT_Runtime/user/mods/ArenaMapPort' }
$archive = Join-Path $archiveBase (([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) + '-' + [Guid]::NewGuid().ToString('N') + '/' + $Scope)

function Assert-OwnedPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $full.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes owned root: $full"
    }
    # Reject redirected ancestors before any mutation, including the leaf itself.
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType) { throw "Linked path refused: $cursor" }
        }
    }
}

Assert-OwnedPath $sourceRoot $dist
Assert-OwnedPath $archive $archiveBase
$parked = @('airpit','airpit2','autoservice','bowl','prison','railwaystation','yard','iceberg','factory','custom','shoreline','woods')
$plan = @(
    foreach ($stem in $parked) {
        $paths = if ($Scope -eq 'Client') {
            @("bundles/arena_${stem}_scenes.bundle", "bundles/arena_${stem}_navmesh.bundle", "bundles/arena_${stem}_preset.bundle", "material-shaders.arena_${stem}.json")
        } else { @("db/arena_${stem}/base.json") }
        foreach ($relative in $paths) {
            $source = Join-Path $sourceRoot $relative
            $destination = Join-Path $archive $relative
            Assert-OwnedPath $source $sourceRoot
            Assert-OwnedPath $destination $archive
            if (Test-Path -LiteralPath $destination) { throw "Archive destination already exists: $destination" }
            if (Test-Path -LiteralPath $source) {
                $file = Get-Item -LiteralPath $source -Force
                if ($file.PSIsContainer) { throw "Expected regular file: $source" }
                [pscustomobject]@{ RelativePath=$relative; Source=$source; Destination=$destination; Bytes=$file.Length; Sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash; Moved=$false }
            }
        }
    }
)
if (-not $plan.Count) { Write-Output "PARKED_DIST_OK $Scope 0 files"; exit 0 }
New-Item -ItemType Directory -Path $archive -Force | Out-Null
$manifest = Join-Path $archive 'manifest.json'
$plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest -Encoding utf8
foreach ($item in $plan) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($item.Destination)) -Force | Out-Null
    Move-Item -LiteralPath $item.Source -Destination $item.Destination
    $item.Moved = $true
    $plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest -Encoding utf8
}
Write-Output "PARKED_DIST_OK $Scope $($plan.Count) files archived: $archive"
