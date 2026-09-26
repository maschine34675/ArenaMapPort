<#
.SYNOPSIS
  Promotes D:/ArenaMapPort/dist/ into a live SPT install (docs/SPEC.md decision #9 / §G.S4's own
  "a separate, explicit, user-approved step" — this script IS that step, not something any build
  target or CI-style pipeline runs automatically).

.DESCRIPTION
  Copies dist/BepInEx/plugins/ArenaMapPort/**      -> <SptRoot>/BepInEx/plugins/ArenaMapPort/**
        dist/SPT_Runtime/user/mods/ArenaMapPort/** -> <SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/**

  SAFETY (task-mandated, not optional):
    - DEFAULT behaviour is a DRY RUN. It prints the exact copy plan (source -> destination, size,
      whether the destination already exists, whether its content already matches by SHA256) and
      copies NOTHING. This is true with zero arguments and with only -SptRoot given.
    - A real copy only happens when -Confirm is passed explicitly.
    - This script NEVER deletes anything, in either mode, anywhere -- not just "outside its own
      ArenaMapPort folders": it performs Copy-Item only (create/overwrite), never Remove-Item. This
      trivially satisfies "never deletes anything outside its own ArenaMapPort folders" by deleting
      nothing at all, which is the safer of the two ways to satisfy that constraint (a copy-only
      script cannot accidentally widen its own blast radius later). Every destination path is also
      asserted, before any write, to fall strictly under
      <SptRoot>/BepInEx/plugins/ArenaMapPort/ or <SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/ in
      standard mode; DynamicMaps mode only writes its two declared files below mpstark-dynamicmaps/
      Maps/ArenaMapPort_Equator/ or Maps/ArenaMapPort_Bay5/, selected explicitly. Full-path prefix checks
      protect the destinations -- the script refuses to run
      at all if that invariant doesn't hold for every planned file, in EITHER mode.
    - Never reads or writes anything under D:/ArenaMapPort/dist/ other than read-only enumeration
      (source of truth, untouched).

.PARAMETER SptRoot
  Root of the live SPT install to promote into. Default D:/SPT41.

.PARAMETER Confirm
  Perform the real copy. Without this switch the script only prints the plan and exits 0.

.PARAMETER DynamicMapsPackage
  Optional data-only package directory. Deploys exactly ArenaMapPort_<map>.jsonc and
  Layers/Ground.svg into the installed mpstark-dynamicmaps/Maps/ArenaMapPort_<map> folder.
  This separate mode never reads dist/ or copies client/server files. It requires DynamicMaps.dll,
  validates the complete package and refuses links in either path. Default remains a dry run.

.PARAMETER DynamicMapsMap
  Selects Equator (default) or Bay5 for DynamicMapsPackage. The selected map fixes the package
  filename, internal map ID, image path and destination; no other map destinations are allowed.

.EXAMPLE
  # dry run (default) -- prints the plan, copies nothing:
  pwsh tools/deploy-to-spt.ps1

.EXAMPLE
  # real promotion, after reviewing the dry-run plan and getting the user's explicit go-ahead:
  pwsh tools/deploy-to-spt.ps1 -Confirm

.EXAMPLE
  # data-only dry run; add -Confirm for the real copy:
  pwsh tools/deploy-to-spt.ps1 -DynamicMapsPackage work/dynamicmaps/equator/pack
#>
[CmdletBinding()]
param(
    [string]$SptRoot = "D:/SPT41",
    [switch]$Confirm,
    [string]$DynamicMapsPackage,
    [ValidateSet('Equator', 'Bay5')]
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

# This intentionally separate path cannot widen the regular client/server deployment mappings.
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
    $mapName = if ($DynamicMapsMap -eq 'Bay5') { 'Bay5' } else { 'Equator' }
    $mapFile = "ArenaMapPort_$mapName.jsonc"
    $package = Assert-DynamicMapsPackage $DynamicMapsPackage $mapName
    $pluginRoot = Assert-PlainPath (Join-Path $SptRoot 'BepInEx/plugins/mpstark-dynamicmaps') 'Directory'
    $pluginDll = Assert-PlainPath (Join-Path $pluginRoot 'DynamicMaps.dll') 'File'
    if (-not (Test-Path -LiteralPath $pluginDll -PathType Leaf)) { throw "Installed DynamicMaps.dll is required: $pluginDll" }
    $target = Assert-PlainPath (Join-Path $pluginRoot "Maps/ArenaMapPort_$mapName") 'Directory'
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
        Write-Host "[deploy-to-spt] copied $($row.Destination)"
    }
    Write-Host '[deploy-to-spt] OK -- 2 DynamicMaps data files copied.' -ForegroundColor Green
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
        SrcRoot = Join-Path $DistRoot "BepInEx/plugins/ArenaMapPort"
        DstRoot = Join-Path $SptRoot "BepInEx/plugins/ArenaMapPort"
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
# Real copy. Copy-Item only -- this script never calls Remove-Item, anywhere, in either mode.
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
