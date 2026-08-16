#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Inventory {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "FUNCTION INVENTORY: $Message" }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$inventoryPath = Join-Path $repoRoot 'doc\generated\workshop-function-inventory.json'
Assert-Inventory (Test-Path -LiteralPath $inventoryPath -PathType Leaf) 'generated inventory is missing'

$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
Assert-Inventory ([int]$inventory.schemaVersion -eq 1) 'schemaVersion must be 1'

$expectedModules = @(
    'Bannerlord.Harmony', 'Bannerlord.ButterLib', 'Bannerlord.UIExtenderEx',
    'Bannerlord.MBOptionScreen', 'RBM', 'ImprovedGarrisons', 'DismembermentPlus',
    'Fourberie', 'Bannerlord.Diplomacy', 'UnblockableThrust', 'PlayerSettlement',
    'RebellionsAndDemographics', 'Separatism'
)
$actualModules = @($inventory.modules.moduleId | Sort-Object -Unique)
Assert-Inventory (($actualModules -join '|') -ceq (($expectedModules | Sort-Object) -join '|')) 'module coverage is not the exact approved 12-surface set'
Assert-Inventory ([int]$inventory.summary.moduleCount -eq 13) 'summary module count is wrong'
Assert-Inventory ([int]$inventory.summary.assemblyCount -eq @($inventory.assemblies).Count) 'summary assembly count is wrong'
Assert-Inventory ([int]$inventory.summary.methodCount -eq (@($inventory.assemblies.methods).Count)) 'summary method count is wrong'

foreach ($assembly in @($inventory.assemblies)) {
    Assert-Inventory ([string]$assembly.sha256 -match '^[0-9a-f]{64}$') "$($assembly.moduleId)/$($assembly.relativePath) has no exact SHA-256"
    Assert-Inventory (-not [IO.Path]::IsPathRooted([string]$assembly.relativePath)) "$($assembly.moduleId) leaks a machine-local absolute path"
    Assert-Inventory (@($assembly.methods).Count -gt 0) "$($assembly.moduleId)/$($assembly.relativePath) has no method surface"
    $tokens = @($assembly.methods.metadataToken)
    Assert-Inventory (@($tokens | Sort-Object -Unique).Count -eq $tokens.Count) "$($assembly.moduleId)/$($assembly.relativePath) has duplicate method tokens"
    foreach ($method in @($assembly.methods)) {
        Assert-Inventory ($null -ne $method.PSObject.Properties['authorityEvidence']) "$($assembly.moduleId)/$($method.metadataToken) has no authority evidence"
        foreach ($property in @('directSignals', 'transitiveSignals', 'calledMembers')) {
            Assert-Inventory ($null -ne $method.authorityEvidence.PSObject.Properties[$property]) "$($assembly.moduleId)/$($method.metadataToken) has no $property evidence"
            [string[]]$values = @($method.authorityEvidence.$property)
            $unique = New-Object Collections.Generic.HashSet[string] ([StringComparer]::Ordinal)
            foreach ($value in $values) {
                Assert-Inventory ($unique.Add($value)) "$($assembly.moduleId)/$($method.metadataToken) has duplicate $property evidence"
            }
            [string[]]$ordinal = @($values)
            [Array]::Sort($ordinal, [StringComparer]::Ordinal)
            Assert-Inventory (($values -join '|') -ceq ($ordinal -join '|')) "$($assembly.moduleId)/$($method.metadataToken) has nondeterministic $property evidence"
        }
    }
}

$separatism = @($inventory.assemblies | Where-Object { [string]$_.moduleId -ceq 'Separatism' })
Assert-Inventory ($separatism.Count -eq 1) 'Separatism must have one integrated adapter surface'
Assert-Inventory (@($separatism[0].methods).Count -gt 0) 'Separatism method surface is empty'
Assert-Inventory (@($separatism[0].methods | Where-Object {
    -not ([string]$_.declaringType).StartsWith('GameInterface.Services.Separatism', [StringComparison]::Ordinal)
}).Count -eq 0) 'Separatism inventory contains unrelated GameInterface methods'

Write-Host ("PASS: {0} modules, {1} runtime assemblies, {2} methods, exact hashes and complete Separatism filter" -f `
    $inventory.summary.moduleCount, $inventory.summary.assemblyCount, $inventory.summary.methodCount)
