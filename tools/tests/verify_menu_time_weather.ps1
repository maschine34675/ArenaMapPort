param([ValidateSet('Tests', 'Client')][string]$Check = 'Tests')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repo
try {
    if ($Check -eq 'Tests') {
        & dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Map time/weather test suite failed.' }
        Write-Output 'MAP_TIME_WEATHER_TESTS_PASSED'
    } else {
        & dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Map time/weather client build failed.' }
        Write-Output 'MAP_TIME_WEATHER_CLIENT_PASSED'
    }
} finally { Pop-Location }
