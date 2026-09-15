<#
.SYNOPSIS
  Pass/fail verdict for the Unity batchmode log of a BuildBundles.Run process. Dot-sourced by
  tools/build_all.ps1 (step 2); also usable on its own to re-check an existing log.

.DESCRIPTION
  A BuildBundles.Run process only counts as successful when ALL of these hold:
    1. Unity.exe exited 0. BuildBundles.cs calls EditorApplication.Exit(1) on every failure path
       (and Unity itself exits non-zero on an executeMethod abort), so this is the primary signal.
    2. The log contains the `[ArenaMapPort][BuildBundles] DONE` marker, which BuildBundles.cs only
       writes after BuildAssetBundles and VerifyBuild.Run have both finished.
    3. No line anywhere matches `error CS`, `Shader error`, `Aborting batchmode` or `Received signal` (compile
       errors, executeMethod aborts, native crashes). This rule has no exceptions.
    4. No line matches `Exception`, UNLESS it belongs to a Microsoft.Extensions.Logging console
       entry (a `<level>: <Category>[<EventId>]` header plus its 6-space-indented continuation
       lines) at level trce/dbug/info/warn whose category starts with an entry of
       $UnityLogBenignLoggerCategories.

  Rule 4's allowlist covers Unity's own IL post-processing host, an ASP.NET Core server on a named
  pipe. It intermittently logs a closed-pipe race while the editor starts up, before any
  ArenaMapPort code runs:
      warn: Unity.AspNetCore.NamedPipeSupport.NamedPipeServer[0]
            Exception occured while accepting client connection: System.IO.IOException: ...
  That one line failed the 2026-09-14 20:35 full build even though Unity exited 0 and logged DONE
  (docs/progress/fix-enabled-state.md section 4). The allowlist matches the logger category, not a
  log position: an exception from any other source still fails the build, including one logged
  during editor startup (e.g. the GameRefs FileNotFoundException in an early S1 run), and a
  fail/crit entry from the allowlisted category does too.

.EXAMPLE
  . tools/unity-log-scan.ps1
  Test-UnityBuildLog -LogFile work/build/full/unity_build_all_20260914_203554.log -ExitCode 0
#>

$UnityLogFatalPattern = "error CS|Shader error|Aborting batchmode|Received signal"
$UnityLogExceptionPattern = "Exception"
$UnityLogBenignLoggerCategories = @("Unity.AspNetCore.NamedPipeSupport.")
$BuildBundlesDoneMarker = "[ArenaMapPort][BuildBundles] DONE"

function Test-UnityBuildLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LogFile,
        # $null when the exit code could not be read -- treated as a failure, never as success.
        [AllowNull()][Nullable[int]]$ExitCode
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $fatalLines = [System.Collections.Generic.List[object]]::new()
    $ignoredLines = [System.Collections.Generic.List[object]]::new()
    $doneLine = $null

    if ($null -eq $ExitCode) {
        $problems.Add("Unity exit code unavailable")
    } elseif ($ExitCode -ne 0) {
        $problems.Add("Unity exited $ExitCode")
    }

    if (-not (Test-Path -LiteralPath $LogFile -PathType Leaf)) {
        $problems.Add("log file not found: $LogFile")
    } else {
        # Category of the allowlisted logger entry the current line belongs to, or $null.
        $benignCategory = $null
        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $LogFile).ProviderPath)) {
            $lineNo++
            if ($line -match '^(trce|dbug|info|warn|fail|crit): (\S+)\[\d+\]\s*$') {
                $benignCategory = $null
                if ($Matches[1] -in @("trce", "dbug", "info", "warn")) {
                    $category = $Matches[2]
                    foreach ($prefix in $UnityLogBenignLoggerCategories) {
                        if ($category.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $benignCategory = $category }
                    }
                }
            } elseif ($line -notmatch '^ {6}') {
                $benignCategory = $null
            }

            if ($null -eq $doneLine -and $line.StartsWith($BuildBundlesDoneMarker, [System.StringComparison]::Ordinal)) {
                $doneLine = $lineNo
            }

            if ($line -match $UnityLogFatalPattern) {
                $fatalLines.Add([pscustomobject]@{ Line = $lineNo; Text = $line.Trim() })
            } elseif ($line -match $UnityLogExceptionPattern) {
                if ($benignCategory) {
                    $ignoredLines.Add([pscustomobject]@{ Line = $lineNo; Category = $benignCategory; Text = $line.Trim() })
                } else {
                    $fatalLines.Add([pscustomobject]@{ Line = $lineNo; Text = $line.Trim() })
                }
            }
        }

        if ($null -eq $doneLine) { $problems.Add("no '$BuildBundlesDoneMarker' marker") }
        if ($fatalLines.Count -gt 0) { $problems.Add("$($fatalLines.Count) fatal log line(s)") }
    }

    [pscustomobject]@{
        Ok           = ($problems.Count -eq 0)
        Problems     = $problems.ToArray()
        ExitCode     = $ExitCode
        DoneLine     = $doneLine
        FatalLines   = $fatalLines.ToArray()
        IgnoredLines = $ignoredLines.ToArray()
    }
}
