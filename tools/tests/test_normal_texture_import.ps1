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
# Bowl's City_Signs_Road_Metal_01_N: one BC7 pixel with a G/B difference of 18 (mean 0.00) is still the packed layout;
# 25 and more is not (an RGB normal differs by 100 and more).
Assert-Plan (Classify 'BC7' 'Linear' (New-Pixels @(251,100,118,120,255,128,128,128)) $normal 'Tex2D' 2 1) 'PackedRaG' 'BC7' $false
Assert-Rejected { Classify 'BC7' 'Linear' (New-Pixels @(251,100,125,120,255,128,128,128)) $normal 'Tex2D' 2 1 }
Assert-Rejected { Classify 'BC7' 'Linear' (New-Pixels @(255,100,126,120)) $normal }  # G/B 26: beyond the BC7 outlier bound of 24
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
# Bowl (2026-10-18): City_Antisniper_tarps binds the same RGB normal (City_Tarp_01_N) on the BACKFACE cloth variant's
# _NormalMap2; both cloth variants read their normal maps as RGB and carry the same _NormalsMask.
$clothBack = New-Usage 'Cloth/ClothShader_backface' '_NormalMap2'
Assert-Plan (Classify 'DXT1' 'sRGB' $rgb $clothBack) 'RgbNormal' 'DXT1' $true
Assert-Plan (Classify 'DXT5' 'sRGB' (New-Pixels @(5,8,5,11)) (New-Usage 'Cloth/ClothShader_backface' '_NormalsMask')) 'ClothNormalsMask' 'DXT5' $true
Assert-Rejected { Classify 'DXT5' 'sRGB' $box (New-Usage 'Cloth/ClothShader_backface' '_NormalMap2') }
$mask = New-Usage 'Cloth/ClothShader' '_NormalsMask'
Assert-Plan (Classify 'DXT5' 'sRGB' (New-Pixels @(5,8,5,11)) $mask) 'ClothNormalsMask' 'DXT5' $true
Assert-Rejected { Classify 'DXT5' 'Linear' $box $mask }
# AirPit's Lighthouse_tarp_02 / mask2: the same Cloth _NormalsMask authored as an opaque DXT1 sRGB grey mask.
Assert-Plan (Classify 'DXT1' 'sRGB' (New-Pixels @(144,143,144,255,0,0,0,255)) $mask 'Tex2D' 2 1) 'ClothNormalsMask' 'DXT1' $true
Assert-Rejected { Classify 'DXT1' 'sRGB' (New-Pixels @(144,143,144,254)) $mask }
Assert-Rejected { Classify 'DXT1' 'Linear' (New-Pixels @(144,143,144,255)) $mask }
Assert-Rejected { Classify 'DXT1' 'sRGB' (New-Pixels @(128,128,255,255)) $mask }
$mixed = New-Usage
$mixed.Add('Cloth/ClothShader', '_NormalsMask', $false)
Assert-Rejected { Classify 'DXT5' 'Linear' $box $mixed }
$mixedRgb = New-Usage 'p0/Reflective/Bumped Specular' '_BumpMap' $true
$mixedRgb.Add('p0/Reflective/Bumped Specular', '_BumpMap', $false)
Assert-Rejected { Classify 'DXT1' 'Linear' $rgb $mixedRgb }
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' '_MainTex') }
Assert-Rejected { Classify 'DXT5' 'Linear' $box ([ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Usage]::new()) }

# AirPit rock shader normal slots and the SMap _SpecMap co-usage (Passenger_ladder_02_part_02).
foreach ($slot in @('_BaseNormalMap','_TopNormalMap','_DetailNormalMap')) {
    Assert-Plan (Classify 'DXT5' 'Linear' $box (New-Usage 'ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)' $slot)) 'PackedRaG' 'DXT5' $false
    Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' $slot) }
}
# Iceberg's MK4 rock shaders: _Normalmap on both variants, _DetailBump and _MaskBump on MK4/Rock only.
foreach ($case in @(@('MK4/Rock','_Normalmap'), @('MK4/Rock Simple','_Normalmap'), @('MK4/Rock','_DetailBump'), @('MK4/Rock','_MaskBump'))) {
    Assert-Plan (Classify 'DXT5' 'Linear' $box (New-Usage $case[0] $case[1])) 'PackedRaG' 'DXT5' $false
}
foreach ($case in @(@('MK4/Rock Simple','_DetailBump'), @('MK4/Rock Simple','_MaskBump'), @('p0/Reflective/Bumped Specular','_Normalmap'), @('MK4/Rock','_MaskRGBA'))) {
    Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage $case[0] $case[1]) }
}
$specCo = New-Usage 'p0/Reflective/Bumped Specular SMap' '_BumpMap'
$specCo.Add('p0/Reflective/Bumped Specular SMap', '_SpecMap', $false)
Assert-Plan (Classify 'DXT5' 'Linear' $box $specCo) 'PackedRaG' 'DXT5' $false
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular SMap' '_SpecMap') }
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' '_SpecMap') }

