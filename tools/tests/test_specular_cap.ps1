[CmdletBinding()]
param(
    # Exposed for the no-match negative control; normal gate runs include material and donor lifecycle tests.
    [string]$Filter = 'FullyQualifiedName~SpecularCapTests|FullyQualifiedName~DonorCameraSubscriptionsTests'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repoRoot
try {
    # Tests compile the production policy, repair, and scene gate with a test-only Unity facade.
    # The server's standard Release target stages only inside this repo's dist/.
    # Use one fixed ignored result file. Remove only that file before running so an absent/new-empty
    # test run cannot inherit a successful report from a previous invocation.
    $resultsDir = Join-Path $repoRoot 'work/tests/specular-cap'
    $trxPath = Join-Path $resultsDir 'specular-cap.trx'
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    if (Test-Path -LiteralPath $trxPath) { Remove-Item -LiteralPath $trxPath }
    & dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release --filter $Filter --nologo `
        --results-directory $resultsDir --logger 'trx;LogFileName=specular-cap.trx'
    if ($LASTEXITCODE -ne 0) { throw "Specular cap regression failed ($LASTEXITCODE)." }
    if (!(Test-Path -LiteralPath $trxPath)) { throw 'Specular cap test run produced no TRX report.' }
    [xml]$report = Get-Content -LiteralPath $trxPath -Raw
    $counters = $report.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) { throw 'Specular cap TRX report has no test counters.' }
    $total = [int]$counters.GetAttribute('total')
    $executed = [int]$counters.GetAttribute('executed')
    $passed = [int]$counters.GetAttribute('passed')
    $failed = [int]$counters.GetAttribute('failed')
    if ($total -le 0 -or $executed -ne $total -or $passed -ne $executed -or $failed -ne 0) {
        throw "Specular cap tests did not all execute successfully: total=$total executed=$executed passed=$passed failed=$failed."
    }
    Write-Output "Specular cap TRX verified: total=$total executed=$executed passed=$passed failed=$failed."
    & dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "Release client build failed ($LASTEXITCODE)." }
    Write-Output 'SPECULAR_CAP_CHECKS_PASSED'
}
finally { Pop-Location }
