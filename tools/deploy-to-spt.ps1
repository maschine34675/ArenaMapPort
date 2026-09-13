<#
.SYNOPSIS
  Promotes D:/ArenaMapPort/dist/ into a live SPT install (docs/SPEC.md decision #9 / §G.S4's own
  "a separate, explicit, user-approved step" — this script IS that step, not something any build
  target or CI-style pipeline runs automatically).

.DESCRIPTION
  Copies dist/BepInEx/plugins/ArenaMapPort/**      -> <SptRoot>/BepInEx/plugins/ArenaMapPort/**
        dist/SPT_Runtime/user/mods/ArenaMapPort/** -> <SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/**

  SAFETY (task-mandated, not optional):
    - DEFAULT behaviour is a DRY RUN. It prints the exact copy plan (source -> destination, size,
      whether the destination already exists, whether its content already matches by SHA256) and
      copies NOTHING. This is true with zero arguments and with only -SptRoot given.
    - A real copy only happens when -Confirm is passed explicitly.
    - This script NEVER deletes anything, in either mode, anywhere -- not just "outside its own
      ArenaMapPort folders": it performs Copy-Item only (create/overwrite), never Remove-Item. This
      trivially satisfies "never deletes anything outside its own ArenaMapPort folders" by deleting
      nothing at all, which is the safer of the two ways to satisfy that constraint (a copy-only
      script cannot accidentally widen its own blast radius later). Every destination path is also
      asserted, before any write, to fall strictly under
      <SptRoot>/BepInEx/plugins/ArenaMapPort/ or <SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/
      (Resolve-Path-based prefix check, not a string-concat assumption) -- the script refuses to run
      at all if that invariant doesn't hold for every planned file, in EITHER mode.
    - Never reads or writes anything under D:/ArenaMapPort/dist/ other than read-only enumeration
      (source of truth, untouched).

.PARAMETER SptRoot
  Root of the live SPT install to promote into. Default D:/SPT41.

.PARAMETER Confirm
  Perform the real copy. Without this switch the script only prints the plan and exits 0.

.EXAMPLE
  # dry run (default) -- prints the plan, copies nothing:
  pwsh tools/deploy-to-spt.ps1

.EXAMPLE
  # real promotion, after reviewing the dry-run plan and getting the user's explicit go-ahead:
  pwsh tools/deploy-to-spt.ps1 -Confirm
#>
[CmdletBinding()]
param(
    [string]$SptRoot = "D:/SPT41",
    [switch]$Confirm
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==== [deploy-to-spt] $msg ====" -ForegroundColor Cyan
}

function Fail($msg) {
    Write-Host "[deploy-to-spt] FATAL: $msg" -ForegroundColor Red
    exit 1
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$DistRoot = Join-Path $RepoRoot "dist"

if (-not (Test-Path $DistRoot)) {
    Fail "$DistRoot does not exist -- build the client+server Release first (see docs/progress/dist-manifest.md)"
}

# The two ArenaMapPort mod folders this script is scoped to -- everything it ever reads is under
# the first path of each pair, everything it could ever write is under the second.
$Mappings = @(
    [PSCustomObject]@{
        Label   = "BepInEx client plugin"
        SrcRoot = Join-Path $DistRoot "BepInEx/plugins/ArenaMapPort"
        DstRoot = Join-Path $SptRoot "BepInEx/plugins/ArenaMapPort"
    },
    [PSCustomObject]@{
        Label   = "SPT server mod"
        SrcRoot = Join-Path $DistRoot "SPT_Runtime/user/mods/ArenaMapPort"
        DstRoot = Join-Path $SptRoot "SPT_Runtime/user/mods/ArenaMapPort"
    }
)

foreach ($m in $Mappings) {
    if (-not (Test-Path $m.SrcRoot)) {
        Fail "$($m.SrcRoot) does not exist -- dist/ is incomplete (expected $($m.Label) content here)"
    }
}

# ---------------------------------------------------------------------------
# Build the copy plan: every file under each SrcRoot, its destination, size, and whether the
# destination already exists with identical content (SHA256) -- informational in both modes.
# ---------------------------------------------------------------------------
Write-Step "Building copy plan (SptRoot = $SptRoot)"

$plan = @()
foreach ($m in $Mappings) {
    $srcFiles = Get-ChildItem -Path $m.SrcRoot -Recurse -File
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($m.SrcRoot.Length).TrimStart('\', '/')
        $dst = Join-Path $m.DstRoot $rel

        # Safety invariant, checked per-file, in BOTH modes: the destination must fall strictly
        # under <SptRoot>/.../ArenaMapPort/ -- never anywhere else in the SPT install. Uses a real
        # path join + prefix check (not string concatenation trust) so a malformed -SptRoot or a
        # future mapping typo cannot silently point outside the mod folder.
        $dstFull = [System.IO.Path]::GetFullPath((Join-Path $m.DstRoot $rel))
        $dstRootFull = [System.IO.Path]::GetFullPath($m.DstRoot)
        if (-not $dstFull.StartsWith($dstRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail "safety invariant violated: planned destination '$dstFull' escapes '$dstRootFull' -- refusing to run"
        }

        $existed = Test-Path $dst
        $same = $false
        if ($existed) {
            $existingHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash
            $newHash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
            $same = ($existingHash -eq $newHash)
        }

        $plan += [PSCustomObject]@{
            Label      = $m.Label
            Source     = $f.FullName
            Destination = $dstFull
            SizeBytes  = $f.Length
            DestExists = $existed
            DestSame   = $same
        }
    }
}

if ($plan.Count -eq 0) {
    Fail "copy plan is empty -- dist/ has no files under either ArenaMapPort folder"
}

foreach ($m in $Mappings) {
    Write-Host ""
    Write-Host "-- $($m.Label): $($m.SrcRoot)" -ForegroundColor Yellow
    Write-Host "   -> $($m.DstRoot)" -ForegroundColor Yellow
    foreach ($row in ($plan | Where-Object { $_.Label -eq $m.Label })) {
        $status = if (-not $row.DestExists) { "NEW" } elseif ($row.DestSame) { "unchanged (same SHA256)" } else { "WILL OVERWRITE (differs)" }
        $sizeKb = [Math]::Round($row.SizeBytes / 1KB, 1)
        Write-Host ("   {0,-45} {1,10} KB   [{2}]" -f (Split-Path $row.Destination -Leaf), $sizeKb, $status)
    }
}

$total = ($plan | Measure-Object -Property SizeBytes -Sum).Sum
$totalMb = [Math]::Round($total / 1MB, 1)
$newCount = ($plan | Where-Object { -not $_.DestExists }).Count
$overwriteCount = ($plan | Where-Object { $_.DestExists -and -not $_.DestSame }).Count
$unchangedCount = ($plan | Where-Object { $_.DestExists -and $_.DestSame }).Count

Write-Host ""
Write-Host "[deploy-to-spt] plan: $($plan.Count) file(s), $totalMb MB total -- $newCount new, $overwriteCount to overwrite, $unchangedCount already identical"

if (-not $Confirm) {
    Write-Host ""
    Write-Host "[deploy-to-spt] DRY RUN (default) -- no files were copied, nothing under $SptRoot was touched." -ForegroundColor Green
    Write-Host "[deploy-to-spt] Re-run with -Confirm to actually copy the plan above." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Real copy. Copy-Item only -- this script never calls Remove-Item, anywhere, in either mode.
# ---------------------------------------------------------------------------
Write-Step "Copying (real run, -Confirm was passed)"

foreach ($row in $plan) {
    $dstDir = Split-Path $row.Destination -Parent
    if (-not (Test-Path $dstDir)) {
        New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    }
    Copy-Item -Path $row.Source -Destination $row.Destination -Force
    Write-Host "[deploy-to-spt] copied $(Split-Path $row.Destination -Leaf) -> $($row.Destination)"
}

Write-Host ""
Write-Host "[deploy-to-spt] OK -- $($plan.Count) file(s) copied into $SptRoot." -ForegroundColor Green
exit 0
