#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DynamicMaps121,
    [Parameter(Mandatory)][string]$DynamicMaps130,
    [string]$CecilPath = 'D:\SPT41\BepInEx\core\Mono.Cecil.dll'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$noStage = Join-Path $PSScriptRoot 'tests/DynamicMapsNoStage.targets'
$results = Join-Path $repo 'work/dynamicmaps-checks/baseline'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$trx = Join-Path $results 'result.trx'
if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx }
& dotnet test (Join-Path $repo 'ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj') -c Release --nologo -v quiet `
    "-p:CustomAfterMicrosoftCommonTargets=$noStage" --filter 'FullyQualifiedName~DynamicMapAssetsTests|FullyQualifiedName~DynamicMapsOwnedCatalogTests' `
    --logger 'trx;LogFileName=result.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'DynamicMaps production-linked tests failed.' }
[xml]$run = Get-Content -LiteralPath $trx -Raw
foreach ($group in @('DynamicMapAssetsTests','DynamicMapsOwnedCatalogTests')) {
    $cases = @($run.TestRun.Results.UnitTestResult | Where-Object testName -Like "*$group.*")
    if ($cases.Count -eq 0 -or @($cases | Where-Object outcome -ne 'Passed').Count -ne 0) { throw "Missing or incomplete test group: $group" }
}
& (Join-Path $PSScriptRoot 'tests/test_dynamicmaps_contracts.ps1') -DynamicMaps121 $DynamicMaps121 -DynamicMaps130 $DynamicMaps130 -CecilPath $CecilPath
& (Join-Path $PSScriptRoot 'tests/test_dynamicmaps_mutants.ps1')
& dotnet build (Join-Path $repo 'ArenaMapPort.Client/ArenaMapPort.Client.csproj') -c Release --nologo -v quiet `
    -p:ArenaMapPortDllOnly=true "-p:CustomAfterMicrosoftCommonTargets=$noStage"
if ($LASTEXITCODE -ne 0) { throw 'DynamicMaps client Release build failed.' }
Write-Output 'DYNAMICMAPS_MAPPORT_VERIFIED'
