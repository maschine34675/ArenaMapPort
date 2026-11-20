#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $repo 'ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj'
$noStage = Join-Path $PSScriptRoot 'DynamicMapsNoStage.targets'
$source = Get-Content (Join-Path $repo 'ArenaMapPort.Client/DynamicMapsOwnedCatalog.cs') -Raw
$integration = Get-Content (Join-Path $repo 'ArenaMapPort.Client/DynamicMapsIntegration.cs') -Raw
$root = Join-Path $repo 'work/dynamicmaps-checks/mutants'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$faults = @(
    @{Name='erase-foreign-maps'; Find='definitions.Remove(path);'; Replace='definitions.Clear();'; Fails='NativeScanRetainsVanillaAndAddons'},
    @{Name='skip-image-resolution'; Find='(layer, image) => _image.SetValue(layer, image)'; Replace='(layer, image) => { }'; Fails='NativeScanRetainsVanillaAndAddons'},
    @{Name='ignore-request-scope'; Find='OwnsMaps = request == "Maps"'; Replace='OwnsMaps = true'; Fails='UnrelatedRequestsDoNotParseOrRefreshAnything'},
    @{Name='lose-definition-identity'; Find='if (cached == null || !cached.Matches(definition, image))'; Replace='if (true)'; Fails='UnchangedOwnedDefinitionRetainsViewportAndFloor'},
    @{Name='register-before-owned-merge'; Find='var definitions = (IDictionary)_definitions.GetValue(dropdown);'; Replace='Preferences.Invoke(dropdown, null); var definitions = (IDictionary)_definitions.GetValue(dropdown);'; Fails='OwnedPreferenceSurvivesFirstNativeRebind'},
    @{Name='leak-scan-scope'; Find='if (scan.Parent == null) _scans.Remove(scan.Dropdown);'; Replace='if (scan.Parent == null) { }'; Fails='NestedOtherScansAndExceptionalNativeRegistration'},
    @{Name='install-late-preference-hook'; Integration=$true; Find='harmony.Patch(_catalog.Preferences, prefix:'; Replace='harmony.Patch(_catalog.Preferences, postfix:'; Fails='InstalledPreferencePrefixRunsBeforeTheFirstNativeRebind'},
    @{Name='omit-installed-finalizer'; Integration=$true; Find='finalizer: new HarmonyMethod(typeof(DynamicMapsIntegration), nameof(CatalogScanFinalizer))'; Replace='finalizer: null'; Fails='InstalledFinalizerClearsTheScopeOnNativeThrowAndSkippedOriginal'}
)
foreach ($fault in $faults) {
    $mutantSource = if ($fault.Integration) { $integration } else { $source }
    $property = if ($fault.Integration) { 'DynamicMapsIntegrationSource' } else { 'DynamicMapsOwnedCatalogSource' }
    $filter = if ($fault.Integration) { 'FullyQualifiedName~DynamicMapsIntegrationTests' } else { 'FullyQualifiedName~DynamicMapsOwnedCatalogTests' }
    if (($mutantSource.Split([string[]]@($fault.Find), [StringSplitOptions]::None).Count - 1) -ne 1) { throw "Mutant anchor is not unique: $($fault.Name)" }
    $folder = Join-Path $root $fault.Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder 'DynamicMapsOwnedCatalog.cs'
    $mutantSource.Replace($fault.Find, $fault.Replace) | Set-Content -LiteralPath $path
    $trx = Join-Path $folder 'result.trx'
    # Remove only this previous result: otherwise a compile failure could masquerade as an old assertion kill.
    if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx }
    & dotnet test $project -c Release --nologo -v quiet "-p:CustomAfterMicrosoftCommonTargets=$noStage" `
        "-p:${property}=$path" --filter $filter `
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
    --filter 'FullyQualifiedName~DynamicMapAssetsTests|FullyQualifiedName~DynamicMapsOwnedCatalogTests|FullyQualifiedName~DynamicMapsIntegrationTests' *> (Join-Path $root 'restored-baseline.log')
if ($LASTEXITCODE -ne 0) { throw 'Production baseline failed after the source mutants.' }
Write-Output 'DYNAMICMAPS_SOURCE_MUTANTS_REJECTED'
