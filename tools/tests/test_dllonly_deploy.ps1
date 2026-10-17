# Execute the actual deployment entry point from a disposable fake repo/install.
# No live install or real dist file is read or written by these checks.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('ArenaMapPort-DllDeploy-' + [guid]::NewGuid().ToString('N'))
$rootFull = [IO.Path]::GetFullPath($root)
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $rootFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture root' }
$originalScript = Join-Path $PSScriptRoot '../deploy-to-spt.ps1'
$client = 'BepInEx/plugins/maschine-ArenaMapPort/maschine-ArenaMapPort.Client.dll'
$server = 'SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll'
$cases = 0
function Write-File($path, $text) {
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}
function New-Fixture([switch]$MissingServer, [switch]$MissingInstalledServer,
    [switch]$MissingClient, [switch]$MissingInstalledClient) {
    $case = Join-Path $root ([guid]::NewGuid().ToString('N'))
    $repo = Join-Path $case 'repo'; $spt = Join-Path $case 'spt'
    $script = Join-Path $repo 'tools/deploy-to-spt.ps1'
    Write-File $script ([IO.File]::ReadAllText($originalScript))
    if (-not $MissingClient) { Write-File (Join-Path $repo "dist/$client") 'new-client' }
    if (-not $MissingServer) { Write-File (Join-Path $repo "dist/$server") 'new-server' }
    else { [IO.Directory]::CreateDirectory((Join-Path $repo 'dist/SPT_Runtime/user/mods/ArenaMapPort')) | Out-Null }
    if (-not $MissingInstalledClient) { Write-File (Join-Path $spt $client) 'old-client' }
    if (-not $MissingInstalledServer) { Write-File (Join-Path $spt $server) 'old-server' }
    foreach ($extra in @('BepInEx/plugins/maschine-ArenaMapPort/bundles/keep.bundle',
        'BepInEx/plugins/maschine-ArenaMapPort/arena-ui/keep.png', 'SPT_Runtime/user/mods/ArenaMapPort/db/base.json')) {
        Write-File (Join-Path $repo "dist/$extra") 'different-staged-content'
        Write-File (Join-Path $spt $extra) 'installed-sentinel'
    }
    Write-File (Join-Path $spt 'BepInEx/config/com.maschine.ArenaMapPort.cfg') 'saved-user-config'
    return @{ Root=$case; Repo=$repo; Spt=$spt; Script=$script }
}
function Snapshot($path, [switch]$ExcludeDlls, [switch]$ExcludeClient) {
    return (@(Get-ChildItem -LiteralPath $path -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($path, $_.FullName).Replace('\', '/')
        if ((-not $ExcludeDlls -or $relative -notin @($client, $server)) -and
            (-not $ExcludeClient -or $relative -ne $client)) {
            "$relative $((Get-FileHash -LiteralPath $_.FullName).Hash)"
        }
    } | Sort-Object) -join "`n")
}
function Invoke-Deploy($f, [switch]$Confirm, [switch]$Normal, [switch]$Conflict,
    [string]$DllTarget, [string[]]$ExtraArguments) {
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $f.Script, '-SptRoot', $f.Spt)
    if (-not $Normal) { $arguments += '-DllOnly' }
    if ($PSBoundParameters.ContainsKey('DllTarget')) { $arguments += @('-DllTarget', $DllTarget) }
    if ($Confirm) { $arguments += '-Confirm' }
    if ($Conflict) { $arguments += @('-DynamicMapsPackage', 'never-read-this') }
    if ($ExtraArguments) { $arguments += $ExtraArguments }
    $output = (& pwsh @arguments 2>&1 | Out-String)
    return @{ Code=$LASTEXITCODE; Output=$output }
}
function Require($condition, $message) { if (-not $condition) { throw $message } }
try {
    $f = New-Fixture; $before = Snapshot $f.Root
    $result = Invoke-Deploy $f
    Require ($result.Code -eq 0 -and $result.Output -match 'plan: 2 file\(s\)' -and $result.Output -match 'DRY RUN') "Dry run failed: $($result.Output)"
    Require ((Snapshot $f.Root) -ceq $before) 'Dry run changed files'
    $cases++

    $protected = Snapshot $f.Spt -ExcludeDlls
    $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -eq 0 -and $result.Output -match '2 verified DLLs updated') "DLL update failed: $($result.Output)"
    foreach ($relative in @($client, $server)) {
        $installedHash = (Get-FileHash -LiteralPath (Join-Path $f.Spt $relative)).Hash
        $stagedHash = (Get-FileHash -LiteralPath (Join-Path $f.Repo "dist/$relative")).Hash
        Require ($installedHash -eq $stagedHash) "DLL differs: $relative"
    }
    Require ((Snapshot $f.Spt -ExcludeDlls) -ceq $protected) 'DLL update changed bundles/assets/data/config'
    $cases++

    $before = Snapshot $f.Root; $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -eq 0 -and $result.Output -match 'unchanged \(same SHA256\)') 'Identical update failed'
    Require ((Snapshot $f.Root) -ceq $before) 'Repeat update changed bytes'
    $cases++

    $f = New-Fixture; $before = Snapshot $f.Root
    $result = Invoke-Deploy $f -DllTarget Client
    Require ($result.Code -eq 0 -and $result.Output -match 'plan: 1 file\(s\)' -and
        $result.Output -match 'DRY RUN' -and $result.Output -notmatch 'maschine-ArenaMapPort.Server.dll') "Client dry run failed: $($result.Output)"
    Require ((Snapshot $f.Root) -ceq $before) 'Client dry run changed files'
    $cases++

    $protected = Snapshot $f.Spt -ExcludeClient
    $result = Invoke-Deploy $f -Confirm -DllTarget Client
    Require ($result.Code -eq 0 -and $result.Output -match '1 verified DLLs updated') "Client update failed: $($result.Output)"
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt $client)) -eq 'new-client') 'Client-only update did not replace client'
    Require ((Snapshot $f.Spt -ExcludeClient) -ceq $protected) 'Client-only update changed server/assets/data/config'
    $cases++

    $before = Snapshot $f.Root; $result = Invoke-Deploy $f -Confirm -DllTarget Client
    Require ($result.Code -eq 0 -and $result.Output -match 'unchanged \(same SHA256\)') 'Identical client update failed'
    Require ((Snapshot $f.Root) -ceq $before) 'Repeat client update changed bytes'
    $cases++

    foreach ($missing in @(@{MissingServer=$true}, @{MissingInstalledServer=$true},
        @{MissingServer=$true; MissingInstalledServer=$true})) {
        $f = New-Fixture @missing; $protected = Snapshot $f.Spt -ExcludeClient
        $result = Invoke-Deploy $f -Confirm -DllTarget Client
        Require ($result.Code -eq 0 -and $result.Output -match 'plan: 1 file\(s\)') "Client update required server DLL: $($result.Output)"
        Require ([IO.File]::ReadAllText((Join-Path $f.Spt $client)) -eq 'new-client') 'Client update with missing server failed'
        Require ((Snapshot $f.Spt -ExcludeClient) -ceq $protected) 'Client update with missing server changed other files'
        $cases++
    }

    # Exclusive locks make even a hash/read of either server DLL fail. Both client paths must work.
    $f = New-Fixture; $protected = Snapshot $f.Spt -ExcludeClient
    $locks = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($path in @((Join-Path $f.Repo "dist/$server"), (Join-Path $f.Spt $server))) {
            $locks.Add([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None))
        }
        $result = Invoke-Deploy $f -DllTarget Client
        Require ($result.Code -eq 0 -and $result.Output -match 'DRY RUN') "Client dry run read locked server: $($result.Output)"
        Require ([IO.File]::ReadAllText((Join-Path $f.Spt $client)) -eq 'old-client') 'Locked-server dry run changed client'
        $cases++
        $result = Invoke-Deploy $f -Confirm -DllTarget Client
        Require ($result.Code -eq 0) "Client update read locked server: $($result.Output)"
        $cases++
    } finally { foreach ($lock in $locks) { $lock.Dispose() } }
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt $client)) -eq 'new-client') 'Client update with locked server failed'
    Require ((Snapshot $f.Spt -ExcludeClient) -ceq $protected) 'Client update with locked server changed other files'
    Require ([IO.File]::ReadAllText((Join-Path $f.Repo "dist/$server")) -eq 'new-server') 'Client update modified staged server'

    foreach ($missing in @(@{MissingClient=$true}, @{MissingInstalledClient=$true})) {
        $f = New-Fixture @missing; $before = Snapshot $f.Root
        $result = Invoke-Deploy $f -Confirm -DllTarget Client
        Require ($result.Code -ne 0 -and $result.Output -match 'Missing staged DLL|existing installed DLL') 'Client-only accepted a missing client DLL'
        Require ((Snapshot $f.Root) -ceq $before) 'Missing client DLL changed files'
        $cases++
    }

    $f = New-Fixture; $before = Snapshot $f.Root
    $lock = [IO.File]::Open((Join-Path $f.Spt $client), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $result = Invoke-Deploy $f -Confirm -DllTarget Client } finally { $lock.Dispose() }
    Require ($result.Code -ne 0) 'Locked client unexpectedly updated'
    Require ((Snapshot $f.Root) -ceq $before) 'Locked client update changed files'
    $cases++

    # Explicit target selection is never meaningful outside DLL-only mode, including Both.
    foreach ($target in @('Client', 'Both')) {
        foreach ($mode in @('', '-RenameClientFolder', '-RemoveDebugHelper', '-RemoveLegacyEquatorDb', '-RemoveLegacyShaderSidecar',
            '-RemoveLegacyDynamicMaps', '-DynamicMapsMap', '-DynamicMapsPackage')) {
            $extra = switch ($mode) {
                '-DynamicMapsMap' { @($mode, 'Equator') }
                '-DynamicMapsPackage' { @($mode, 'never-read-this') }
                '' { @() }
                default { @($mode) }
            }
            $f = New-Fixture; $before = Snapshot $f.Root
            $result = Invoke-Deploy $f -Confirm -Normal -DllTarget $target -ExtraArguments $extra
            Require ($result.Code -ne 0 -and $result.Output -match 'DllTarget requires DllOnly') 'DllTarget without DllOnly was accepted'
            Require ((Snapshot $f.Root) -ceq $before) 'Rejected DllTarget mode changed files'
            $cases++
        }
    }

    foreach ($mode in @('-RenameClientFolder', '-RemoveDebugHelper', '-RemoveLegacyEquatorDb', '-RemoveLegacyShaderSidecar',
        '-RemoveLegacyDynamicMaps', '-DynamicMapsMap', '-DynamicMapsPackage')) {
        $extra = switch ($mode) {
            '-DynamicMapsMap' { @($mode, 'Equator') }
            '-DynamicMapsPackage' { @($mode, 'never-read-this') }
            default { @($mode) }
        }
        $f = New-Fixture; $before = Snapshot $f.Root
        $result = Invoke-Deploy $f -Confirm -DllTarget Client -ExtraArguments $extra
        Require ($result.Code -ne 0 -and $result.Output -match 'cannot be combined') "Mixed Client/mode was accepted: $mode"
        Require ((Snapshot $f.Root) -ceq $before) 'Rejected mixed Client mode changed files'
        $cases++
    }

    $f = New-Fixture; $before = Snapshot $f.Root
    $result = Invoke-Deploy $f -Confirm -DllTarget Server
    Require ($result.Code -ne 0) 'Unsupported DLL target was accepted'
    Require ((Snapshot $f.Root) -ceq $before) 'Rejected unsupported DLL target changed files'
    $cases++

    $f = New-Fixture; $result = Invoke-Deploy $f -Confirm -DllTarget Both
    Require ($result.Code -eq 0 -and $result.Output -match '2 verified DLLs updated') 'Explicit Both target failed'
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt $client)) -eq 'new-client' -and
        [IO.File]::ReadAllText((Join-Path $f.Spt $server)) -eq 'new-server') 'Explicit Both target did not update both DLLs'
    $cases++

    $before = Snapshot $f.Root; $result = Invoke-Deploy $f -Confirm -Conflict
    Require ($result.Code -ne 0 -and $result.Output -match 'cannot be combined') 'Mixed modes were accepted'
    Require ((Snapshot $f.Root) -ceq $before) 'Rejected mixed modes changed files'
    $cases++

    $f = New-Fixture -MissingServer; $before = Snapshot $f.Root; $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -ne 0 -and $result.Output -match 'Missing staged DLL') 'Incomplete pair was accepted'
    Require ((Snapshot $f.Root) -ceq $before) 'Missing staged server partially deployed client'
    $cases++

    $f = New-Fixture -MissingInstalledServer; $before = Snapshot $f.Root; $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -ne 0 -and $result.Output -match 'existing installed DLL') 'Incomplete installation was accepted'
    Require ((Snapshot $f.Root) -ceq $before) 'Incomplete installation created files or partially deployed client'
    $cases++

    $f = New-Fixture; $before = Snapshot $f.Root
    $lock = [IO.File]::Open((Join-Path $f.Spt $server), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $result = Invoke-Deploy $f -Confirm } finally { $lock.Dispose() }
    Require ($result.Code -ne 0) 'Locked DLL unexpectedly updated'
    Require ((Snapshot $f.Root) -ceq $before) 'Locked server partially deployed client'
    $cases++

    $f = New-Fixture; $before = Snapshot $f.Spt
    $scriptText = [IO.File]::ReadAllText($f.Script)
    $writeAnchor = '$stream.Position = 0; $stream.Write($bytes, 0, $bytes.Length); $stream.SetLength($bytes.Length); $stream.Flush($true)'
    $firstWrite = $scriptText.IndexOf($writeAnchor, [StringComparison]::Ordinal)
    Require ($firstWrite -ge 0) 'Missing deployment-write fault-injection anchor'
    $writeEnd = $firstWrite + $writeAnchor.Length
    Write-File $f.Script ($scriptText.Insert($writeEnd, "`n            if (`$i -eq 1) { throw 'injected deployment write failure' }"))
    $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -ne 0 -and $result.Output -match 'injected deployment write failure') 'Injected write failure was not surfaced'
    Require ((Snapshot $f.Spt) -ceq $before) 'Failed second write did not restore both original DLLs'
    $cases++

    $clientFailure = New-Fixture; $clientBefore = Snapshot $clientFailure.Spt
    $clientScript = [IO.File]::ReadAllText($clientFailure.Script)
    $clientWriteEnd = $clientScript.IndexOf($writeAnchor, [StringComparison]::Ordinal) + $writeAnchor.Length
    Write-File $clientFailure.Script ($clientScript.Insert($clientWriteEnd, "`n            throw 'injected client write failure'"))
    $result = Invoke-Deploy $clientFailure -Confirm -DllTarget Client
    Require ($result.Code -ne 0 -and $result.Output -match 'injected client write failure') 'Injected client write failure was not surfaced'
    Require ((Snapshot $clientFailure.Spt) -ceq $clientBefore) 'Failed client-only write did not restore client or changed other files'
    $cases++

    # A failed first restoration must not prevent attempting/verifying the second one.
    $scriptText = [IO.File]::ReadAllText($f.Script)
    $restoreAnchor = '$stream = $streams[$i]; $bytes = $backups[$i]'
    Require ($scriptText.Contains($restoreAnchor)) 'Missing restore fault-injection anchor'
    Write-File $f.Script ($scriptText.Replace($restoreAnchor,
        "if (`$i -eq 0) { throw 'injected first restore failure' }`n                    " + $restoreAnchor))
    $result = Invoke-Deploy $f -Confirm
    Require ($result.Code -ne 0 -and $result.Output -match 'injected deployment write failure' -and $result.Output -match 'Restore failed') 'Combined write/restore failures were not surfaced'
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt $server)) -eq 'old-server') 'First restore failure prevented restoring server'
    $cases++

    $f = New-Fixture; $result = Invoke-Deploy $f -Confirm -Normal
    Require ($result.Code -eq 0 -and $result.Output -match 'plan: 5 file\(s\)') "Normal whole-dist deploy changed: $($result.Output)"
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt 'BepInEx/plugins/maschine-ArenaMapPort/bundles/keep.bundle')) -eq 'different-staged-content') 'Normal mode stopped deploying assets'
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt 'BepInEx/config/com.maschine.ArenaMapPort.cfg')) -eq 'saved-user-config') 'Normal mode changed config'
    $cases++
    Write-Output "DLL_ONLY_DEPLOY_TESTS_PASSED ($cases cases)"
} finally {
    if (Test-Path -LiteralPath $rootFull) {
        $resolved = (Resolve-Path -LiteralPath $rootFull).Path
        if ($resolved -ne $rootFull -or -not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing unsafe fixture cleanup'
        }
        Remove-Item -LiteralPath $rootFull -Recurse -Force
    }
}
