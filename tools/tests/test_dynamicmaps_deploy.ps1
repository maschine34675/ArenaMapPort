# Runs the real deploy entry point in child pwsh processes against temporary fixtures only.
# No live SPT files, generated source assets, or repository dist files are written or copied.
$ErrorActionPreference = 'Stop'
$deployScript = Join-Path $PSScriptRoot '../deploy-to-spt.ps1'
$pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ArenaMapPort-DynamicMaps-' + [guid]::NewGuid().ToString('N'))
$fixtureFull = [IO.Path]::GetFullPath($fixtureRoot)
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixtureFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture root' }
$script:caseCount = 0

function Write-FixtureFile([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}

function Move-FixtureItem([string]$Source, [string]$Destination) {
    $prefix = $fixtureFull + [IO.Path]::DirectorySeparatorChar
    $sourceFull = (Resolve-Path -LiteralPath $Source).Path
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    if (-not $sourceFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $destinationFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture move' }
    Move-Item -LiteralPath $sourceFull -Destination $destinationFull
}

function New-Fixture([ValidateSet('Equator', 'Bay5')][string]$MapName = 'Equator') {
    $caseRoot = Join-Path $fixtureRoot ([guid]::NewGuid().ToString('N'))
    $package = Join-Path $caseRoot 'package'
    $spt = Join-Path $caseRoot 'spt'
    $plugin = Join-Path $spt 'BepInEx/plugins/mpstark-dynamicmaps'
    $mapId = if ($MapName -eq 'Bay5') { 'arena_bay5' } else { 'arena_equator' }
    $jsonName = "ArenaMapPort_$MapName.jsonc"
    $mapDirectory = "ArenaMapPort_$MapName"
    $bounds = @{ Min = @{ x = -20; y = -30 }; Max = @{ x = 40; y = 50 } }
    $map = @{
        DisplayName = "$MapName (ArenaMapPort)"; Author = 'maschine';
        MapInternalNames = @($mapId); CoordinateRotation = 0; DefaultLevel = 0;
        Bounds = $bounds;
        Layers = @{ 'Ground Floor' = @{
            Level = 0; ImagePath = "Maps/$mapDirectory/Layers/Ground.svg";
            ImageBounds = $bounds;
            GameBounds = @(@{ Min = @{ x = -20; y = -30; z = -5 }; Max = @{ x = 40; y = 50; z = 10 } })
        } };
        Labels = @(); StaticMarkers = @(@{ ImagePath = 'Markers/exit.png'; Position = @{ x = 0; y = 1; z = 0 } })
    }
    $json = Join-Path $package $jsonName
    $svg = Join-Path $package 'Layers/Ground.svg'
    Write-FixtureFile $json ($map | ConvertTo-Json -Depth 20)
    Write-FixtureFile $svg '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 60 80"><path d="M 0 0 L 60 80"/></svg>'
    Write-FixtureFile (Join-Path $plugin 'DynamicMaps.dll') 'fixture DLL sentinel, never loaded'
    Write-FixtureFile (Join-Path $plugin 'Maps/Existing/Map.jsonc') 'existing map sentinel'
    Write-FixtureFile (Join-Path $spt 'BepInEx/config/com.mpstark.DynamicMaps.cfg') 'config sentinel'
    Write-FixtureFile (Join-Path $spt 'BepInEx/plugins/ArenaMapPort/ArenaMapPort.Client.dll') 'MapPort client sentinel'
    Write-FixtureFile (Join-Path $spt 'SPT_Runtime/user/mods/ArenaMapPort/ArenaMapPort.Server.dll') 'MapPort server sentinel'
    return @{ Root = $caseRoot; Package = $package; Spt = $spt; Plugin = $plugin; Map = $map;
        MapName = $MapName; JsonName = $jsonName; MapDirectory = $mapDirectory;
        Json = $json; Svg = $svg; Target = (Join-Path $plugin "Maps/$mapDirectory") }
}

function Get-TreeState([string]$Path) {
    # Do not follow reparse links: record their link text and inspect ordinary descendants only.
    $entries = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = [IO.Path]::GetRelativePath($Path, $item.FullName)
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $entries.Add("L $relative -> $($item.LinkTarget)")
            } elseif ($item.PSIsContainer) {
                $entries.Add("D $relative")
                $pending.Push($item.FullName)
            } else {
                $entries.Add("F $relative $((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)")
            }
        }
    }
    return (($entries | Sort-Object) -join "`n")
}

