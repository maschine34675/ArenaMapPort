<#
.SYNOPSIS
  Promotes D:/ArenaMapPort/dist/ into a live SPT install (docs/SPEC.md decision #9 / §G.S4's own
  "a separate, explicit, user-approved step" — this script IS that step, not something any build
  target or CI-style pipeline runs automatically).

.DESCRIPTION
  Copies dist/BepInEx/plugins/maschine-ArenaMapPort/**      -> <SptRoot>/BepInEx/plugins/maschine-ArenaMapPort/**
        dist/SPT_Runtime/user/mods/ArenaMapPort/** -> <SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/**

  SAFETY (task-mandated, not optional):
    - DEFAULT behaviour is a DRY RUN. It prints the exact copy plan (source -> destination, size,
      whether the destination already exists, whether its content already matches by SHA256) and
      copies NOTHING. This is true with zero arguments and with only -SptRoot given.
    - A real copy only happens when -Confirm is passed explicitly.
    - Ordinary, DLL-only and data deployment never delete files. All copies remain inside
      ArenaMapPort's own client/server directories. The explicit RemoveLegacyDynamicMaps mode
      removes only our two old map packages after verifying their replacements and installed
      adapter against dist. It rejects links and unknown content before any deletion, and never
      recursively deletes directories. No other DynamicMaps files or settings are changed.
      RemoveLegacyEquatorDb is a second explicit migration: verify the new database/server DLL,
      back up the original loose Equator database, then remove only that original file.
      RemoveLegacyShaderSidecar does the same on the client side for Equator's renamed shader
      sidecar: verify material-shaders.arena_equator.json and the client DLL against dist, back up
      and remove only the old material-shaders.json.
      RemoveDebugHelper similarly backs up/removes only the accepted ArenaMapDebug DLL
      at its two historical locations; other plugin files, folders and configs stay intact.
    - Never reads or writes anything under D:/ArenaMapPort/dist/ other than read-only enumeration
      (source of truth, untouched).

.PARAMETER SptRoot
  Root of the live SPT install to promote into. Default D:/SPT41.

.PARAMETER Confirm
  Perform the real copy. Without this switch the script only prints the plan and exits 0.

.PARAMETER DynamicMapsPackage
  Optional data-only package directory. Deploys exactly ArenaMapPort_<map>.jsonc and
  Layers/Ground.svg into maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_<map>.
  This separate mode never reads dist/ or copies client/server files. DynamicMaps is optional; it
  validates the complete package and refuses links in either path. Default remains a dry run.

.PARAMETER DynamicMapsMap
  Selects Equator (default), Bay5, AirPit or Bowl for DynamicMapsPackage. The selected map fixes the package
  filename, internal map ID, image path and destination; no other map destinations are allowed.

.PARAMETER DllOnly
  Updates maschine-ArenaMapPort.Client.dll and, by default, maschine-ArenaMapPort.Server.dll
  from dist. Requires every selected DLL already installed: this is not a first-install mode.
  All selected destinations are exclusively locked before any write; failures restore their
  previous bytes. No bundles, data, assets or configuration are read or copied.
  Incompatible with DynamicMapsPackage/Map. Dry run unless Confirm is supplied.

.PARAMETER DllTarget
  Selects Both (default) or Client for DllOnly. Client updates only the client DLL and never
  inspects, reads or opens the staged or installed server DLL. Requires DllOnly.

.PARAMETER RemoveLegacyDynamicMaps
  Separate migration mode (not combinable with other modes). Deletes only the old Equator/Bay5
  JSONC/SVG files in mpstark-dynamicmaps/Maps after verifying both new owned packages and the client
  DLL match dist. Missing legacy folders are a no-op; unknown entries/links fail before deletion.
  Close EFT first (DynamicMaps caches definitions). Dry run unless Confirm is supplied.

.PARAMETER RemoveLegacyEquatorDb
  Separate migration after a full deployment. Verifies the installed nested Equator database
  and server DLL against dist, backs up the old owned db/base.json under work/deployment-backups,
  then removes that one legacy file. Refuses links and non-Equator data. Dry run unless Confirm.

.PARAMETER RemoveLegacyShaderSidecar
  Separate migration after a full deployment (2026-10-17: Equator's shader sidecar was renamed from
  material-shaders.json to material-shaders.arena_equator.json, one rule for every map). Verifies the
  installed renamed sidecar and client DLL against dist, backs up the old material-shaders.json under
  work/deployment-backups, then removes that one legacy file. Refuses links and files that are not a
  shader sidecar. Run it after a full deployment (not DllOnly) and after EFT was restarted with the new
  client DLL; an EFT still running the old DLL would read the old name at its next raid. Dry run unless Confirm.

.PARAMETER RemoveDebugHelper
  Separate cleanup mode for the retired four-button ArenaMapDebug helper. Backs up and removes
  only its known DLL from either historical plugin path, verifying the accepted helper hash.
  Close EFT first. Leaves folders/configuration untouched. Dry run unless Confirm is supplied.

.PARAMETER RemoveLegacySpectatorTextures
  Separate development-package migration after full deployment, with EFT closed. Requires the
  installed DLL, four schema-4 graphs and shared textures to match dist. Removes only old per-map
  PNGs byte-identical to the verified shared replacements. Original exports remain in work/extract.
  Unknown or changed files refuse the operation before removal. Dry run unless Confirm is supplied.

.PARAMETER RenameClientFolder
  Separate migration from BepInEx/plugins/ArenaMapPort to maschine-ArenaMapPort.
  Renames the complete directory without changing any file contents or the server folder.
  Refuses links, a missing client DLL, and an existing destination. Close EFT first,
  including any host receiving this installation through synchronization. Dry run unless Confirm.

.EXAMPLE
  # dry run (default) -- prints the plan, copies nothing:
  pwsh tools/deploy-to-spt.ps1

.EXAMPLE
  # real promotion, after reviewing the dry-run plan and getting the user's explicit go-ahead:
  pwsh tools/deploy-to-spt.ps1 -Confirm

.EXAMPLE
  # data-only dry run; add -Confirm for the real copy:
  pwsh tools/deploy-to-spt.ps1 -DynamicMapsPackage work/dynamicmaps/equator/pack

.EXAMPLE
  # client DLL-only dry run; server DLL and all assets remain outside the plan:
  pwsh tools/deploy-to-spt.ps1 -DllOnly -DllTarget Client
#>
[CmdletBinding()]
param(
    [string]$SptRoot = "D:/SPT41",
    [switch]$Confirm,
    [switch]$DllOnly,
    [ValidateSet('Client', 'Both')]
    [string]$DllTarget = 'Both',
    [switch]$RemoveLegacyDynamicMaps,
    [switch]$RemoveLegacyEquatorDb,
    [switch]$RemoveLegacyShaderSidecar,
    [switch]$RemoveLegacySpectatorTextures,
    [switch]$RemoveDebugHelper,
    [switch]$RenameClientFolder,
    [string]$DynamicMapsPackage,
    [ValidateSet('Equator', 'Bay5', 'AirPit', 'Bowl')]
    [string]$DynamicMapsMap = 'Equator'
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==== [deploy-to-spt] $msg ====" -ForegroundColor Cyan
}

function Fail($msg) {
    Write-Host "[deploy-to-spt] FATAL: $msg" -ForegroundColor Red
    exit 1
}

# Scoped update modes cannot widen the regular client/server deployment mappings.
function Assert-PlainPath([string]$Path, [string]$Kind) {
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    $isLeaf = $true
    while ($cursor) {
        # Get-Item (rather than Test-Path alone) also sees broken symbolic links.
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.LinkType -eq 'HardLink') {
                throw "Linked/reparse path is forbidden: $cursor"
            }
            if ((-not $isLeaf -or $Kind -eq 'Directory') -and -not $item.PSIsContainer) {
                throw "Expected a directory: $cursor"
            }
            if ($isLeaf -and $Kind -eq 'File' -and $item.PSIsContainer) {
                throw "Expected a file: $cursor"
            }
        }
        $isLeaf = $false
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Assert-FiniteNumber($Value, [string]$Name) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool] -or
        $Value -isnot [ValueType] -or [double]::IsNaN([double]$Value) -or [double]::IsInfinity([double]$Value)) {
        throw "Expected finite numeric $Name"
    }
}

function Assert-MapBounds($Bounds, [string[]]$Axes, [string]$Name) {
    if ($Bounds -isnot [System.Collections.IDictionary]) { throw "Missing/invalid $Name" }
    foreach ($axis in $Axes) {
        if ($Bounds.Min -isnot [System.Collections.IDictionary] -or $Bounds.Max -isnot [System.Collections.IDictionary]) {
            throw "Missing Min/Max in $Name"
        }
        Assert-FiniteNumber $Bounds.Min[$axis] "$Name.Min.$axis"
        Assert-FiniteNumber $Bounds.Max[$axis] "$Name.Max.$axis"
        if ([double]$Bounds.Min[$axis] -ge [double]$Bounds.Max[$axis]) { throw "Unordered/empty $Name axis $axis" }
    }
}

function Assert-UniqueJsonKeys([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw "Duplicate JSON property: $($property.Name)" }
            Assert-UniqueJsonKeys $property.Value
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($child in $Element.EnumerateArray()) { Assert-UniqueJsonKeys $child }
    }
}

function Assert-DynamicMapsPackage([string]$Package, [string]$MapName) {
    $mapFile = "ArenaMapPort_$MapName.jsonc"
    $mapId = "arena_$($MapName.ToLowerInvariant())"
    $packageFull = Assert-PlainPath $Package 'Directory'
    if (-not (Test-Path -LiteralPath $packageFull -PathType Container)) { throw "Package directory does not exist: $Package" }
    $rootEntries = @(Get-ChildItem -LiteralPath $packageFull -Force)
    if ($rootEntries.Count -ne 2 -or
        @($rootEntries | Where-Object { $_.Name -ceq $mapFile -and -not $_.PSIsContainer }).Count -ne 1 -or
        @($rootEntries | Where-Object { $_.Name -ceq 'Layers' -and $_.PSIsContainer }).Count -ne 1) {
        throw "Package must contain only $mapFile and the Layers directory"
    }
    $layersPath = Assert-PlainPath (Join-Path $packageFull 'Layers') 'Directory'
    $layerEntries = @(Get-ChildItem -LiteralPath $layersPath -Force)
    if ($layerEntries.Count -ne 1 -or $layerEntries[0].Name -cne 'Ground.svg' -or $layerEntries[0].PSIsContainer) {
        throw 'Layers must contain only Ground.svg'
    }
    $jsonPath = Assert-PlainPath (Join-Path $packageFull $mapFile) 'File'
    $svgPath = Assert-PlainPath (Join-Path $layersPath 'Ground.svg') 'File'
    $jsonText = Get-Content -LiteralPath $jsonPath -Raw
    # JsonDocument detects duplicate keys and rejects non-JSON values before PowerShell conversion.
    $jsonOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $jsonOptions.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
    $jsonOptions.AllowTrailingCommas = $true
    $document = [System.Text.Json.JsonDocument]::Parse($jsonText, $jsonOptions)
    try { Assert-UniqueJsonKeys $document.RootElement } finally { $document.Dispose() }
    $map = ConvertFrom-Json -InputObject $jsonText -AsHashtable -Depth 64
    if ($map -isnot [System.Collections.IDictionary] -or $map.MapInternalNames -isnot [array] -or
        $map.MapInternalNames.Count -ne 1 -or $map.MapInternalNames[0] -isnot [string] -or $map.MapInternalNames[0] -cne $mapId) {
        throw "MapInternalNames must be exactly [`"$mapId`"]"
    }
    Assert-FiniteNumber $map.CoordinateRotation 'CoordinateRotation'
    Assert-FiniteNumber $map.DefaultLevel 'DefaultLevel'
    if ($map.CoordinateRotation -ne 0 -or $map.DefaultLevel -ne 0) { throw 'CoordinateRotation and DefaultLevel must be 0' }
    Assert-MapBounds $map.Bounds @('x', 'y') 'Bounds'
    if ($map.Layers -isnot [System.Collections.IDictionary] -or $map.Layers.Count -ne 1) { throw 'Expected exactly one ground layer' }
    $layer = @($map.Layers.Values)[0]
    if ($layer -isnot [System.Collections.IDictionary]) { throw 'Invalid ground layer' }
    Assert-FiniteNumber $layer.Level 'Layers.Level'
    if ($layer.Level -ne 0) { throw 'Ground layer Level must be 0' }
    if ($layer.ImagePath -isnot [string] -or $layer.ImagePath -cne "Maps/ArenaMapPort_$MapName/Layers/Ground.svg") { throw 'Invalid ground ImagePath' }
    Assert-MapBounds $layer.ImageBounds @('x', 'y') 'ImageBounds'
    if ($layer.GameBounds -isnot [array] -or $layer.GameBounds.Count -eq 0) { throw 'Missing ground GameBounds' }
    foreach ($bounds in $layer.GameBounds) { Assert-MapBounds $bounds @('x', 'y', 'z') 'GameBounds' }
    # The prototype may refer to the installed exit marker; it never supplies third-party assets.
    if ($map.Contains('StaticMarkers') -and $map.StaticMarkers -isnot [array]) { throw 'StaticMarkers must be an array' }
    foreach ($marker in $map.StaticMarkers) {
        if ($marker -isnot [System.Collections.IDictionary] -or $marker.ImagePath -isnot [string] -or $marker.ImagePath -cne 'Markers/exit.png') {
            throw 'Invalid static marker ImagePath'
        }
    }
    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($svgPath, $xmlSettings)
    try {
        $svg = [Xml.XmlDocument]::new()
        $svg.XmlResolver = $null
        $svg.Load($reader)
        if ($svg.DocumentElement.LocalName -cne 'svg' -or $svg.DocumentElement.NamespaceURI -cne 'http://www.w3.org/2000/svg') {
            throw 'Ground.svg must have an SVG root element'
        }
        # Keep the two-file prototype self-contained: no external files, URLs, or active content.
        foreach ($element in $svg.SelectNodes('//*')) {
            if ($element.LocalName -in @('script', 'foreignObject')) { throw 'Active SVG content is forbidden' }
            foreach ($attribute in $element.Attributes) {
                if ($attribute.LocalName -in @('src', 'base') -or
                    ($attribute.LocalName -eq 'href' -and -not $attribute.Value.StartsWith('#')) -or
                    $attribute.LocalName -match '^on') { throw 'External/active SVG reference is forbidden' }
            }
        }
        if ($svg.OuterXml -match '(?i)@import|url\s*\(' -or $svg.SelectNodes('//processing-instruction()').Count) {
            throw 'SVG external styles/references are forbidden in this prototype'
        }
    } finally { $reader.Dispose() }
    return $packageFull
}

function Invoke-DynamicMapsDeploy {
    # ValidateSet accepts case-insensitive input; keep filenames and destinations canonical.
    $mapName = switch ($DynamicMapsMap) { 'Bay5' { 'Bay5' } 'AirPit' { 'AirPit' } 'Bowl' { 'Bowl' } default { 'Equator' } }
    $mapFile = "ArenaMapPort_$mapName.jsonc"
    $package = Assert-DynamicMapsPackage $DynamicMapsPackage $mapName
    $pluginRoot = Assert-PlainPath (Join-Path $SptRoot 'BepInEx/plugins/maschine-ArenaMapPort') 'Directory'
    $target = Assert-PlainPath (Join-Path $pluginRoot "dynamicmaps/ArenaMapPort_$mapName") 'Directory'
    $targetPrefix = $target.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($target -eq $package -or $target.StartsWith($package.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $package.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package and deployment target must not overlap' }
    $plan = @(foreach ($relative in @($mapFile, 'Layers/Ground.svg')) {
        $source = Assert-PlainPath (Join-Path $package $relative) 'File'
        $destination = Assert-PlainPath (Join-Path $target $relative) 'File'
        if (-not $destination.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Destination escapes the $mapName map directory" }
        $exists = Test-Path -LiteralPath $destination -PathType Leaf
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $same = $exists -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $hash
        [PSCustomObject]@{ Source = $source; Destination = $destination; Hash = $hash; SizeBytes = (Get-Item -LiteralPath $source).Length; Exists = $exists; Same = $same }
    })
    Write-Step "DynamicMaps $mapName data-only copy plan"
    foreach ($row in $plan) {
        $status = if ($row.Same) { 'unchanged (same SHA256)' } elseif ($row.Exists) { 'WILL OVERWRITE (differs)' } else { 'NEW' }
        Write-Host "   $($row.Source) -> $($row.Destination) ($($row.SizeBytes) bytes) [$status]"
    }
    Write-Host "[deploy-to-spt] plan: 2 file(s), DynamicMaps $mapName data only"
    if (-not $Confirm) {
        Write-Host '[deploy-to-spt] DRY RUN (default) -- no files were copied.' -ForegroundColor Green
        return
    }
    # Revalidate the complete package and every target before any write, then recheck each copy.
    $null = Assert-DynamicMapsPackage $package $mapName
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Destination 'File'
        if ((Get-FileHash -LiteralPath $row.Source -Algorithm SHA256).Hash -ne $row.Hash) { throw 'Package changed during validation' }
    }
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Source 'File'
        $null = Assert-PlainPath $row.Destination 'File'
        $directory = Split-Path $row.Destination -Parent
        if (-not (Test-Path -LiteralPath $directory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        Copy-Item -LiteralPath $row.Source -Destination $row.Destination -Force
        if ((Get-FileHash -LiteralPath $row.Destination -Algorithm SHA256).Hash -ne $row.Hash) { throw 'Copied map data hash differs' }
        Write-Host "[deploy-to-spt] copied $($row.Destination)"
    }
    Write-Host '[deploy-to-spt] OK -- 2 DynamicMaps data files copied.' -ForegroundColor Green
}

function Get-LegacyDynamicMapsPlan {
    $repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $relativePlugin = 'BepInEx/plugins/maschine-ArenaMapPort'
    $stagedPlugin = Join-Path (Join-Path $repo 'dist') $relativePlugin
    $installedPlugin = Join-Path $SptRoot $relativePlugin
    $replacementFiles = @('maschine-ArenaMapPort.Client.dll')
    foreach ($map in @('Equator', 'Bay5')) {
        $relative = "dynamicmaps/ArenaMapPort_$map"
        $null = Assert-DynamicMapsPackage (Join-Path $installedPlugin $relative) $map
        $null = Assert-DynamicMapsPackage (Join-Path $stagedPlugin $relative) $map
        $replacementFiles += "$relative/ArenaMapPort_$map.jsonc", "$relative/Layers/Ground.svg"
    }
    foreach ($relative in $replacementFiles) {
        $staged = Assert-PlainPath (Join-Path $stagedPlugin $relative) 'File'
        $installed = Assert-PlainPath (Join-Path $installedPlugin $relative) 'File'
        if (-not (Test-Path -LiteralPath $staged -PathType Leaf) -or -not (Test-Path -LiteralPath $installed -PathType Leaf) -or
            (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) {
            throw "Legacy cleanup requires installed replacement matching dist: $relative"
        }
    }
    $legacyRoot = Assert-PlainPath (Join-Path $SptRoot 'BepInEx/plugins/mpstark-dynamicmaps/Maps') 'Directory'
    foreach ($map in @('Equator', 'Bay5')) {
        $target = Assert-PlainPath (Join-Path $legacyRoot "ArenaMapPort_$map") 'Directory'
        $prefix = $legacyRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Legacy target escapes Maps directory' }
        if (-not (Test-Path -LiteralPath $target -PathType Container)) { continue }
        # Inspect exactly one directory level at a time; never follow a link while enumerating.
        foreach ($item in Get-ChildItem -LiteralPath $target -Force) {
            if ($item.Name -ceq "ArenaMapPort_$map.jsonc") { $null = Assert-PlainPath $item.FullName 'File' }
            elseif ($item.Name -ceq 'Layers') { $null = Assert-PlainPath $item.FullName 'Directory' }
            else { throw "Unknown legacy package entry: $($item.FullName)" }
        }
        $layers = Assert-PlainPath (Join-Path $target 'Layers') 'Directory'
        if (Test-Path -LiteralPath $layers -PathType Container) {
            foreach ($item in Get-ChildItem -LiteralPath $layers -Force) {
                if ($item.Name -cne 'Ground.svg') { throw "Unknown legacy layer entry: $($item.FullName)" }
                $null = Assert-PlainPath $item.FullName 'File'
            }
        }
        foreach ($relative in @("ArenaMapPort_$map.jsonc", 'Layers/Ground.svg')) {
            $file = Assert-PlainPath (Join-Path $target $relative) 'File'
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                [PSCustomObject]@{ Path = $file; Kind = 'File'; Hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
            }
        }
        if (Test-Path -LiteralPath $layers -PathType Container) { [PSCustomObject]@{ Path = $layers; Kind = 'Directory'; Hash = '' } }
        [PSCustomObject]@{ Path = $target; Kind = 'Directory'; Hash = '' }
    }
}

function Invoke-LegacyDynamicMapsRemoval {
    $plan = @(Get-LegacyDynamicMapsPlan)
    Write-Step 'Legacy ArenaMapPort DynamicMaps cleanup (replacements verified)'
    foreach ($row in $plan) { Write-Host "   DELETE $($row.Kind): $($row.Path)" }
    Write-Host "[deploy-to-spt] cleanup plan: $(@($plan | Where-Object Kind -eq 'File').Count) legacy file(s)"
    if (-not $Confirm) {
        Write-Host '[deploy-to-spt] DRY RUN (default) -- nothing was deleted.'
        return
    }
    # Validate BOTH maps and replacement files again before removing the first legacy file.
    $current = @(Get-LegacyDynamicMapsPlan)
    if (($current | ConvertTo-Json -Compress) -cne ($plan | ConvertTo-Json -Compress)) { throw 'Legacy cleanup plan changed during validation' }
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Path $row.Kind
        if ($row.Kind -eq 'File') {
            if ((Get-FileHash -LiteralPath $row.Path -Algorithm SHA256).Hash -ne $row.Hash) { throw 'Legacy file changed during cleanup' }
            Remove-Item -LiteralPath $row.Path -Force
        } else {
            # Fails if anything was added concurrently; never remove unexpected contents.
            [IO.Directory]::Delete($row.Path, $false)
        }
    }
    Write-Host '[deploy-to-spt] OK -- legacy ArenaMapPort map packages removed; other DynamicMaps files untouched.'
}

function Invoke-DllOnlyDeploy {
    $repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $relativePaths = @('BepInEx/plugins/maschine-ArenaMapPort/maschine-ArenaMapPort.Client.dll')
    if ($DllTarget -eq 'Both') {
        $relativePaths += 'SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll'
    }
    $plan = @(foreach ($relative in $relativePaths) {
        $source = Assert-PlainPath (Join-Path (Join-Path $repo 'dist') $relative) 'File'
        $destination = Assert-PlainPath (Join-Path $SptRoot $relative) 'File'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing staged DLL: $source" }
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "DllOnly requires an existing installed DLL: $destination" }
        if ($source -eq $destination) { throw 'Staged DLL and installed target must differ' }
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        [PSCustomObject]@{ Source = $source; Destination = $destination; Hash = $hash;
            Same = ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $hash) }
    })
    Write-Step 'DLL-only update plan'
    foreach ($row in $plan) {
        $status = if ($row.Same) { 'unchanged (same SHA256)' } else { 'WILL OVERWRITE (differs)' }
        Write-Host "   $($row.Source) -> $($row.Destination) [$status]"
    }
    Write-Host "[deploy-to-spt] plan: $($plan.Count) file(s), DLL-only update; no bundles/assets/data/configs"
    if (-not $Confirm) {
        Write-Host '[deploy-to-spt] DRY RUN (default) -- no files were copied.' -ForegroundColor Green
        return
    }

    $streams = [Collections.Generic.List[IO.FileStream]]::new()
    $backups = [Collections.Generic.List[byte[]]]::new()
    $sources = [Collections.Generic.List[byte[]]]::new()
    $writing = $false
    try {
        # Open existing files only. A missing/locked second DLL cannot create an empty
        # first DLL or partially deploy the pair. Links are revalidated before each open.
        foreach ($row in $plan) {
            $null = Assert-PlainPath $row.Source 'File'
            $null = Assert-PlainPath $row.Destination 'File'
            $bytes = [IO.File]::ReadAllBytes($row.Source)
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $row.Hash) {
                throw 'Staged DLL changed during validation'
            }
            $sources.Add($bytes)
            $stream = [IO.File]::Open($row.Destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $streams.Add($stream)
            $backup = [IO.MemoryStream]::new()
            try { $stream.CopyTo($backup); $backups.Add($backup.ToArray()) } finally { $backup.Dispose() }
        }
        $writing = $true
        for ($i = 0; $i -lt $streams.Count; $i++) {
            $stream = $streams[$i]; $bytes = $sources[$i]
            $stream.Position = 0; $stream.Write($bytes, 0, $bytes.Length); $stream.SetLength($bytes.Length); $stream.Flush($true)
            $stream.Position = 0
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne $plan[$i].Hash) {
                throw "DLL verification failed: $($plan[$i].Destination)"
            }
        }
    } catch {
        $deploymentError = $_
        $restoreErrors = [Collections.Generic.List[Exception]]::new()
        if ($writing) {
            for ($i = 0; $i -lt $streams.Count; $i++) {
                try {
                    $stream = $streams[$i]; $bytes = $backups[$i]
                    $stream.Position = 0; $stream.Write($bytes, 0, $bytes.Length); $stream.SetLength($bytes.Length); $stream.Flush($true)
                    $stream.Position = 0
                    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne
                        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) {
                        throw 'Restored DLL hash differs from its backup'
                    }
                } catch {
                    $restoreErrors.Add([IO.IOException]::new("Restore failed: $($plan[$i].Destination)", $_.Exception))
                }
            }
        }
        if ($restoreErrors.Count) {
            $restoreErrors.Insert(0, $deploymentError.Exception)
            throw [AggregateException]::new('DLL update failed; one or more backups could not be restored. Do not start EFT/SPT.', $restoreErrors)
        }
        throw $deploymentError
    } finally { foreach ($stream in $streams) { $stream.Dispose() } }
    Write-Host "[deploy-to-spt] OK -- $($plan.Count) verified DLLs updated; all other files untouched." -ForegroundColor Green
}

function Invoke-LegacyEquatorDbRemoval {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $relativeRoot = 'SPT_Runtime/user/mods/ArenaMapPort'
    $installed = Assert-PlainPath (Join-Path $SptRoot $relativeRoot) 'Directory'
    $staged = Assert-PlainPath (Join-Path (Join-Path $repo 'dist') $relativeRoot) 'Directory'
    foreach ($relative in @('db/arena_equator/base.json', 'maschine-ArenaMapPort.Server.dll')) {
        $source = Assert-PlainPath (Join-Path $staged $relative) 'File'
        $target = Assert-PlainPath (Join-Path $installed $relative) 'File'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Deploy and verify the new Equator data and server DLL first: $relative"
        }
    }
    $newDb = Get-Content -LiteralPath (Join-Path $installed 'db/arena_equator/base.json') -Raw | ConvertFrom-Json
    if ($newDb.Id -cne 'arena_equator') { throw 'Replacement database is not Equator' }
    $legacy = Assert-PlainPath (Join-Path $installed 'db/base.json') 'File'
    if (-not (Test-Path -LiteralPath $legacy -PathType Leaf)) {
        Write-Output 'LEGACY_EQUATOR_DB_OK already absent'; return
    }
    $oldBytes = [IO.File]::ReadAllBytes($legacy)
    $oldDb = [Text.Encoding]::UTF8.GetString($oldBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    if ($oldDb.Id -cne 'arena_equator') { throw 'Legacy database is not Equator; refusing removal' }
    if (-not $Confirm) {
        Write-Output "DRY RUN: back up and remove only $legacy after verified replacement"; return
    }
    $backup = Assert-PlainPath (Join-Path $repo ('work/deployment-backups/' + [Guid]::NewGuid().ToString('N') + '/equator-base.json')) 'File'
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
    [IO.File]::WriteAllBytes($backup, $oldBytes)
    # Revalidate after backup and refuse a concurrently changed legacy file.
    $null = Assert-PlainPath $legacy 'File'
    if ((Get-FileHash -LiteralPath $legacy).Hash -ne (Get-FileHash -LiteralPath $backup).Hash) {
        throw 'Legacy database changed during migration; backup retained, removal cancelled'
    }
    Remove-Item -LiteralPath $legacy
    Write-Output "LEGACY_EQUATOR_DB_OK backed up to $backup"
}

function Invoke-LegacyShaderSidecarRemoval {
    # 2026-10-17: Equator's shader sidecar was renamed from material-shaders.json to material-shaders.arena_equator.json
    # (ShaderRebind.SidecarFileName, one rule for every map). Ordinary deployment never deletes, so the old file stays
    # on an install until this explicit migration retires it, after the renamed file and the client DLL match dist.
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $relativeRoot = 'BepInEx/plugins/maschine-ArenaMapPort'
    $installed = Assert-PlainPath (Join-Path $SptRoot $relativeRoot) 'Directory'
    $staged = Assert-PlainPath (Join-Path (Join-Path $repo 'dist') $relativeRoot) 'Directory'
    foreach ($relative in @('material-shaders.arena_equator.json', 'maschine-ArenaMapPort.Client.dll')) {
        $source = Assert-PlainPath (Join-Path $staged $relative) 'File'
        $target = Assert-PlainPath (Join-Path $installed $relative) 'File'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Deploy and verify the renamed Equator shader sidecar and the client DLL first: $relative"
        }
    }
    $legacy = Assert-PlainPath (Join-Path $installed 'material-shaders.json') 'File'
    if (-not (Test-Path -LiteralPath $legacy -PathType Leaf)) {
        Write-Output 'LEGACY_SHADER_SIDECAR_OK already absent'; return
    }
    $oldBytes = [IO.File]::ReadAllBytes($legacy)
    # A shader sidecar maps material ids to { shader, renderQueue, keywords }; anything else under that name stays.
    $old = [Text.Encoding]::UTF8.GetString($oldBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable
    if ($old -isnot [hashtable] -or $old.Count -eq 0) { throw 'Legacy file is not a shader sidecar; refusing removal' }
    foreach ($entry in $old.Values) {
        if ($entry -isnot [hashtable] -or -not $entry.ContainsKey('shader')) { throw 'Legacy file is not a shader sidecar; refusing removal' }
    }
    if (-not $Confirm) {
        Write-Output "DRY RUN: back up and remove only $legacy after verified replacement"; return
    }
    $backup = Assert-PlainPath (Join-Path $repo ('work/deployment-backups/' + [Guid]::NewGuid().ToString('N') + '/equator-material-shaders.json')) 'File'
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
    [IO.File]::WriteAllBytes($backup, $oldBytes)
    # Revalidate after backup and refuse a concurrently changed legacy file.
    $null = Assert-PlainPath $legacy 'File'
    if ((Get-FileHash -LiteralPath $legacy).Hash -ne (Get-FileHash -LiteralPath $backup).Hash) {
        throw 'Legacy sidecar changed during migration; backup retained, removal cancelled'
    }
    Remove-Item -LiteralPath $legacy
    Write-Output "LEGACY_SHADER_SIDECAR_OK backed up to $backup"
}

function Invoke-DebugHelperRemoval {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    # This migration retires the accepted four-button helper, not an arbitrary DLL
    # a user may subsequently place under the same name.
    $acceptedHash = 'DF0F5B7D578EDC63899CA53AD24D1E78589034A973770645CA8675ECB413C301'
    $root = Assert-PlainPath $SptRoot 'Directory'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'SPT installation does not exist' }
    $plan = @(foreach ($relative in @(
        'BepInEx/plugins/ArenaMapDebug/maschine-ArenaMapDebug.dll',
        'BepInEx/plugins/maschine-ArenaMapDebug.dll')) {
        $target = Assert-PlainPath (Join-Path $root $relative) 'File'
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ((Get-FileHash -LiteralPath $target).Hash -ne $acceptedHash) {
            throw "Unrecognized ArenaMapDebug build; nothing removed: $target"
        }
        [PSCustomObject]@{ Path = $target; Relative = $relative }
    })
    if (-not $plan.Count) { Write-Output 'DEBUG_HELPER_REMOVED already absent'; return }
    foreach ($row in $plan) { Write-Output "Back up and remove: $($row.Path)" }
    if (-not $Confirm) { Write-Output 'DRY RUN: no files changed'; return }

    $backupRoot = Assert-PlainPath (Join-Path $repo ('work/deployment-backups/debug-helper-' + [Guid]::NewGuid().ToString('N'))) 'Directory'
    # Back up and recheck every candidate before deleting the first file.
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Path 'File'
        $bytes = [IO.File]::ReadAllBytes($row.Path)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $acceptedHash) {
            throw 'Debug helper changed before backup; nothing removed'
        }
        $backup = Assert-PlainPath (Join-Path $backupRoot $row.Relative) 'File'
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
        [IO.File]::WriteAllBytes($backup, $bytes)
        if ((Get-FileHash -LiteralPath $backup).Hash -ne $acceptedHash) { throw 'Debug helper backup verification failed' }
    }
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Path 'File'
        if ((Get-FileHash -LiteralPath $row.Path).Hash -ne $acceptedHash) {
            throw 'Debug helper changed during cleanup; backups retained'
        }
    }
    foreach ($row in $plan) {
        $null = Assert-PlainPath $row.Path 'File'
        if ((Get-FileHash -LiteralPath $row.Path).Hash -ne $acceptedHash) { throw 'Debug helper changed before removal' }
        Remove-Item -LiteralPath $row.Path
    }
    Write-Output "DEBUG_HELPER_REMOVED $($plan.Count) DLL(s); backups: $backupRoot"
}

function Invoke-ClientFolderRename {
    $plugins = Assert-PlainPath (Join-Path $SptRoot 'BepInEx/plugins') 'Directory'
    $source = Assert-PlainPath (Join-Path $plugins 'ArenaMapPort') 'Directory'
    $target = Assert-PlainPath (Join-Path $plugins 'maschine-ArenaMapPort') 'Directory'
    $prefix = $plugins.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $source.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($source) -ne $plugins -or [IO.Path]::GetDirectoryName($target) -ne $plugins) {
        throw 'Client migration paths escape the intended plugins directory'
    }
    if ((Test-Path -LiteralPath $source) -and (Test-Path -LiteralPath $target)) {
        throw 'Both client folders exist; refusing to merge or overwrite either folder'
    }
    $alreadyMoved = -not (Test-Path -LiteralPath $source -PathType Container)
    $current = if ($alreadyMoved) { $target } else { $source }
    $dll = Assert-PlainPath (Join-Path $current 'maschine-ArenaMapPort.Client.dll') 'File'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Expected ArenaMapPort client DLL is missing' }
    # Inspect each level before descending; never follow a directory junction.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($current)
    while ($pending.Count) {
        $folder = $pending.Pop()
        $null = Assert-PlainPath $folder 'Directory'
        foreach ($item in Get-ChildItem -LiteralPath $folder -Force) {
            $kind = if ($item.PSIsContainer) { 'Directory' } else { 'File' }
            $null = Assert-PlainPath $item.FullName $kind
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    if ($alreadyMoved) { Write-Output 'CLIENT_FOLDER_RENAMED already migrated'; return }
    $eftPath = [IO.Path]::GetFullPath((Join-Path $SptRoot 'EscapeFromTarkov.exe'))
    foreach ($process in Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue) {
        if ($process.Path -eq $eftPath) { throw 'Close EFT before renaming its plugin folder' }
    }
    Write-Output "Rename client directory: $source -> $target"
    if (-not $Confirm) { Write-Output 'DRY RUN: no files changed'; return }
    $null = Assert-PlainPath $source 'Directory'
    $null = Assert-PlainPath $target 'Directory'
    # Directory.Move refuses an existing destination instead of nesting/merging.
    [IO.Directory]::Move($source, $target)
    Write-Output 'CLIENT_FOLDER_RENAMED all contents preserved; server folder untouched'
}

function Invoke-LegacySpectatorTextureRemoval {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $relativeRoot = 'BepInEx/plugins/maschine-ArenaMapPort'
    $installed = Assert-PlainPath (Join-Path $SptRoot $relativeRoot) 'Directory'
    $staged = Assert-PlainPath (Join-Path (Join-Path $repo 'dist') $relativeRoot) 'Directory'
    $manifestPath = Assert-PlainPath (Join-Path $staged 'spectators/shared/spectator-textures.json') 'File'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or @($manifest.files).Count -lt 1) { throw 'Invalid shared texture manifest' }
    $textures = @{}
    $replacements = @('maschine-ArenaMapPort.Client.dll', 'spectators/shared/spectator-textures.json')
    foreach ($row in $manifest.files) {
        if ($row.file -cnotmatch '^[0-9a-f]{16}(-[0-5])?\.png$' -or $textures.ContainsKey($row.file) -or
            $row.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $row.bytes -le 0) { throw 'Invalid shared texture row' }
        $textures[$row.file] = $row
        $replacements += 'spectators/shared/' + $row.file
    }
    $maps = @('arena_airpit', 'arena_equator', 'arena_bay5', 'arena_bowl')
    foreach ($map in $maps) { $replacements += "spectators/$map/spectator.json.gz" }
    foreach ($relative in $replacements) {
        $source = Assert-PlainPath (Join-Path $staged $relative) 'File'
        $target = Assert-PlainPath (Join-Path $installed $relative) 'File'
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Deploy all shared spectator files and the client DLL first: $relative"
        }
    }
    foreach ($row in $textures.Values) {
        $file = Get-Item -LiteralPath (Join-Path $installed ('spectators/shared/' + $row.file))
        if ($file.Length -ne $row.bytes -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $row.sha256) {
            throw 'Damaged shared spectator replacement'
        }
    }
    $plan = @()
    foreach ($map in $maps) {
        $folder = Assert-PlainPath (Join-Path $installed "spectators/$map") 'Directory'
        $stream = [IO.File]::OpenRead((Join-Path $folder 'spectator.json.gz'))
        try {
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
            $reader = [IO.StreamReader]::new($gzip)
            try { $graph = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
        if ($graph.schema -ne 4 -or $graph.map -cne $map) { throw 'Expected the new schema-4 map graph' }
        foreach ($file in Get-ChildItem -LiteralPath $folder -Force) {
            $path = Assert-PlainPath $file.FullName 'File'
            if ($file.Name -ceq 'spectator.json.gz') { continue }
            if (-not $textures.ContainsKey($file.Name) -or
                (Get-FileHash -LiteralPath $path).Hash -ne $textures[$file.Name].sha256) {
                throw "Unknown or changed legacy spectator file: $path"
            }
            $plan += $path
        }
    }
    $eftPath = [IO.Path]::GetFullPath((Join-Path $SptRoot 'EscapeFromTarkov.exe'))
    foreach ($process in Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue) {
        if ($process.Path -eq $eftPath) { throw 'Close EFT before removing legacy spectator textures' }
    }
    Write-Output "Legacy spectator textures: $($plan.Count) identical redundant files"
    if (-not $Confirm) { Write-Output 'DRY RUN: no files changed'; return }
    foreach ($path in $plan) {
        $resolved = Assert-PlainPath $path 'File'
        if (-not $resolved.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $resolved).Hash -ne $textures[[IO.Path]::GetFileName($resolved)].sha256) {
            throw 'Legacy texture changed during migration; removal stopped'
        }
        Remove-Item -LiteralPath $resolved
    }
    Write-Output "LEGACY_SPECTATOR_TEXTURES_OK removed=$($plan.Count); shared replacements retained"
}

if ($RemoveLegacySpectatorTextures) {
    try {
        if ($RemoveLegacyShaderSidecar -or $RemoveDebugHelper -or $RemoveLegacyEquatorDb -or $RemoveLegacyDynamicMaps -or
            $RenameClientFolder -or $DllOnly -or $PSBoundParameters.ContainsKey('DllTarget') -or
            $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RemoveLegacySpectatorTextures cannot be combined with other deployment modes'
        }
        Invoke-LegacySpectatorTextureRemoval
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($PSBoundParameters.ContainsKey('DllTarget') -and -not $DllOnly) {
    Fail 'DllTarget requires DllOnly'
}

if ($RenameClientFolder) {
    try {
        if ($RemoveDebugHelper -or $RemoveLegacyEquatorDb -or $RemoveLegacyShaderSidecar -or $RemoveLegacyDynamicMaps -or $DllOnly -or
            $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RenameClientFolder cannot be combined with other deployment modes'
        }
        Invoke-ClientFolderRename
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($RemoveDebugHelper) {
    try {
        if ($RemoveLegacyEquatorDb -or $RemoveLegacyShaderSidecar -or $RemoveLegacyDynamicMaps -or $DllOnly -or
            $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RemoveDebugHelper cannot be combined with other deployment modes'
        }
        Invoke-DebugHelperRemoval
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($RemoveLegacyEquatorDb) {
    try {
        if ($RemoveLegacyShaderSidecar -or $RemoveLegacyDynamicMaps -or $DllOnly -or $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RemoveLegacyEquatorDb cannot be combined with other deployment modes'
        }
        Invoke-LegacyEquatorDbRemoval
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($RemoveLegacyShaderSidecar) {
    try {
        if ($RemoveLegacyDynamicMaps -or $DllOnly -or $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RemoveLegacyShaderSidecar cannot be combined with other deployment modes'
        }
        Invoke-LegacyShaderSidecarRemoval
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($RemoveLegacyDynamicMaps) {
    try {
        if ($DllOnly -or $PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'RemoveLegacyDynamicMaps cannot be combined with other deployment modes'
        }
        Invoke-LegacyDynamicMapsRemoval
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($DllOnly) {
    try {
        if ($PSBoundParameters.ContainsKey('DynamicMapsPackage') -or $PSBoundParameters.ContainsKey('DynamicMapsMap')) {
            throw 'DllOnly cannot be combined with DynamicMapsPackage or DynamicMapsMap'
        }
        Invoke-DllOnlyDeploy
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($PSBoundParameters.ContainsKey('DynamicMapsPackage')) {
    try {
        if ([string]::IsNullOrWhiteSpace($DynamicMapsPackage)) { throw 'DynamicMapsPackage cannot be empty' }
        Invoke-DynamicMapsDeploy
    } catch { Fail $_.Exception.Message }
    exit 0
}

if ($PSBoundParameters.ContainsKey('DynamicMapsMap')) {
    Fail 'DynamicMapsMap requires DynamicMapsPackage'
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$DistRoot = Join-Path $RepoRoot "dist"

if (-not (Test-Path $DistRoot)) {
    Fail "$DistRoot does not exist -- build the client+server Release first (see docs/progress/dist-manifest.md)"
}

# The two ArenaMapPort mod folders this script is scoped to -- everything it ever reads is under
# the first path of each pair, everything it could ever write is under the second.
$Mappings = @(
    [PSCustomObject]@{
        Label   = "BepInEx client plugin"
        SrcRoot = Join-Path $DistRoot "BepInEx/plugins/maschine-ArenaMapPort"
        DstRoot = Join-Path $SptRoot "BepInEx/plugins/maschine-ArenaMapPort"
    },
    [PSCustomObject]@{
        Label   = "SPT server mod"
        SrcRoot = Join-Path $DistRoot "SPT_Runtime/user/mods/ArenaMapPort"
        DstRoot = Join-Path $SptRoot "SPT_Runtime/user/mods/ArenaMapPort"
    }
)

foreach ($m in $Mappings) {
    if (-not (Test-Path $m.SrcRoot)) {
        Fail "$($m.SrcRoot) does not exist -- dist/ is incomplete (expected $($m.Label) content here)"
    }
}

# ---------------------------------------------------------------------------
# Build the copy plan: every file under each SrcRoot, its destination, size, and whether the
# destination already exists with identical content (SHA256) -- informational in both modes.
# ---------------------------------------------------------------------------
Write-Step "Building copy plan (SptRoot = $SptRoot)"

$plan = @()
foreach ($m in $Mappings) {
    $srcFiles = Get-ChildItem -Path $m.SrcRoot -Recurse -File
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($m.SrcRoot.Length).TrimStart('\', '/')
        $dst = Join-Path $m.DstRoot $rel

        # Safety invariant, checked per-file, in BOTH modes: the destination must fall strictly
        # under <SptRoot>/.../ArenaMapPort/ -- never anywhere else in the SPT install. Uses a real
        # path join + prefix check (not string concatenation trust) so a malformed -SptRoot or a
        # future mapping typo cannot silently point outside the mod folder.
        $dstFull = [System.IO.Path]::GetFullPath((Join-Path $m.DstRoot $rel))
        $dstRootFull = [System.IO.Path]::GetFullPath($m.DstRoot)
        if (-not $dstFull.StartsWith($dstRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail "safety invariant violated: planned destination '$dstFull' escapes '$dstRootFull' -- refusing to run"
        }

        $existed = Test-Path $dst
        $same = $false
        if ($existed) {
            $existingHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash
            $newHash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
            $same = ($existingHash -eq $newHash)
        }

        $plan += [PSCustomObject]@{
            Label      = $m.Label
            Source     = $f.FullName
            Destination = $dstFull
            SizeBytes  = $f.Length
            DestExists = $existed
            DestSame   = $same
        }
    }
}

if ($plan.Count -eq 0) {
    Fail "copy plan is empty -- dist/ has no files under either ArenaMapPort folder"
}

foreach ($m in $Mappings) {
    Write-Host ""
    Write-Host "-- $($m.Label): $($m.SrcRoot)" -ForegroundColor Yellow
    Write-Host "   -> $($m.DstRoot)" -ForegroundColor Yellow
    foreach ($row in ($plan | Where-Object { $_.Label -eq $m.Label })) {
        $status = if (-not $row.DestExists) { "NEW" } elseif ($row.DestSame) { "unchanged (same SHA256)" } else { "WILL OVERWRITE (differs)" }
        $sizeKb = [Math]::Round($row.SizeBytes / 1KB, 1)
        Write-Host ("   {0,-45} {1,10} KB   [{2}]" -f (Split-Path $row.Destination -Leaf), $sizeKb, $status)
    }
}

$total = ($plan | Measure-Object -Property SizeBytes -Sum).Sum
$totalMb = [Math]::Round($total / 1MB, 1)
$newCount = ($plan | Where-Object { -not $_.DestExists }).Count
$overwriteCount = ($plan | Where-Object { $_.DestExists -and -not $_.DestSame }).Count
$unchangedCount = ($plan | Where-Object { $_.DestExists -and $_.DestSame }).Count

Write-Host ""
Write-Host "[deploy-to-spt] plan: $($plan.Count) file(s), $totalMb MB total -- $newCount new, $overwriteCount to overwrite, $unchangedCount already identical"

if (-not $Confirm) {
    Write-Host ""
    Write-Host "[deploy-to-spt] DRY RUN (default) -- no files were copied, nothing under $SptRoot was touched." -ForegroundColor Green
    Write-Host "[deploy-to-spt] Re-run with -Confirm to actually copy the plan above." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Standard deployment remains copy-only; the explicit legacy migration above is separate.
# ---------------------------------------------------------------------------
Write-Step "Copying (real run, -Confirm was passed)"

foreach ($row in $plan) {
    $dstDir = Split-Path $row.Destination -Parent
    if (-not (Test-Path $dstDir)) {
        New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    }
    Copy-Item -Path $row.Source -Destination $row.Destination -Force
    Write-Host "[deploy-to-spt] copied $(Split-Path $row.Destination -Leaf) -> $($row.Destination)"
}

Write-Host ""
Write-Host "[deploy-to-spt] OK -- $($plan.Count) file(s) copied into $SptRoot." -ForegroundColor Green
exit 0
