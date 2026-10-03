# Execute the actual deployment entry point from a disposable fake repo/install.
# No live install or real dist file is read or written by these checks.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('ArenaMapPort-DllDeploy-' + [guid]::NewGuid().ToString('N'))
$rootFull = [IO.Path]::GetFullPath($root)
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $rootFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture root' }
$originalScript = Join-Path $PSScriptRoot '../deploy-to-spt.ps1'
$client = 'BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll'
$server = 'SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll'
$cases = 0
function Write-File($path, $text) {
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}
function New-Fixture([switch]$MissingServer, [switch]$MissingInstalledServer) {
    $case = Join-Path $root ([guid]::NewGuid().ToString('N'))
    $repo = Join-Path $case 'repo'; $spt = Join-Path $case 'spt'
    $script = Join-Path $repo 'tools/deploy-to-spt.ps1'
    Write-File $script ([IO.File]::ReadAllText($originalScript))
    Write-File (Join-Path $repo "dist/$client") 'new-client'
    if (-not $MissingServer) { Write-File (Join-Path $repo "dist/$server") 'new-server' }
    else { [IO.Directory]::CreateDirectory((Join-Path $repo 'dist/SPT_Runtime/user/mods/ArenaMapPort')) | Out-Null }
    Write-File (Join-Path $spt $client) 'old-client'
    if (-not $MissingInstalledServer) { Write-File (Join-Path $spt $server) 'old-server' }
    foreach ($extra in @('BepInEx/plugins/ArenaMapPort/bundles/keep.bundle',
        'BepInEx/plugins/ArenaMapPort/arena-ui/keep.png', 'SPT_Runtime/user/mods/ArenaMapPort/db/base.json')) {
        Write-File (Join-Path $repo "dist/$extra") 'different-staged-content'
        Write-File (Join-Path $spt $extra) 'installed-sentinel'
    }
    Write-File (Join-Path $spt 'BepInEx/config/com.maschine.ArenaMapPort.cfg') 'saved-user-config'
    return @{ Root=$case; Repo=$repo; Spt=$spt; Script=$script }
}
function Snapshot($path, [switch]$ExcludeDlls) {
    return (@(Get-ChildItem -LiteralPath $path -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($path, $_.FullName).Replace('\', '/')
        if (-not $ExcludeDlls -or $relative -notin @($client, $server)) {
            "$relative $((Get-FileHash -LiteralPath $_.FullName).Hash)"
        }
    } | Sort-Object) -join "`n")
}
function Invoke-Deploy($f, [switch]$Confirm, [switch]$Normal, [switch]$Conflict) {
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $f.Script, '-SptRoot', $f.Spt)
    if (-not $Normal) { $arguments += '-DllOnly' }
    if ($Confirm) { $arguments += '-Confirm' }
    if ($Conflict) { $arguments += @('-DynamicMapsPackage', 'never-read-this') }
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
    Require ([IO.File]::ReadAllText((Join-Path $f.Spt 'BepInEx/plugins/ArenaMapPort/bundles/keep.bundle')) -eq 'different-staged-content') 'Normal mode stopped deploying assets'
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