function Invoke-Deploy($Fixture, [switch]$Confirm, [switch]$DefaultMode, [string]$Script = $deployScript) {
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $Script, '-SptRoot', $Fixture.Spt)
    if (-not $DefaultMode) { $arguments += @('-DynamicMapsPackage', $Fixture.Package) }
    if ($Confirm) { $arguments += '-Confirm' }
    $output = (& $pwshPath @arguments 2>&1 | Out-String)
    return @{ Code = $LASTEXITCODE; Output = $output }
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Pass-Case([string]$Name) {
    $script:caseCount++
    Write-Output "PASS $Name"
}

function Test-Rejected([string]$Name, [scriptblock]$Mutate, [string]$Expected, [switch]$DryRunOnly,
    [ValidateSet('Equator', 'Bay5')][string]$MapName = 'Equator') {
    $fixture = New-Fixture -MapName $MapName
    & $Mutate $fixture
    $before = Get-TreeState $fixture.Root
    $result = Invoke-Deploy $fixture -Confirm:(-not $DryRunOnly)
    Assert-That ($result.Code -ne 0) "$Name unexpectedly succeeded: $($result.Output)"
    Assert-That ($result.Output -match $Expected) "$Name failed for the wrong reason: $($result.Output)"
    Assert-That ((Get-TreeState $fixture.Root) -ceq $before) "$Name wrote files or created directories on rejection"
    Pass-Case $Name
}

function Save-Map($Fixture) { Write-FixtureFile $Fixture.Json ($Fixture.Map | ConvertTo-Json -Depth 20) }

