[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$policyPath = Join-Path $repoRoot 'UnityProject/Assets/ArenaMapPort/Editor/Import/NormalTextureImportPolicy.cs'
Add-Type -TypeDefinition ([IO.File]::ReadAllText($policyPath))

function New-Usage([string]$shader = 'p0/Reflective/Bumped Specular', [string]$property = '_BumpMap', [bool]$dxt1 = $false) {
    $usage = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Usage]::new()
    $usage.Add($shader, $property, $dxt1)
    return $usage
}
function New-Pixels([object[]]$rgba) {
    $pixels = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Pixels]::new()
    for ($i = 0; $i -lt $rgba.Length; $i += 4) {
        $pixels.Add([byte]$rgba[$i], [byte]$rgba[$i+1], [byte]$rgba[$i+2], [byte]$rgba[$i+3])
    }
    return $pixels
}
function Classify([string]$format, [string]$space, $pixels, $usage, [string]$dimension = 'Tex2D', [int]$width = 1, [int]$height = 1) {
    return [ArenaMapPort.Editor.Import.NormalTextureImportPolicy]::Classify('fixture', $format, $space,
        $dimension, $width, $height, $pixels, $usage)
}
$script:checks = 0
function Assert-Plan($plan, [string]$encoding, [string]$compression, [bool]$srgb) {
    if ($plan.Encoding -ne $encoding -or $plan.Compression -ne $compression -or $plan.Srgb -ne $srgb) {
        throw "Incorrect preservation plan: $($plan.Encoding)/$($plan.Compression)/sRGB=$($plan.Srgb)"
    }
    $script:checks++
}
function Assert-Rejected([scriptblock]$action) {
    $rejected = $false
    try { & $action | Out-Null }
    catch {
        $errorObject = $_.Exception
        while ($null -ne $errorObject.InnerException) { $errorObject = $errorObject.InnerException }
        if ($errorObject -isnot [IO.InvalidDataException] -or $errorObject.Message -notlike '*rejected:*') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'Unsupported normal source was accepted.' }
    $script:checks++
}

$normal = New-Usage
# The actual cardboard source sample, versus the observed importer-corrupted sample.
$box = New-Pixels @(255,124,126,121)
Assert-Plan (Classify 'DXT5' 'Linear' $box $normal) 'PackedRaG' 'DXT5' $false
Assert-Rejected { Classify 'DXT5' 'Linear' (New-Pixels @(255,123,123,255)) $normal }
Assert-Plan (Classify 'Format29' 'Linear' $box $normal) 'PackedRaG' 'DXT5' $false
Assert-Plan (Classify 'DXT5Crunched' 'Linear' $box $normal) 'PackedRaG' 'DXT5' $false
Assert-Plan (Classify 'DXT5' 'Linear' (New-Pixels @(255,128,128,128)) $normal) 'PackedRaG' 'DXT5' $false
# BC7 retains R*A and G; compression perturbation of nominally white R must not be discarded.
$bc7 = New-Pixels @(239,100,109,120,255,128,128,128)
Assert-Plan (Classify 'BC7' 'Linear' $bc7 $normal 'Tex2D' 2 1) 'PackedRaG' 'BC7' $false
Assert-Rejected { Classify 'BC7' 'Linear' (New-Pixels @(238,128,128,120,255,128,128,128)) $normal 'Tex2D' 2 1 }
Assert-Rejected { Classify 'BC7' 'Linear' (New-Pixels @(255,100,117,120)) $normal }
Assert-Rejected { Classify 'BC5' 'Linear' $box $normal }
Assert-Rejected { Classify 'Unknown(77)' 'Linear' $box $normal }
Assert-Rejected { Classify 'DXT5' 'sRGB' $box $normal }
Assert-Rejected { Classify 'DXT5' 'Unknown' $box $normal }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $normal 'Cube' }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $normal 'Tex2D' 2 1 }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $normal 'Tex2D' 0 1 }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $normal 'Tex2D' 16385 1 }
Assert-Rejected { Classify 'DXT5' 'Linear' (New-Pixels @(128,128,255,255)) $normal }
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' '_BumpMap' $true) }

$rgb = New-Pixels @(128,128,255,255)
$dxt1 = New-Usage 'p0/Reflective/Bumped Specular' '_BumpMap' $true
Assert-Plan (Classify 'DXT1' 'Linear' $rgb $dxt1) 'RgbNormal' 'DXT1' $false
Assert-Rejected { Classify 'DXT1' 'Linear' $rgb $normal }
Assert-Rejected { Classify 'DXT1' 'Linear' (New-Pixels @(128,128,127,255)) $dxt1 }
Assert-Rejected { Classify 'DXT1' 'Linear' (New-Pixels @(128,128,255,254)) $dxt1 }
# Source-authored Cloth exceptions: RGB normal keeps sRGB; _NormalsMask is not a vector map.
$cloth = New-Usage 'Cloth/ClothShader' '_NormalMap1'
Assert-Plan (Classify 'DXT1' 'sRGB' $rgb $cloth) 'RgbNormal' 'DXT1' $true
$mask = New-Usage 'Cloth/ClothShader' '_NormalsMask'
Assert-Plan (Classify 'DXT5' 'sRGB' (New-Pixels @(5,8,5,11)) $mask) 'ClothNormalsMask' 'DXT5' $true
Assert-Rejected { Classify 'DXT5' 'Linear' $box $mask }
$mixed = New-Usage
$mixed.Add('Cloth/ClothShader', '_NormalsMask', $false)
Assert-Rejected { Classify 'DXT5' 'Linear' $box $mixed }
$mixedRgb = New-Usage 'p0/Reflective/Bumped Specular' '_BumpMap' $true
$mixedRgb.Add('p0/Reflective/Bumped Specular', '_BumpMap', $false)
Assert-Rejected { Classify 'DXT1' 'Linear' $rgb $mixedRgb }
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' '_MainTex') }
Assert-Rejected { Classify 'DXT5' 'Linear' $box ([ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Usage]::new()) }

# All currently used concrete normal properties qualify independently of texture names.
foreach ($slot in @('_BumpMap','_BumpMap0','_BumpMap1','_BumpMap2','_NormalMap1','_NormalMap2')) {
    Assert-Plan (Classify 'DXT5' 'Linear' $box (New-Usage 'verified source shader' $slot)) 'PackedRaG' 'DXT5' $false
}
# The source evidence is read-only and repeated classification never changes/swizzles pixels.
Assert-Plan (Classify 'DXT5' 'Linear' $box $normal) 'PackedRaG' 'DXT5' $false
if ($box.Count -ne 1 -or $box.MinR -ne 255 -or $box.MinA -ne 121) { throw 'Source evidence mutated.' }
$script:checks++

# Regression tripwire for the exact destructive import path, not a mathematical mirror.
$driver = [IO.File]::ReadAllText((Join-Path $repoRoot 'UnityProject/Assets/ArenaMapPort/Editor/Import/TextureImportDriver.cs'))
if ($driver.Contains('TextureImporterType.NormalMap')) { throw 'Already-encoded source entered Unity normal-map repacking again.' }
$script:checks++
Write-Output "NORMAL_TEXTURE_IMPORT_POLICY_PASSED: $script:checks checks (production C# policy, original-alpha failure control, keyword routes and source preservation)."
