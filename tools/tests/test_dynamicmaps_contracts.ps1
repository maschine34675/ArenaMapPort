#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DynamicMaps121,
    [Parameter(Mandatory)][string]$DynamicMaps130,
    [string]$CecilPath = 'D:\SPT41\BepInEx\core\Mono.Cecil.dll'
)
$ErrorActionPreference = 'Stop'
Add-Type -LiteralPath $CecilPath

function Require($condition, [string]$message) {
    if (-not $condition) { throw "DynamicMaps contract mismatch: $message" }
}
function Method($type, [string]$name, [string[]]$arguments, [string]$returns, [bool]$public, [bool]$static = $false) {
    $found = @($type.Methods | Where-Object {
        $_.Name -eq $name -and $_.IsPublic -eq $public -and $_.IsStatic -eq $static -and
        ($_.Parameters.ParameterType.FullName -join ',') -eq ($arguments -join ',') -and $_.ReturnType.FullName -eq $returns
    })
    Require ($found.Count -eq 1) "$($type.FullName).$name"
}
function Property($type, [string]$name, [string]$returns, [bool]$writable = $false) {
    $found = @($type.Properties | Where-Object Name -eq $name)
    Require ($found.Count -eq 1 -and $found[0].PropertyType.FullName -eq $returns -and $found[0].GetMethod.IsPublic -and
        (-not $writable -or $found[0].SetMethod.IsPublic)) "$($type.FullName).$name"
}
function Check($assembly, [string]$version) {
    Require ($assembly.Name.Version.ToString() -eq "$version.0") "version $version"
    $module = $assembly.MainModule
    $dropdown = $module.GetType('DynamicMaps.UI.Controls.MapSelectDropdown')
    $definition = $module.GetType('DynamicMaps.Data.MapDef')
    $layer = $module.GetType('DynamicMaps.Data.MapLayerDef')
    Require ($null -ne $dropdown -and $null -ne $definition -and $null -ne $layer) 'native types'
    Method $dropdown 'LoadMapDefsFromPath' @('System.String') 'System.Void' $true
    Property $definition 'Layers' 'System.Collections.Generic.Dictionary`2<System.String,DynamicMaps.Data.MapLayerDef>'
    Property $layer 'ImagePath' 'System.String' $true
    if ($version -eq '1.2.1') {
        Method $definition 'LoadFromPath' @('System.String') 'DynamicMaps.Data.MapDef' $true $true
        return
    }
    Method $definition 'LoadFromPath' @('System.String','System.String') 'DynamicMaps.Data.MapDef' $true $true
    foreach ($entry in @(
        @('_mapDefs','System.Collections.Generic.Dictionary`2<System.String,DynamicMaps.Data.MapDef>'),
        @('_writeTimes','System.Collections.Generic.Dictionary`2<System.String,System.DateTime>')
    )) {
        $fields = @($dropdown.Fields | Where-Object { $_.Name -eq $entry[0] -and $_.FieldType.FullName -eq $entry[1] -and $_.IsPrivate -and -not $_.IsStatic })
        Require ($fields.Count -eq 1) $entry[0]
    }
    Property $definition 'Bounds' 'DynamicMaps.Data.BoundingRectangle'
    Property $definition 'MapInternalNames' 'System.Collections.Generic.List`1<System.String>'
    Property $definition 'DisplayName' 'System.String'
    Property $definition 'SelectionId' 'System.String' $true
    Property $definition 'IsAddon' 'System.Boolean' $true
    Property $layer 'ImageBounds' 'DynamicMaps.Data.BoundingRectangle'
    Property $layer 'GameBounds' 'System.Collections.Generic.List`1<DynamicMaps.Data.BoundingRectangularSolid>'
    Method $dropdown 'RegisterMapPreferences' @() 'System.Void' $false
    Method $dropdown 'FilteredMapDefs' @() 'System.Collections.Generic.IEnumerable`1<DynamicMaps.Data.MapDef>' $false
    Method $dropdown 'ChangeAvailableMapDefs' @('System.Collections.Generic.IEnumerable`1<DynamicMaps.Data.MapDef>') 'System.Void' $false
}

foreach ($item in @(@('1.2.1',$DynamicMaps121), @('1.3.0',$DynamicMaps130))) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $item[1]).Path)
    try {
        Check $assembly $item[0]
        Write-Output "DynamicMaps $($item[0]) actual DLL contract passed: $((Get-FileHash -LiteralPath $item[1]).Hash)"
        # In-memory only: removing the real loader must make the same contract oracle reject this DLL.
        $dropdown = $assembly.MainModule.GetType('DynamicMaps.UI.Controls.MapSelectDropdown')
        $method = $dropdown.Methods | Where-Object Name -eq 'LoadMapDefsFromPath'
        $method.Name = 'RemovedLoaderNegativeControl'
        $rejected = $false
        try { Check $assembly $item[0] } catch {
            if ($_.Exception.Message -notlike '*contract mismatch*LoadMapDefsFromPath*') { throw }
            $rejected = $true
        }
        Require $rejected 'missing-loader negative control was accepted'
    } finally { $assembly.Dispose() }
}
Write-Output 'DYNAMICMAPS_REAL_CONTRACTS_PASSED'
