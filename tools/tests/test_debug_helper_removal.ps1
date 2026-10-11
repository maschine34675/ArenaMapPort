$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('debug-helper-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$temp/tools" -Force | Out-Null
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../deploy-to-spt.ps1') -Raw
# Substitute only the known-build fingerprint so fixtures need no proprietary/compiled DLL.
$fixture = [Text.Encoding]::UTF8.GetBytes('synthetic accepted debug helper')
$fixtureHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fixture))
$source = $source.Replace('DF0F5B7D578EDC63899CA53AD24D1E78589034A973770645CA8675ECB413C301', $fixtureHash)
Set-Content -LiteralPath "$temp/tools/deploy-to-spt.ps1" -Value $source
$live = "$temp/live"
$plugins = "$live/BepInEx/plugins"
$targets = @("$plugins/ArenaMapDebug/maschine-ArenaMapDebug.dll", "$plugins/maschine-ArenaMapDebug.dll")
New-Item -ItemType Directory -Path "$plugins/ArenaMapDebug" -Force | Out-Null
foreach ($target in $targets) { [IO.File]::WriteAllBytes($target, $fixture) }
Set-Content -LiteralPath "$plugins/ArenaMapDebug/keep.txt" -Value 'unrelated content'
Set-Content -LiteralPath "$plugins/other.dll" -Value 'other plugin'
New-Item -ItemType Directory -Path "$live/BepInEx/config" -Force | Out-Null
Set-Content -LiteralPath "$live/BepInEx/config/debug.cfg" -Value 'saved setting'

function Run([bool]$Success, [string[]]$ExtraArgs) {
    $output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot $live -RemoveDebugHelper @ExtraArgs 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw "Unexpected cleanup outcome: $output" }
}
function Assert-Present {
    foreach ($target in $targets) { if (-not (Test-Path -LiteralPath $target)) { throw "Premature deletion: $target" } }
}
Run $true @()
Assert-Present
if (Test-Path -LiteralPath "$temp/work") { throw 'Dry run created backup files' }
foreach ($option in @('-DllOnly', '-RemoveLegacyEquatorDb', '-RemoveLegacyDynamicMaps')) {
    Run $false @('-Confirm', $option)
    Assert-Present
}
Run $false @('-Confirm','-DynamicMapsMap','Equator')
Run $false @('-Confirm','-DynamicMapsPackage','unused')
# A bad second candidate must prevent deletion of the valid first candidate.
Set-Content -LiteralPath $targets[1] -Value 'unexpected DLL'
Run $false @('-Confirm')
Assert-Present
if (Test-Path -LiteralPath "$temp/work") { throw 'Rejected plan created backups' }
[IO.File]::WriteAllBytes($targets[1], $fixture)

# A redirected plugin root must fail before deleting files in the real fixture.
New-Item -ItemType Directory -Path "$temp/redirect/BepInEx" -Force | Out-Null
New-Item -ItemType Junction -Path "$temp/redirect/BepInEx/plugins" -Target $plugins | Out-Null
$output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot "$temp/redirect" -RemoveDebugHelper -Confirm 2>&1
if ($LASTEXITCODE -eq 0) { throw 'Linked plugin root was accepted' }
Assert-Present
Remove-Item -LiteralPath "$temp/redirect/BepInEx/plugins" -Force

Run $true @('-Confirm')
foreach ($target in $targets) { if (Test-Path -LiteralPath $target) { throw "Helper remains: $target" } }
$backups = @(Get-ChildItem -LiteralPath "$temp/work/deployment-backups" -Recurse -File)
if ($backups.Count -ne 2) { throw 'Missing backup' }
foreach ($backup in $backups) { if ((Get-FileHash -LiteralPath $backup.FullName).Hash -ne $fixtureHash) { throw 'Bad backup hash' } }
if ((Get-Content -LiteralPath "$plugins/other.dll").Trim() -ne 'other plugin' -or
    (Get-Content -LiteralPath "$plugins/ArenaMapDebug/keep.txt").Trim() -ne 'unrelated content' -or
    (Get-Content -LiteralPath "$live/BepInEx/config/debug.cfg").Trim() -ne 'saved setting') { throw 'Unrelated file changed' }
Run $true @('-Confirm')
if (@(Get-ChildItem -LiteralPath "$temp/work/deployment-backups" -Recurse -File).Count -ne 2) { throw 'Repeated removal created new backup' }
$resolved = [IO.Path]::GetFullPath($temp)
if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture root' }
Remove-Item -LiteralPath $resolved -Recurse -Force
Write-Output 'DEBUG_HELPER_REMOVAL_TESTS_PASSED'
