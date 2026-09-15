$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\unity-log-scan.ps1')
$casePath = [IO.Path]::GetTempFileName()
try {
    $done = '[ArenaMapPort][BuildBundles] DONE packageDir=test outputDir=test'
    $cases = @(
        @{ Name='clean'; Lines=@('ordinary progress', $done); Exit=0; Expected=$true },
        @{ Name='shader parse error despite DONE'; Lines=@("Shader error in '': Parse error at line 10", $done); Exit=0; Expected=$false },
        @{ Name='shader compile error'; Lines=@("Shader error in 'ArenaMapPort/Terrain': undeclared identifier", $done); Exit=0; Expected=$false },
        @{ Name='missing DONE'; Lines=@('ordinary progress'); Exit=0; Expected=$false },
        @{ Name='nonzero exit'; Lines=@($done); Exit=1; Expected=$false },
        @{ Name='unknown exception'; Lines=@('SomeSubsystem: Exception occurred', $done); Exit=0; Expected=$false },
        @{ Name='known benign pipe race'; Lines=@('warn: Unity.AspNetCore.NamedPipeSupport.NamedPipeServer[0]', '      Exception occured while accepting client connection: System.IO.IOException: closed pipe', $done); Exit=0; Expected=$true },
        @{ Name='fatal category cannot hide exception'; Lines=@('fail: Unity.AspNetCore.NamedPipeSupport.NamedPipeServer[0]', '      Exception occurred', $done); Exit=0; Expected=$false }
    )
    foreach ($case in $cases) {
        [IO.File]::WriteAllLines($casePath, [string[]]$case.Lines)
        $result = Test-UnityBuildLog -LogFile $casePath -ExitCode $case.Exit
        if ($result.Ok -ne $case.Expected) { throw "Unexpected log verdict: $($case.Name)" }
    }
    Write-Output "UNITY_LOG_TESTS_OK $($cases.Count) cases"
} finally {
    Remove-Item -LiteralPath $casePath -Force
}
