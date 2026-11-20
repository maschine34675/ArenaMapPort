#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $repo 'ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj'
$noStage = Join-Path $PSScriptRoot 'DynamicMapsNoStage.targets'
$source = Get-Content (Join-Path $repo 'ArenaMapPort.Client/DynamicMapsOwnedCatalog.cs') -Raw
$root = Join-Path $repo 'work/dynamicmaps-checks/mutants'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$faults = @(
    @{Name='erase-foreign-maps'; Find='definitions.Remove(path);'; Replace='definitions.Clear();'; Fails='NativeScanRetainsVanillaAndAddons'},
    @{Name='skip-image-resolution'; Find='(layer, image) => _image.SetValue(layer, image)'; Replace='(layer, image) => { }'; Fails='NativeScanRetainsVanillaAndAddons'},
    @{Name='ignore-request-scope'; Find='if (request != "Maps") return;'; Replace='if (false) return;'; Fails='UnrelatedRequestsDoNotParseOrRefreshAnything'}
)
foreach ($fault in $faults) {
    if (($source.Split([string[]]@($fault.Find), [StringSplitOptions]::None).Count - 1) -ne 1) { throw "Mutant anchor is not unique: $($fault.Name)" }
    $folder = Join-Path $root $fault.Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder 'DynamicMapsOwnedCatalog.cs'
    $source.Replace($fault.Find, $fault.Replace) | Set-Content -LiteralPath $path
    $trx = Join-Path $folder 'result.trx'
    # Remove only this previous result: otherwise a compile failure could masquerade as an old assertion kill.
    if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx }
    & dotnet test $project -c Release --nologo -v quiet "-p:CustomAfterMicrosoftCommonTargets=$noStage" `
        "-p:DynamicMapsOwnedCatalogSource=$path" --filter 'FullyQualifiedName~DynamicMapsOwnedCatalogTests' `
        --logger 'trx;LogFileName=result.trx' --results-directory $folder *> (Join-Path $folder 'run.log')
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -or -not (Test-Path -LiteralPath $trx)) { throw "Mutant did not reach a failing test: $($fault.Name)" }
    [xml]$results = Get-Content -LiteralPath $trx -Raw
    $matched = @($results.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' -and $_.testName -like "*$($fault.Fails)*" })
    if ($matched.Count -eq 0) { throw "Mutant did not fail the intended assertion: $($fault.Name)" }
    Write-Output "Rejected source fault: $($fault.Name)"
}
# Restore the baseline test binary and prove the faults did not leak into production sources/build inputs.
& dotnet test $project -c Release --nologo -v quiet "-p:CustomAfterMicrosoftCommonTargets=$noStage" `
    --filter 'FullyQualifiedName~DynamicMapAssetsTests|FullyQualifiedName~DynamicMapsOwnedCatalogTests' *> (Join-Path $root 'restored-baseline.log')
if ($LASTEXITCODE -ne 0) { throw 'Production baseline failed after the source mutants.' }
Write-Output 'DYNAMICMAPS_SOURCE_MUTANTS_REJECTED'
