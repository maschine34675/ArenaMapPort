$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../deploy-to-spt.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('equator-db-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$temp/tools" -Force | Out-Null
Copy-Item -LiteralPath $source -Destination "$temp/tools/deploy-to-spt.ps1"
$mod = 'SPT_Runtime/user/mods/ArenaMapPort'
$live = "$temp/live/$mod"
$dist = "$temp/dist/$mod"
foreach ($root in @($live, $dist)) {
    New-Item -ItemType Directory -Path "$root/db/arena_equator" -Force | Out-Null
    Set-Content -LiteralPath "$root/db/arena_equator/base.json" -Value '{"Id":"arena_equator"}'
    Set-Content -LiteralPath "$root/maschine-ArenaMapPort.Server.dll" -Value 'matching DLL fixture'
}
Set-Content -LiteralPath "$live/db/base.json" -Value '{"Id":"arena_equator","old":true}'
Set-Content -LiteralPath "$live/db/unrelated.json" -Value 'preserve'
function Run([bool]$Success, [string[]]$ExtraArgs) {
    $output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot "$temp/live" -RemoveLegacyEquatorDb @ExtraArgs 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw "Unexpected migration outcome: $output" }
}
Run $true @()
if (-not (Test-Path -LiteralPath "$live/db/base.json") -or (Test-Path -LiteralPath "$temp/work")) { throw 'Dry run changed files' }
Set-Content -LiteralPath "$live/maschine-ArenaMapPort.Server.dll" -Value 'stale'
Run $false @('-Confirm')
Copy-Item -LiteralPath "$dist/maschine-ArenaMapPort.Server.dll" -Destination "$live/maschine-ArenaMapPort.Server.dll" -Force
Set-Content -LiteralPath "$live/db/base.json" -Value '{"Id":"other"}'
Run $false @('-Confirm')
Set-Content -LiteralPath "$live/db/base.json" -Value '{"Id":"arena_equator","old":true}'
Run $false @('-Confirm','-DllOnly')
Run $true @('-Confirm')
if (Test-Path -LiteralPath "$live/db/base.json") { throw 'Legacy file remains' }
$backups = @(Get-ChildItem -LiteralPath "$temp/work/deployment-backups" -Recurse -File)
if ($backups.Count -ne 1 -or (Get-Content -LiteralPath $backups[0].FullName -Raw | ConvertFrom-Json).old -ne $true) { throw 'Original backup missing' }
if ((Get-Content -LiteralPath "$live/db/unrelated.json").Trim() -ne 'preserve') { throw 'Unrelated data changed' }
Run $true @('-Confirm')
# An ancestor redirected outside the owned mod must fail before any mutation.
$redirect = "$temp/redirect"
New-Item -ItemType Directory -Path "$redirect/SPT_Runtime/user/mods" -Force | Out-Null
New-Item -ItemType Junction -Path "$redirect/$mod" -Target $live | Out-Null
$output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot $redirect -RemoveLegacyEquatorDb -Confirm 2>&1
if ($LASTEXITCODE -eq 0) { throw 'Linked mod root was accepted' }
# Remove the junction itself only; no recursive deletion follows a link.
Remove-Item -LiteralPath "$redirect/$mod" -Force
$resolved = [IO.Path]::GetFullPath($temp)
if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture root' }
Remove-Item -LiteralPath $resolved -Recurse -Force
Write-Output 'EQUATOR_DB_DEPLOY_TESTS_PASSED'