# Yard (2026-11-21): the Amplify bark shader's _Normal is an ordinary packed normal slot, on that shader only.
Assert-Plan (Classify 'DXT5' 'Linear' $box (New-Usage 'Impostors/Examples/StandardCrossfade' '_Normal')) 'PackedRaG' 'DXT5' $false
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'p0/Reflective/Bumped Specular' '_Normal') }
Assert-Rejected { Classify 'DXT5' 'Linear' $box (New-Usage 'Impostors/Examples/StandardCrossfade' '_DetailNormal') }
# Yard's impostor atlas (Hidden/Amplify Impostors/Octahedron Impostor, _Normals): a vector in RGB and the depth in alpha,
# kept as linear DXT5 with all four channels. Not on another shader, not sRGB, not another format, not opaque, not a packed
# normal (R = 255 everywhere), and never together with a normal reader or a cloth mask.
$atlasUsage = New-Usage 'Hidden/Amplify Impostors/Octahedron Impostor' '_Normals'
$atlas = New-Pixels @(54,48,62,143,0,0,0,0)
Assert-Plan (Classify 'DXT5' 'Linear' $atlas $atlasUsage 'Tex2D' 2 1) 'ImpostorNormalDepth' 'DXT5' $false
Assert-Rejected { Classify 'DXT5' 'sRGB' $atlas $atlasUsage 'Tex2D' 2 1 }
Assert-Rejected { Classify 'BC7' 'Linear' $atlas $atlasUsage 'Tex2D' 2 1 }
Assert-Rejected { Classify 'DXT1' 'Linear' $atlas $atlasUsage 'Tex2D' 2 1 }
Assert-Rejected { Classify 'DXT5' 'Linear' (New-Pixels @(54,48,62,255)) $atlasUsage }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $atlasUsage }
Assert-Rejected { Classify 'DXT5' 'Linear' $atlas (New-Usage 'p0/Reflective/Bumped Specular' '_Normals') 'Tex2D' 2 1 }
$atlasMixed = New-Usage 'Hidden/Amplify Impostors/Octahedron Impostor' '_Normals'
$atlasMixed.Add('p0/Reflective/Bumped Specular', '_BumpMap', $false)
Assert-Rejected { Classify 'DXT5' 'Linear' $atlas $atlasMixed 'Tex2D' 2 1 }
Assert-Rejected { Classify 'DXT5' 'Linear' $box $atlasMixed }
# The two pixel bounds of the atlas, at their edges: 254 passes for red and for alpha, 255 does not.
Assert-Plan (Classify 'DXT5' 'Linear' (New-Pixels @(254,48,62,254)) $atlasUsage) 'ImpostorNormalDepth' 'DXT5' $false
Assert-Rejected { Classify 'DXT5' 'Linear' (New-Pixels @(255,48,62,254)) $atlasUsage }
Assert-Rejected { Classify 'DXT5' 'Linear' (New-Pixels @(254,48,62,255)) $atlasUsage }
# Only plain DXT5 was measured for an atlas; the crunched spellings stay refused until one is seen.
Assert-Rejected { Classify 'DXT5Crunched' 'Linear' $atlas $atlasUsage 'Tex2D' 2 1 }
Assert-Rejected { Classify 'Format29' 'Linear' $atlas $atlasUsage 'Tex2D' 2 1 }
$atlasUnsupported = New-Usage 'Hidden/Amplify Impostors/Octahedron Impostor' '_Normals'
$atlasUnsupported.Add('Hidden/Amplify Impostors/Octahedron Impostor', '_MainTex', $false)
Assert-Rejected { Classify 'DXT5' 'Linear' $atlas $atlasUnsupported 'Tex2D' 2 1 }
# The bark slot is a packed-normal reader: an RGB normal on it is refused (no DXT1_NM keyword, not a cloth slot).
Assert-Rejected { Classify 'DXT1' 'sRGB' $rgb (New-Usage 'Impostors/Examples/StandardCrossfade' '_Normal') }
$atlasCloth = New-Usage 'Hidden/Amplify Impostors/Octahedron Impostor' '_Normals'
$atlasCloth.Add('Cloth/ClothShader', '_NormalsMask', $false)
Assert-Rejected { Classify 'DXT5' 'sRGB' (New-Pixels @(5,8,5,11)) $atlasCloth }
# Yard's Cloth_for_tree_N: an RGB normal with 0.039 percent of its pixels below blue 128 (deep folds). The bound is one
# pixel in a thousand: 1 of 1000 passes, 2 of 1000 and 1 of 999 do not, and without such a pixel it is the old rule.
function New-RgbPixels([int]$count, [int]$low) {
    $pixels = [ArenaMapPort.Editor.Import.NormalTextureImportPolicy+Pixels]::new()
    for ($i = 0; $i -lt $count; $i++) {
        if ($i -lt $low) { $pixels.Add(8, 174, 107, 255) } else { $pixels.Add(123, 125, 255, 255) }
    }
    return $pixels
}
$clothBack1 = New-Usage 'Cloth/ClothShader_backface' '_NormalMap1'
Assert-Plan (Classify 'DXT1' 'sRGB' (New-RgbPixels 1000 1) $clothBack1 'Tex2D' 1000 1) 'RgbNormal' 'DXT1' $true
Assert-Plan (Classify 'DXT1' 'sRGB' (New-RgbPixels 1000 0) $clothBack1 'Tex2D' 1000 1) 'RgbNormal' 'DXT1' $true
Assert-Rejected { Classify 'DXT1' 'sRGB' (New-RgbPixels 1000 2) $clothBack1 'Tex2D' 1000 1 }
Assert-Rejected { Classify 'DXT1' 'sRGB' (New-RgbPixels 999 1) $clothBack1 'Tex2D' 999 1 }
Assert-Plan (Classify 'DXT1' 'Linear' (New-RgbPixels 1000 1) $dxt1 'Tex2D' 1000 1) 'RgbNormal' 'DXT1' $false
Assert-Rejected { Classify 'DXT1' 'Linear' (New-RgbPixels 1000 1) $normal 'Tex2D' 1000 1 }
$low = New-RgbPixels 1000 1
if ($low.BelowHalfBlue -ne 1 -or $low.MinB -ne 107 -or $low.Count -ne 1000) { throw 'Blue statistics are wrong.' }
$script:checks++

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