try {
    foreach ($mapName in @('Equator', 'Bay5')) {
        $fixture = New-Fixture -MapName $mapName
        $before = Get-TreeState $fixture.Root
        $result = Invoke-Deploy $fixture
        Assert-That ($result.Code -eq 0 -and $result.Output -match 'DRY RUN' -and $result.Output -match 'plan: 2 file\(s\)') "Valid dry run failed: $($result.Output)"
        Assert-That ((Get-TreeState $fixture.Root) -ceq $before) 'Dry run modified fixture'
        Pass-Case "$mapName valid package dry run"

        $protectedBefore = Get-TreeState $fixture.Spt
        $result = Invoke-Deploy $fixture -Confirm
        Assert-That ($result.Code -eq 0) "Valid real copy failed: $($result.Output)"
        $deployed = @(Get-ChildItem -LiteralPath $fixture.Target -File -Recurse)
        Assert-That ($deployed.Count -eq 2) 'Real copy did not deploy exactly two files'
        foreach ($relative in @($fixture.JsonName, 'Layers/Ground.svg')) {
            Assert-That ((Get-FileHash -LiteralPath (Join-Path $fixture.Package $relative)).Hash -eq
                (Get-FileHash -LiteralPath (Join-Path $fixture.Target $relative)).Hash) "Copied file differs: $relative"
        }
        $ownedPattern = 'Maps[\\/]' + [regex]::Escape($fixture.MapDirectory) + '(?:[\\/ ]|$)'
        $protectedAfter = ((Get-TreeState $fixture.Spt) -split "`n" | Where-Object { $_ -notmatch $ownedPattern }) -join "`n"
        Assert-That ($protectedAfter -ceq $protectedBefore) 'Copy changed something outside its two-file target'
        Pass-Case "$mapName real copy preserves DLLs, configs, other maps and MapPort"

        Write-FixtureFile (Join-Path $fixture.Target 'Layers/Ground.svg') 'old SVG'
        $before = Get-TreeState $fixture.Root
        $result = Invoke-Deploy $fixture
        Assert-That ($result.Code -eq 0 -and $result.Output -match 'WILL OVERWRITE') 'Overwrite dry run did not report changed target'
        Assert-That ((Get-TreeState $fixture.Root) -ceq $before) 'Overwrite dry run wrote target'
        $result = Invoke-Deploy $fixture -Confirm
        Assert-That ($result.Code -eq 0 -and (Get-FileHash -LiteralPath $fixture.Svg).Hash -eq
            (Get-FileHash -LiteralPath (Join-Path $fixture.Target 'Layers/Ground.svg')).Hash) 'Confirmed overwrite did not replace SVG'
        Pass-Case "$mapName overwrite requires Confirm"

        $before = Get-TreeState $fixture.Root
        $result = Invoke-Deploy $fixture -Confirm
        Assert-That ($result.Code -eq 0 -and $result.Output -match 'unchanged \(same SHA256\)') 'Identical package did not report matching hashes'
        Assert-That ((Get-TreeState $fixture.Root) -ceq $before) 'Identical repeat changed fixture content'
        Pass-Case "$mapName repeat copy content remains identical"

        $fixture = New-Fixture -MapName $mapName
        $jsonText = Get-Content -LiteralPath $fixture.Json -Raw
        Write-FixtureFile $fixture.Json ("// generated comment`n" + $jsonText.Replace('"CoordinateRotation": 0,', '"CoordinateRotation": 0, /* rotation */'))
        $result = Invoke-Deploy $fixture
        Assert-That ($result.Code -eq 0) "JSONC comments rejected: $($result.Output)"
        Pass-Case "$mapName JSONC comments accepted"
    }

    # Install both fixed profiles into the same fixture in each order. The second install must
    # preserve the entire first map, DLL/config sentinels and every other existing file/directory.
    foreach ($firstName in @('Equator', 'Bay5')) {
        $secondName = if ($firstName -eq 'Equator') { 'Bay5' } else { 'Equator' }
        $first = New-Fixture -MapName $firstName
        $second = New-Fixture -MapName $secondName
        $second.Spt = $first.Spt
        $second.Plugin = $first.Plugin
        $second.Target = Join-Path $first.Plugin "Maps/$($second.MapDirectory)"
        $result = Invoke-Deploy $first -Confirm
        Assert-That ($result.Code -eq 0) "First map install failed: $($result.Output)"
        $firstBefore = Get-TreeState $first.Target
        $protectedBefore = Get-TreeState $first.Spt
        $result = Invoke-Deploy $second
        Assert-That ($result.Code -eq 0 -and $result.Output -match 'DRY RUN') "Second map dry run failed: $($result.Output)"
        Assert-That ((Get-TreeState $first.Spt) -ceq $protectedBefore) 'Second map dry run changed installed content'
        $result = Invoke-Deploy $second -Confirm
        Assert-That ($result.Code -eq 0) "Second map install failed: $($result.Output)"
        Assert-That ((Get-TreeState $first.Target) -ceq $firstBefore) 'Second map install changed the first map'
        $ownedPattern = 'Maps[\\/]' + [regex]::Escape($second.MapDirectory) + '(?:[\\/ ]|$)'
        $protectedAfter = ((Get-TreeState $first.Spt) -split "`n" | Where-Object { $_ -notmatch $ownedPattern }) -join "`n"
        Assert-That ($protectedAfter -ceq $protectedBefore) 'Second map install changed unrelated files/directories'
        Assert-That (@(Get-ChildItem -LiteralPath $second.Target -File -Recurse).Count -eq 2) 'Second map install did not copy exactly two files'
        foreach ($relative in @($second.JsonName, 'Layers/Ground.svg')) {
            Assert-That ((Get-FileHash -LiteralPath (Join-Path $second.Package $relative)).Hash -eq
                (Get-FileHash -LiteralPath (Join-Path $second.Target $relative)).Hash) "Second map copy differs: $relative"
        }
        Pass-Case "$firstName then $secondName remain independently installed"
    }

    Test-Rejected 'Equator filename with Bay5 content' { param($f) $f.Map.MapInternalNames = @('arena_bay5'); $f.Map.Layers['Ground Floor'].ImagePath = 'Maps/ArenaMapPort_Bay5/Layers/Ground.svg'; Save-Map $f } 'MapInternalNames'
    Test-Rejected 'Bay5 filename with Equator content' { param($f) $f.Map.MapInternalNames = @('arena_equator'); $f.Map.Layers['Ground Floor'].ImagePath = 'Maps/ArenaMapPort_Equator/Layers/Ground.svg'; Save-Map $f } 'MapInternalNames' -MapName Bay5
    Test-Rejected 'Equator ID with Bay5 image' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = 'Maps/ArenaMapPort_Bay5/Layers/Ground.svg'; Save-Map $f } 'ground ImagePath'
    Test-Rejected 'Bay5 ID with Equator image' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = 'Maps/ArenaMapPort_Equator/Layers/Ground.svg'; Save-Map $f } 'ground ImagePath' -MapName Bay5
    Test-Rejected 'Bay5 filename with Equator ID' { param($f) $f.Map.MapInternalNames = @('arena_equator'); Save-Map $f } 'MapInternalNames' -MapName Bay5
    Test-Rejected 'unsupported map filename' { param($f) Move-FixtureItem $f.Json (Join-Path $f.Package 'ArenaMapPort_Factory.jsonc') } 'Unsupported package JSON filename'
    Test-Rejected 'both profile JSON files in one package' { param($f) Copy-Item -LiteralPath $f.Json -Destination (Join-Path $f.Package 'ArenaMapPort_Bay5.jsonc') } 'Package must contain only'
    Test-Rejected 'Bay5 unsupported ID' { param($f) $f.Map.MapInternalNames = @('arena_factory'); Save-Map $f } 'MapInternalNames' -MapName Bay5
    Test-Rejected 'Bay5 multiple map IDs' { param($f) $f.Map.MapInternalNames += 'arena_equator'; Save-Map $f } 'MapInternalNames' -MapName Bay5
    Test-Rejected 'Bay5 image traversal' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = 'Maps/ArenaMapPort_Equator/../ArenaMapPort_Bay5/Layers/Ground.svg'; Save-Map $f } 'ground ImagePath' -MapName Bay5
    Test-Rejected 'Bay5 missing SVG' { param($f) Remove-Item -LiteralPath $f.Svg } 'Layers must contain only' -MapName Bay5
    Test-Rejected 'Bay5 extra hidden file' { param($f) $p = Join-Path $f.Package '.hidden'; Write-FixtureFile $p 'blocked'; (Get-Item -LiteralPath $p -Force).Attributes = [IO.FileAttributes]::Hidden } 'Package must contain only' -MapName Bay5
    Test-Rejected 'Bay5 source junction' { param($f) $p = Join-Path $f.Root 'package-link'; New-Item -ItemType Junction -Path $p -Target $f.Package | Out-Null; $f.Package = $p } 'Linked/reparse' -MapName Bay5
    Test-Rejected 'Bay5 target junction' { param($f) $p = Join-Path $f.Root 'outside-map'; [IO.Directory]::CreateDirectory($p) | Out-Null; New-Item -ItemType Junction -Path $f.Target -Target $p | Out-Null } 'Linked/reparse' -MapName Bay5

    Test-Rejected 'malformed JSON' { param($f) Write-FixtureFile $f.Json '{ broken' } 'FATAL'
    Test-Rejected 'duplicate JSON key' { param($f) $j = Get-Content -LiteralPath $f.Json -Raw; Write-FixtureFile $f.Json ($j -replace '^\{', '{ "CoordinateRotation": 0,') } 'Duplicate JSON property'
    Test-Rejected 'wrong map ID' { param($f) $f.Map.MapInternalNames = @('arena_bay5'); Save-Map $f } 'MapInternalNames'
    Test-Rejected 'multiple map IDs' { param($f) $f.Map.MapInternalNames += 'bigmap'; Save-Map $f } 'MapInternalNames'
    Test-Rejected 'string instead of ID array' { param($f) $f.Map.MapInternalNames = 'arena_equator'; Save-Map $f } 'MapInternalNames'
    Test-Rejected 'wrong rotation' { param($f) $f.Map.CoordinateRotation = 90; Save-Map $f } 'CoordinateRotation'
    Test-Rejected 'boolean default level' { param($f) $f.Map.DefaultLevel = $false; Save-Map $f } 'finite numeric'
    Test-Rejected 'missing layer' { param($f) $f.Map.Layers = @{}; Save-Map $f } 'one ground layer'
    Test-Rejected 'wrong layer level' { param($f) $f.Map.Layers['Ground Floor'].Level = 1; Save-Map $f } 'Level must be 0'
    Test-Rejected 'image traversal path' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = '../config.cfg'; Save-Map $f } 'ground ImagePath'
    Test-Rejected 'dry-run image traversal path' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = '../config.cfg'; Save-Map $f } 'ground ImagePath' -DryRunOnly
    Test-Rejected 'absolute image path' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = 'C:/escape.svg'; Save-Map $f } 'ground ImagePath'
    Test-Rejected 'image path array' { param($f) $f.Map.Layers['Ground Floor'].ImagePath = @('Maps/ArenaMapPort_Equator/Layers/Ground.svg'); Save-Map $f } 'ground ImagePath'
    Test-Rejected 'static marker traversal' { param($f) $f.Map.StaticMarkers[0].ImagePath = 'Markers/../../escape.png'; Save-Map $f } 'static marker ImagePath'
    Test-Rejected 'missing game bounds' { param($f) $f.Map.Layers['Ground Floor'].Remove('GameBounds'); Save-Map $f } 'GameBounds'
    Test-Rejected 'empty game bounds' { param($f) $f.Map.Layers['Ground Floor'].GameBounds = @(); Save-Map $f } 'GameBounds'
    Test-Rejected 'reversed image bounds' { param($f) $f.Map.Layers['Ground Floor'].ImageBounds.Min.x = 100; Save-Map $f } 'Unordered/empty'
    Test-Rejected 'non-numeric coordinate' { param($f) $f.Map.Layers['Ground Floor'].GameBounds[0].Min.z = 'zero'; Save-Map $f } 'finite numeric'
    Test-Rejected 'missing coordinate' { param($f) $f.Map.Bounds.Min.Remove('y'); Save-Map $f } 'finite numeric'
    Test-Rejected 'non-finite coordinate' { param($f) $j = Get-Content -LiteralPath $f.Json -Raw; Write-FixtureFile $f.Json ($j.Replace('-20', '1e999')) } 'finite numeric'
    Test-Rejected 'missing JSON file' { param($f) Remove-Item -LiteralPath $f.Json } 'Package must contain only'
    Test-Rejected 'missing SVG file' { param($f) Remove-Item -LiteralPath $f.Svg } 'Layers must contain only'
    Test-Rejected 'extra root file' { param($f) Write-FixtureFile (Join-Path $f.Package 'unexpected.dll') 'blocked' } 'Package must contain only'
    Test-Rejected 'extra hidden file' { param($f) $p = Join-Path $f.Package '.hidden'; Write-FixtureFile $p 'blocked'; (Get-Item -LiteralPath $p -Force).Attributes = [IO.FileAttributes]::Hidden } 'Package must contain only'
    Test-Rejected 'extra layer file' { param($f) Write-FixtureFile (Join-Path $f.Package 'Layers/extra.svg') 'blocked' } 'Layers must contain only'
    Test-Rejected 'extra empty directory' { param($f) [IO.Directory]::CreateDirectory((Join-Path $f.Package 'Extra')) | Out-Null } 'Package must contain only'
    Test-Rejected 'malformed SVG' { param($f) Write-FixtureFile $f.Svg '<svg>' } 'FATAL'
    Test-Rejected 'non-SVG XML' { param($f) Write-FixtureFile $f.Svg '<root/>' } 'SVG root'
    Test-Rejected 'SVG external entity' { param($f) Write-FixtureFile $f.Svg '<!DOCTYPE svg SYSTEM "file:///missing"><svg xmlns="http://www.w3.org/2000/svg"/>' } 'DTD'
    Test-Rejected 'SVG external image' { param($f) Write-FixtureFile $f.Svg '<svg xmlns="http://www.w3.org/2000/svg"><image href="../../outside.svg"/></svg>' } 'SVG reference'
    Test-Rejected 'SVG external CSS' { param($f) Write-FixtureFile $f.Svg '<svg xmlns="http://www.w3.org/2000/svg"><style>@import "../../outside.css";</style></svg>' } 'SVG external styles'
    Test-Rejected 'SVG script' { param($f) Write-FixtureFile $f.Svg '<svg xmlns="http://www.w3.org/2000/svg"><script/></svg>' } 'Active SVG'
    Test-Rejected 'missing installed plugin' { param($f) Remove-Item -LiteralPath (Join-Path $f.Plugin 'DynamicMaps.dll') } 'DynamicMaps.dll is required'
    Test-Rejected 'plugin DLL is directory' { param($f) $p = Join-Path $f.Plugin 'DynamicMaps.dll'; Remove-Item -LiteralPath $p; [IO.Directory]::CreateDirectory($p) | Out-Null } 'Expected a file'

    # Link fixtures point solely inside the random temporary fixture tree.
    Test-Rejected 'source root junction' { param($f) $p = Join-Path $f.Root 'package-link'; New-Item -ItemType Junction -Path $p -Target $f.Package | Out-Null; $f.Package = $p } 'Linked/reparse'
    Test-Rejected 'source ancestor junction' { param($f) $p = Join-Path $f.Root 'ancestor-link'; New-Item -ItemType Junction -Path $p -Target $f.Root | Out-Null; $f.Package = Join-Path $p 'package' } 'Linked/reparse'
    Test-Rejected 'source Layers junction' { param($f) $p = Join-Path $f.Package 'Layers'; $moved = Join-Path $f.Root 'real-layers'; Move-FixtureItem $p $moved; New-Item -ItemType Junction -Path $p -Target $moved | Out-Null } 'Linked/reparse'
    foreach ($leaf in @('Json', 'Svg')) {
        Test-Rejected "source $leaf symlink" { param($f) $p = $f[$leaf]; $moved = Join-Path $f.Root "real-$leaf"; Move-FixtureItem $p $moved; New-Item -ItemType SymbolicLink -Path $p -Target $moved | Out-Null } 'Linked/reparse'
    }
    Test-Rejected 'target SptRoot junction' { param($f) $p = Join-Path $f.Root 'spt-link'; New-Item -ItemType Junction -Path $p -Target $f.Spt | Out-Null; $f.Spt = $p } 'Linked/reparse'
    Test-Rejected 'target map junction' { param($f) $p = Join-Path $f.Root 'outside-map'; [IO.Directory]::CreateDirectory($p) | Out-Null; New-Item -ItemType Junction -Path $f.Target -Target $p | Out-Null } 'Linked/reparse'
    Test-Rejected 'dry-run target map junction' { param($f) $p = Join-Path $f.Root 'outside-map'; [IO.Directory]::CreateDirectory($p) | Out-Null; New-Item -ItemType Junction -Path $f.Target -Target $p | Out-Null } 'Linked/reparse' -DryRunOnly
    Test-Rejected 'target Layers junction' { param($f) $p = Join-Path $f.Root 'outside-layers'; [IO.Directory]::CreateDirectory($p) | Out-Null; [IO.Directory]::CreateDirectory($f.Target) | Out-Null; New-Item -ItemType Junction -Path (Join-Path $f.Target 'Layers') -Target $p | Out-Null } 'Linked/reparse'
    foreach ($relative in @('ArenaMapPort_Equator.jsonc', 'Layers/Ground.svg')) {
        Test-Rejected "target $relative symlink" { param($f) $p = Join-Path $f.Root 'outside-file'; Write-FixtureFile $p 'sentinel'; $dst = Join-Path $f.Target $relative; [IO.Directory]::CreateDirectory((Split-Path $dst -Parent)) | Out-Null; New-Item -ItemType SymbolicLink -Path $dst -Target $p | Out-Null } 'Linked/reparse'
    }
    Test-Rejected 'target hardlink' { param($f) $p = Join-Path $f.Root 'outside-file'; Write-FixtureFile $p 'sentinel'; [IO.Directory]::CreateDirectory($f.Target) | Out-Null; New-Item -ItemType HardLink -Path (Join-Path $f.Target 'ArenaMapPort_Equator.jsonc') -Target $p | Out-Null } 'Linked/reparse'
    Test-Rejected 'target broken symlink' { param($f) [IO.Directory]::CreateDirectory($f.Target) | Out-Null; New-Item -ItemType SymbolicLink -Path (Join-Path $f.Target 'ArenaMapPort_Equator.jsonc') -Target (Join-Path $f.Root 'missing.json') | Out-Null } 'Linked/reparse'
    Test-Rejected 'target Layers is file' { param($f) Write-FixtureFile (Join-Path $f.Target 'Layers') 'sentinel' } 'Expected a directory'
    Test-Rejected 'target JSON is directory' { param($f) [IO.Directory]::CreateDirectory((Join-Path $f.Target 'ArenaMapPort_Equator.jsonc')) | Out-Null } 'Expected a file'
    Test-Rejected 'plugin DLL symlink' { param($f) $p = Join-Path $f.Plugin 'DynamicMaps.dll'; $moved = Join-Path $f.Root 'real-plugin.dll'; Move-FixtureItem $p $moved; New-Item -ItemType SymbolicLink -Path $p -Target $moved | Out-Null } 'Linked/reparse'
    Test-Rejected 'package overlaps target' { param($f) Copy-Item -LiteralPath $f.Package -Destination $f.Target -Recurse; $f.Package = $f.Target } 'must not overlap'
    Test-Rejected 'empty package argument' { param($f) $f.Package = '' } 'DynamicMapsPackage cannot be empty'

    # Standard mode uses the untouched two dist mappings. Test an independent 72-file distribution.
    $fixture = New-Fixture
    $fakeRepo = Join-Path $fixture.Root 'repo'
    $fixtureScript = Join-Path $fakeRepo 'tools/deploy-to-spt.ps1'
    Write-FixtureFile $fixtureScript (Get-Content -LiteralPath $deployScript -Raw)
    $clientPath = 'BepInEx/plugins/ArenaMapPort'
    $serverPath = 'SPT_Runtime/user/mods/ArenaMapPort'
    for ($index = 0; $index -lt 72; $index++) {
        $relative = if ($index -lt 60) { "$clientPath/nested/file-$index.bin" } else { "$serverPath/data/file-$index.json" }
        Write-FixtureFile (Join-Path $fakeRepo "dist/$relative") "default fixture $index"
    }
    Write-FixtureFile (Join-Path $fakeRepo 'dist/BepInEx/plugins/OtherMod/should-not-copy.dll') 'excluded sentinel'
    $before = Get-TreeState $fixture.Root
    $result = Invoke-Deploy $fixture -DefaultMode -Script $fixtureScript
    Assert-That ($result.Code -eq 0 -and $result.Output -match 'plan: 72 file\(s\)' -and $result.Output -match 'DRY RUN') "Default dry run failed: $($result.Output)"
    Assert-That ((Get-TreeState $fixture.Root) -ceq $before) 'Default dry run changed fixture'
    Pass-Case 'default mode retains 72-file dry-run mapping'
    $dynamicBefore = Get-TreeState $fixture.Plugin
    $result = Invoke-Deploy $fixture -DefaultMode -Confirm -Script $fixtureScript
    Assert-That ($result.Code -eq 0 -and $result.Output -match '72 file\(s\) copied') "Default Confirm failed: $($result.Output)"
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $fakeRepo 'dist') -File -Recurse | Where-Object { $_.FullName -notmatch 'OtherMod' }) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $fakeRepo 'dist'), $file.FullName)
        Assert-That ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath (Join-Path $fixture.Spt $relative)).Hash) "Default copy mismatch: $relative"
    }
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $fixture.Spt 'BepInEx/plugins/OtherMod'))) 'Default mapping copied third-party directory'
    Assert-That ((Get-TreeState $fixture.Plugin) -ceq $dynamicBefore) 'Default mapping changed DynamicMaps'
    Pass-Case 'default Confirm copies only the original two mappings'

    Write-Output "DYNAMICMAPS_DEPLOY_TESTS_PASSED $script:caseCount cases"
} finally {
    # Resolve and validate the exact temporary root immediately before recursive cleanup.
    if (Test-Path -LiteralPath $fixtureFull) {
        $resolvedFixture = (Resolve-Path -LiteralPath $fixtureFull).Path
        if ($resolvedFixture -ne $fixtureFull -or -not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-Item -LiteralPath $resolvedFixture).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe fixture cleanup path' }
        # Unlink fixture reparse points first so recursive cleanup cannot traverse them.
        $links = Get-ChildItem -LiteralPath $resolvedFixture -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
        foreach ($link in $links) { Remove-Item -LiteralPath $link.FullName -Force }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
