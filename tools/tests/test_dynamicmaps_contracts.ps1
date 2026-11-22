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
    # 2026-11-22 (CODE-1): the postfix target, and the screen's failed load is what calls it.
    Method $dropdown 'RemoveUnavailableMap' @('DynamicMaps.Data.MapDef') 'System.Void' $true
    $screen = $module.GetType('DynamicMaps.UI.ModdedMapScreen')
    $change = @($screen.Methods | Where-Object { $_.Name -eq 'ChangeMap' -and $_.HasBody })
    $removal = @($change.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RemoveUnavailableMap' -and
        $_.Operand.DeclaringType.FullName -eq 'DynamicMaps.UI.Controls.MapSelectDropdown'
    })
    Require ($change.Count -eq 1 -and $removal.Count -eq 1) 'ModdedMapScreen.ChangeMap calls RemoveUnavailableMap'
    $load = $dropdown.Methods | Where-Object Name -eq 'LoadMapDefsFromPath'
    $preferences = @($load.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RegisterMapPreferences' })
    $refresh = @($load.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ChangeAvailableMapDefs' })
    Require ($preferences.Count -eq 1 -and $refresh.Count -eq 1 -and $preferences[0].Offset -lt $refresh[0].Offset) 'preference registration before dropdown refresh'
    $writes = @($load.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -like 'System.Collections.Generic.Dictionary*' -and
        $_.Operand.Name -in @('set_Item','Remove') -and $_.Operand.DeclaringType.FullName -like '*DynamicMaps.Data.MapDef*'
    })
    Require ($writes.Count -gt 0 -and @($writes | Where-Object Offset -gt $preferences[0].Offset).Count -eq 0) 'catalogue mutations finish before preferences'
}

foreach ($item in @(@('1.2.1',$DynamicMaps121), @('1.3.0',$DynamicMaps130))) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $item[1]).Path)
    try {
        Check $assembly $item[0]
        Write-Output "DynamicMaps $($item[0]) actual DLL contract passed: $((Get-FileHash -LiteralPath $item[1]).Hash)"
        if ($item[0] -eq '1.3.0') {
            $load = $assembly.MainModule.GetType('DynamicMaps.UI.Controls.MapSelectDropdown').Methods | Where-Object Name -eq 'LoadMapDefsFromPath'
            $call = $load.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RegisterMapPreferences' }
            $operand = $call.Operand
            $opcode = $call.OpCode
            $call.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
            $call.Operand = $null
            $rejected = $false
            try { Check $assembly $item[0] } catch {
                if ($_.Exception.Message -notlike '*contract mismatch*preference registration*') { throw }
                $rejected = $true
            }
            Require $rejected 'missing preference hook negative control was accepted'
            $call.OpCode = $opcode
            $call.Operand = $operand
            # In-memory only: without RemoveUnavailableMap the same oracle must reject this DLL.
            $removal = $assembly.MainModule.GetType('DynamicMaps.UI.Controls.MapSelectDropdown').Methods | Where-Object Name -eq 'RemoveUnavailableMap'
            $removal.Name = 'RemovedUnavailableNegativeControl'
            $rejected = $false
            try { Check $assembly $item[0] } catch {
                if ($_.Exception.Message -notlike '*contract mismatch*RemoveUnavailableMap*') { throw }
                $rejected = $true
            }
            Require $rejected 'missing unavailable-map hook negative control was accepted'
            $removal.Name = 'RemoveUnavailableMap'
        }
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
