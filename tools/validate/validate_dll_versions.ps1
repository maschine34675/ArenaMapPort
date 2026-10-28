param([string]$RepoRoot = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$versions = @()
foreach ($part in @('Client', 'Server')) {
    $project = Join-Path $RepoRoot "ArenaMapPort.$part/ArenaMapPort.$part.csproj"
    $xml = [xml](Get-Content -LiteralPath $project -Raw)
    $version = [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw "Invalid $part project version" }
    $versions += $version
    $sourcePath = if ($part -eq 'Client') { 'ArenaMapPort.Client/Plugin.cs' } else { 'ArenaMapPort.Server/ModMetadata.cs' }
    $pattern = if ($part -eq 'Client') { 'PluginVersion\s*=\s*"([^"]+)"' } else { '\bVersion\s*\{[^}]+\}\s*=\s*new\("([^"]+)"\)' }
    $declared = [regex]::Match((Get-Content -LiteralPath (Join-Path $RepoRoot $sourcePath) -Raw), $pattern).Groups[1].Value
    if ($declared -ne $version) { throw "$part source and project versions differ" }
    $relative = if ($part -eq 'Client') { 'dist/BepInEx/plugins/maschine-ArenaMapPort' } else { 'dist/SPT_Runtime/user/mods/ArenaMapPort' }
    $dll = Join-Path $RepoRoot "$relative/maschine-ArenaMapPort.$part.dll"
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    if ($info.FileVersion -ne "$version.0" -or
        ($info.ProductVersion -ne $version -and -not $info.ProductVersion.StartsWith($version + '+', [StringComparison]::Ordinal))) {
        throw "Stale $part DLL: expected $version, found $($info.FileVersion) / $($info.ProductVersion)"
    }
}
if ($versions[0] -ne $versions[1]) { throw 'Client and server versions differ' }
Write-Output "DLL_VERSIONS_OK $($versions[0]) client/server source, project and staged binaries"
