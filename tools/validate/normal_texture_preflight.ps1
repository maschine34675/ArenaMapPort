# PowerShell half of tools/validate/normal_texture_preflight.py.
#
# Why PowerShell: the verdict must come from the PRODUCTION policy (UnityProject/.../NormalTextureImportPolicy.cs),
# not from a Python copy that drifts. The file is Unity-free, so Add-Type compiles it exactly like
# tools/tests/test_normal_texture_import.ps1 does. This script feeds it the rows the Python half collected:
# usages go through the real Usage.Add, pixel statistics through reflection on the Pixels setters (found by name,
# never a hard-coded list), and the verdict is the real Classify (plan or the policy's own exception message).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputJson,
    [Parameter(Mandatory = $true)][string]$OutputJson,
    [string]$PolicyPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $PolicyPath) { $PolicyPath = Join-Path $repoRoot 'UnityProject/Assets/ArenaMapPort/Editor/Import/NormalTextureImportPolicy.cs' }
if (-not (Test-Path -LiteralPath $PolicyPath)) { throw "policy file not found: $PolicyPath" }
Add-Type -TypeDefinition ([IO.File]::ReadAllText($PolicyPath))

$policy = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy]
$usageType = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Usage]
$pixelsType = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Pixels]

# Every Pixels property with a setter (private ones included) must be fed by the Python half, or the statistic the
# policy reads would silently stay at its default. A missing one is a loud failure, never a guess.
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$settable = @($pixelsType.GetProperties($flags) | Where-Object { $_.GetSetMethod($true) -ne $null })

$rows = Get-Content -LiteralPath $InputJson -Raw -Encoding UTF8 | ConvertFrom-Json
$out = New-Object System.Collections.Generic.List[object]
foreach ($row in @($rows.textures)) {
    $result = [ordered]@{ id = $row.id; plan = $null; compression = $null; srgb = $null; message = $null }
    try {
        $usage = $null
        if ($row.usage -ne $null) {
            $usage = $usageType::new()
            foreach ($slot in @($row.usage)) { $usage.Add([string]$slot.shader, [string]$slot.property, [bool]$slot.dxt1) }
        }
        $pixels = $null
        if ($row.stats -ne $null) {
            $pixels = $pixelsType::new()
            foreach ($p in $settable) {
                $member = $row.stats.PSObject.Properties[$p.Name]
                if ($null -eq $member) { throw "Pixels.$($p.Name) has a setter but the Python half supplies no value for it; extend normal_texture_preflight.py" }
                $p.SetValue($pixels, [System.Convert]::ChangeType($member.Value, $p.PropertyType))
            }
        }
        $plan = $policy::Classify([string]$row.id, [string]$row.format_src, [string]$row.color_space, [string]$row.dimension,
            [int]$row.width, [int]$row.height, $pixels, $usage)
        $result.plan = $plan.Encoding
        $result.compression = $plan.Compression
        $result.srgb = $plan.Srgb
    }
    catch {
        $e = $_.Exception
        while ($null -ne $e.InnerException) { $e = $e.InnerException }
        if ($e -isnot [IO.InvalidDataException]) { throw $e }   # a bridge defect, not a policy verdict
        $result.message = $e.Message
    }
    $out.Add([pscustomobject]$result)
}
[IO.File]::WriteAllText($OutputJson, (ConvertTo-Json -InputObject $out.ToArray() -Depth 5), (New-Object Text.UTF8Encoding($false)))
