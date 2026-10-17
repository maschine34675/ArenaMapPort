$ErrorActionPreference = 'Stop'
# -RemoveLegacyShaderSidecar: Equator's shader sidecar was renamed from material-shaders.json to
# material-shaders.arena_equator.json (2026-10-17). The migration removes only the old file, only after the renamed
# file and the client DLL match dist, only when the old file really is a shader sidecar, and never through a link.
$source = Join-Path $PSScriptRoot '../deploy-to-spt.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('shader-sidecar-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$temp/tools" -Force | Out-Null
Copy-Item -LiteralPath $source -Destination "$temp/tools/deploy-to-spt.ps1"
$plugin = 'BepInEx/plugins/maschine-ArenaMapPort'
$live = "$temp/live/$plugin"
$dist = "$temp/dist/$plugin"
$sidecar = '{"002875081e1cfcb6":{"shader":"Decal/DeferredDecalDiffuseNormalsDynamicEditor","renderQueue":2000,"keywords":[]}}'
foreach ($root in @($live, $dist)) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Set-Content -LiteralPath "$root/material-shaders.arena_equator.json" -Value $sidecar
    Set-Content -LiteralPath "$root/maschine-ArenaMapPort.Client.dll" -Value 'matching DLL fixture'
}
Set-Content -LiteralPath "$live/material-shaders.json" -Value $sidecar
Set-Content -LiteralPath "$live/material-shaders.arena_bay5.json" -Value 'preserve'
function Run([bool]$Success, [string[]]$ExtraArgs) {
    $output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot "$temp/live" -RemoveLegacyShaderSidecar @ExtraArgs 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw "Unexpected migration outcome: $output" }
}
Run $true @()
if (-not (Test-Path -LiteralPath "$live/material-shaders.json") -or (Test-Path -LiteralPath "$temp/work")) { throw 'Dry run changed files' }
# The client DLL that still reads the old name blocks the removal.
Set-Content -LiteralPath "$live/maschine-ArenaMapPort.Client.dll" -Value 'stale'
Run $false @('-Confirm')
Copy-Item -LiteralPath "$dist/maschine-ArenaMapPort.Client.dll" -Destination "$live/maschine-ArenaMapPort.Client.dll" -Force
# A renamed sidecar that differs from dist blocks it as well.
Set-Content -LiteralPath "$live/material-shaders.arena_equator.json" -Value '{}'
Run $false @('-Confirm')
Set-Content -LiteralPath "$live/material-shaders.arena_equator.json" -Value $sidecar
# Anything else under the legacy name stays.
foreach ($foreign in @('{"note":"not a sidecar"}', '[1,2]', '{}')) {
    Set-Content -LiteralPath "$live/material-shaders.json" -Value $foreign
    Run $false @('-Confirm')
    if (-not (Test-Path -LiteralPath "$live/material-shaders.json")) { throw "Foreign file deleted: $foreign" }
}
Set-Content -LiteralPath "$live/material-shaders.json" -Value $sidecar
Run $false @('-Confirm','-DllOnly')
Run $false @('-Confirm','-RemoveLegacyEquatorDb')
Run $false @('-Confirm','-RemoveLegacyDynamicMaps')
Run $false @('-Confirm','-DynamicMapsMap','Equator')
Run $false @('-Confirm','-DynamicMapsPackage','unused')
if (Test-Path -LiteralPath "$temp/work") { throw 'Rejected runs created backups' }
Run $true @('-Confirm')
if (Test-Path -LiteralPath "$live/material-shaders.json") { throw 'Legacy file remains' }
$backups = @(Get-ChildItem -LiteralPath "$temp/work/deployment-backups" -Recurse -File)
if ($backups.Count -ne 1 -or (Get-Content -LiteralPath $backups[0].FullName -Raw).Trim() -ne $sidecar) { throw 'Original backup missing' }
if ((Get-Content -LiteralPath "$live/material-shaders.arena_bay5.json").Trim() -ne 'preserve') { throw 'Unrelated sidecar changed' }
if ((Get-Content -LiteralPath "$live/material-shaders.arena_equator.json").Trim() -ne $sidecar) { throw 'Renamed sidecar changed' }
Run $true @('-Confirm')
# An ancestor redirected outside the owned plugin folder must fail before any mutation.
$redirect = "$temp/redirect"
New-Item -ItemType Directory -Path "$redirect/BepInEx/plugins" -Force | Out-Null
New-Item -ItemType Junction -Path "$redirect/$plugin" -Target $live | Out-Null
Set-Content -LiteralPath "$live/material-shaders.json" -Value $sidecar
$output = & pwsh -NoProfile -File "$temp/tools/deploy-to-spt.ps1" -SptRoot $redirect -RemoveLegacyShaderSidecar -Confirm 2>&1
if ($LASTEXITCODE -eq 0 -or -not (Test-Path -LiteralPath "$live/material-shaders.json")) { throw 'Linked plugin root was accepted' }
# Remove the junction itself only; no recursive deletion follows a link.
Remove-Item -LiteralPath "$redirect/$plugin" -Force
$resolved = [IO.Path]::GetFullPath($temp)
if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture root' }
Remove-Item -LiteralPath $resolved -Recurse -Force
Write-Output 'SHADER_SIDECAR_DEPLOY_TESTS_PASSED'
