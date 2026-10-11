$ErrorActionPreference = 'Stop'
$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../deploy-to-spt.ps1'))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('amp-client-rename-' + [Guid]::NewGuid().ToString('N'))
$old = Join-Path $temp 'BepInEx/plugins/ArenaMapPort'
$new = Join-Path $temp 'BepInEx/plugins/maschine-ArenaMapPort'
$server = Join-Path $temp 'SPT_Runtime/user/mods/ArenaMapPort'
$checks = 0
function Require($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
function Run([bool]$success, [string[]]$extra) {
    $output = & pwsh -NoProfile -File $script -SptRoot $temp -RenameClientFolder @extra 2>&1
    Require (($LASTEXITCODE -eq 0) -eq $success) "Unexpected migration outcome: $output"
}
try {
    New-Item -ItemType Directory -Path "$old/data/empty", $server -Force | Out-Null
    [IO.File]::WriteAllText("$old/maschine-ArenaMapPort.Client.dll", 'client sentinel')
    [IO.File]::WriteAllText("$old/data/unrecognized.bin", 'preserve custom data')
    [IO.File]::WriteAllText("$server/maschine-ArenaMapPort.Server.dll", 'server sentinel')
    $before = (Get-FileHash -LiteralPath "$old/data/unrecognized.bin").Hash
    $serverBefore = (Get-FileHash -LiteralPath "$server/maschine-ArenaMapPort.Server.dll").Hash
    Run $true @()
    Require ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $new)) 'Dry run moved files'
    Run $false @('-Confirm', '-DllOnly')
    Run $false @('-Confirm', '-RemoveDebugHelper')
    Run $false @('-Confirm', '-RemoveLegacyEquatorDb')
    Run $false @('-Confirm', '-RemoveLegacyDynamicMaps')
    Run $false @('-Confirm', '-DynamicMapsMap', 'Bay5')
    Run $false @('-Confirm', '-DynamicMapsPackage', $temp)
    New-Item -ItemType Directory -Path $new | Out-Null
    Run $false @('-Confirm')
    Require (Test-Path -LiteralPath "$old/maschine-ArenaMapPort.Client.dll") 'Collision touched source'
    Remove-Item -LiteralPath $new
    [IO.File]::WriteAllText($new, 'file collision')
    Run $false @('-Confirm')
    Remove-Item -LiteralPath $new
    [IO.Directory]::Move("$old/data", "$old/held-data")
    New-Item -ItemType Junction -Path "$old/data" -Target $server | Out-Null
    try { Run $false @('-Confirm') }
    finally { Remove-Item -LiteralPath "$old/data" -Force }
    [IO.Directory]::Move("$old/held-data", "$old/data")
    [IO.File]::Move("$old/maschine-ArenaMapPort.Client.dll", "$old/saved.dll")
    Run $false @('-Confirm')
    [IO.File]::Move("$old/saved.dll", "$old/maschine-ArenaMapPort.Client.dll")
    Run $true @('-Confirm')
    Require (-not (Test-Path -LiteralPath $old)) 'Old folder remains'
    Require ((Get-FileHash -LiteralPath "$new/data/unrecognized.bin").Hash -eq $before) 'Moved data changed'
    Require (Test-Path -LiteralPath "$new/data/empty" -PathType Container) 'Empty directory lost'
    Require ((Get-FileHash -LiteralPath "$server/maschine-ArenaMapPort.Server.dll").Hash -eq $serverBefore) 'Server changed'
    Run $true @('-Confirm')
    Run $true @()
    Require ((Get-Content -LiteralPath "$new/maschine-ArenaMapPort.Client.dll" -Raw) -eq 'client sentinel') 'Client changed'
    Write-Output "CLIENT_FOLDER_DEPLOY_TESTS_PASSED $checks checks"
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture root' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
