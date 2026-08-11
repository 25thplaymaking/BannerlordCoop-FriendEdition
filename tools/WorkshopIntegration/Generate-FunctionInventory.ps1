#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..'),
    [string]$OutputPath = 'doc\generated\workshop-function-inventory.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputPath))
}

$moduleMetadata = [ordered]@{
    'Bannerlord.Harmony' = [ordered]@{ category = 'framework'; deployment = 'active canonical patch provider' }
    'Bannerlord.ButterLib' = [ordered]@{ category = 'framework'; deployment = 'active client framework; exact-pinned dedicated runtime certified live' }
    'Bannerlord.UIExtenderEx' = [ordered]@{ category = 'framework'; deployment = 'active client presentation framework' }
    'Bannerlord.MBOptionScreen' = [ordered]@{ category = 'framework'; deployment = 'active client settings framework' }
    'RBM' = [ordered]@{ category = 'combat'; deployment = 'retired from the Friend Edition loadout' }
    'ImprovedGarrisons' = [ordered]@{ category = 'campaign'; deployment = 'active server-authoritative adapter' }
    'DismembermentPlus' = [ordered]@{ category = 'combat-presentation'; deployment = 'module active; execution fails closed in live Coop pending stable cosmetic event' }
    'Fourberie' = [ordered]@{ category = 'campaign-and-mission'; deployment = 'active guarded subset; unsafe creation routes fail closed' }
    'Bannerlord.Diplomacy' = [ordered]@{ category = 'campaign-and-ui'; deployment = 'active server-authoritative adapter' }
    'UnblockableThrust' = [ordered]@{ category = 'combat-rule'; deployment = 'active mission adapter' }
    'PlayerSettlement' = [ordered]@{ category = 'campaign-and-map'; deployment = 'loads; construction and rebuild flows fail closed' }
    'Separatism' = [ordered]@{ category = 'campaign'; deployment = 'integrated Friend Edition server-authoritative implementation' }
}

# Runtime-reachable, mod-owned assemblies for Bannerlord 1.4.7. Historical version-specific
# implementation DLLs and third-party dependencies remain hash-audited by the suite builder but are
# not mod-function surfaces because the 1.4.7 loaders cannot select them.
$specs = @(
    [ordered]@{ moduleId='Bannerlord.Harmony'; role='entrypoint'; path='mb2\Modules\Bannerlord.Harmony\bin\Win64_Shipping_Client\Bannerlord.Harmony.dll' },
    [ordered]@{ moduleId='Bannerlord.ButterLib'; role='entrypoint'; path='mb2\Modules\Bannerlord.ButterLib\bin\Win64_Shipping_Client\Bannerlord.ButterLib.dll' },
    [ordered]@{ moduleId='Bannerlord.ButterLib'; role='implementation-1.4.7'; path='mb2\Modules\Bannerlord.ButterLib\bin\Win64_Shipping_Client\Bannerlord.ButterLib.Implementation.1.4.7.dll' },
    [ordered]@{ moduleId='Bannerlord.UIExtenderEx'; role='entrypoint-and-implementation'; path='mb2\Modules\Bannerlord.UIExtenderEx\bin\Win64_Shipping_Client\Bannerlord.UIExtenderEx.dll' },
    [ordered]@{ moduleId='Bannerlord.MBOptionScreen'; role='api'; path='mb2\Modules\Bannerlord.MBOptionScreen\bin\Win64_Shipping_Client\MCMv5.dll' },
    [ordered]@{ moduleId='Bannerlord.MBOptionScreen'; role='ui-adapter'; path='mb2\Modules\Bannerlord.MBOptionScreen\bin\Win64_Shipping_Client\MCM.UI.Adapter.MCMv5.dll' },
    [ordered]@{ moduleId='Bannerlord.MBOptionScreen'; role='implementation-1.4.7'; path='mb2\Modules\Bannerlord.MBOptionScreen\bin\Win64_Shipping_Client\Bannerlord.MBOptionScreen.v1.4.7.dll' },
    [ordered]@{ moduleId='Bannerlord.MBOptionScreen'; role='loader'; path='mb2\Modules\Bannerlord.MBOptionScreen\bin\Win64_Shipping_Client\Bannerlord.ModuleLoader.Bannerlord.MBOptionScreen.dll' },
    [ordered]@{ moduleId='RBM'; role='entrypoint'; path='mb2\Modules\RBM\bin\Win64_Shipping_Client\RBM.dll' },
    [ordered]@{ moduleId='RBM'; role='ai'; path='mb2\Modules\RBM\bin\Win64_Shipping_Client\RBMAI.dll' },
    [ordered]@{ moduleId='RBM'; role='combat'; path='mb2\Modules\RBM\bin\Win64_Shipping_Client\RBMCombat.dll' },
    [ordered]@{ moduleId='RBM'; role='configuration'; path='mb2\Modules\RBM\bin\Win64_Shipping_Client\RBMConfig.dll' },
    [ordered]@{ moduleId='RBM'; role='tournament'; path='mb2\Modules\RBM\bin\Win64_Shipping_Client\RBMTournament.dll' },
    [ordered]@{ moduleId='ImprovedGarrisons'; role='entrypoint-and-implementation'; path='mb2\Modules\ImprovedGarrisons\bin\Win64_Shipping_Client\ImprovedGarrisons.dll' },
    [ordered]@{ moduleId='DismembermentPlus'; role='entrypoint-and-implementation'; path='mb2\Modules\DismembermentPlus\bin\Win64_Shipping_Client\DismembermentPlus.dll' },
    [ordered]@{ moduleId='Fourberie'; role='entrypoint-and-implementation'; path='mb2\Modules\Fourberie\bin\Win64_Shipping_Client\Fourberie.dll' },
    [ordered]@{ moduleId='Bannerlord.Diplomacy'; role='implementation-1.4.7'; path='mb2\Modules\Bannerlord.Diplomacy\bin\Win64_Shipping_Client\Bannerlord.Diplomacy.1.4.7.dll' },
    [ordered]@{ moduleId='Bannerlord.Diplomacy'; role='loader'; path='mb2\Modules\Bannerlord.Diplomacy\bin\Win64_Shipping_Client\Bannerlord.ModuleLoader.Bannerlord.Diplomacy.dll' },
    [ordered]@{ moduleId='UnblockableThrust'; role='entrypoint-and-implementation'; path='mb2\Modules\UnblockableThrust\bin\Win64_Shipping_Client\UnblockableThrust.dll' },
    [ordered]@{ moduleId='PlayerSettlement'; role='entrypoint-and-implementation'; path='mb2\Modules\PlayerSettlement\bin\Win64_Shipping_Client\PlayerSettlement.dll' },
    [ordered]@{ moduleId='PlayerSettlement'; role='fixes'; path='mb2\Modules\PlayerSettlement\bin\Win64_Shipping_Client\PlayerSettlementFixes.dll' },
    [ordered]@{ moduleId='Separatism'; role='integrated-source-filter'; path='source\GameInterface\bin\Release\netstandard2.0\GameInterface.dll' }
)

$requests = New-Object System.Collections.Generic.List[object]
foreach ($spec in $specs) {
    $fullPath = Join-Path $repo $spec.path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Function inventory input is missing: $($spec.path)"
    }
    $requests.Add([ordered]@{
        moduleId = [string]$spec.moduleId
        relativePath = ([string]$spec.path).Replace('\', '/')
        path = $fullPath
        included = $true
        platform = if ([string]$spec.moduleId -ceq 'Separatism') { 'netstandard2.0' } else { 'Win64_Shipping_Client' }
    })
}

Import-Module (Join-Path $PSScriptRoot 'WorkshopIntegration.psm1') -Force
$workshopModule = Get-Module WorkshopIntegration
$inspections = @(& $workshopModule {
    param($items)
    Invoke-AssemblyInspector -Files $items
} $requests.ToArray())

$assemblies = New-Object System.Collections.Generic.List[object]
foreach ($spec in $specs) {
    $relativePath = ([string]$spec.path).Replace('\', '/')
    $matches = @($inspections | Where-Object {
        [string]$_.moduleId -ceq [string]$spec.moduleId -and
        [string]$_.relativePath -ceq $relativePath
    })
    if ($matches.Count -ne 1 -or -not [bool]$matches[0].managed) {
        throw "Function inventory input is not one managed assembly: $relativePath"
    }

    $methods = @($matches[0].methods)
    if ([string]$spec.moduleId -ceq 'Separatism') {
        $methods = @($methods | Where-Object {
            ([string]$_.declaringType).StartsWith('GameInterface.Services.Separatism', [StringComparison]::Ordinal)
        })
    }
    if ($methods.Count -eq 0) { throw "No methods found for $relativePath" }

    $assemblies.Add([ordered]@{
        moduleId = [string]$spec.moduleId
        assemblyRole = [string]$spec.role
        relativePath = $relativePath
        sha256 = [string]$matches[0].sha256
        identity = $matches[0].identity
        methodCount = $methods.Count
        methods = $methods
    })
}

$modules = New-Object System.Collections.Generic.List[object]
foreach ($moduleId in $moduleMetadata.Keys) {
    $owned = @($assemblies | Where-Object { [string]$_.moduleId -ceq [string]$moduleId })
    $modules.Add([ordered]@{
        moduleId = [string]$moduleId
        category = [string]$moduleMetadata[$moduleId].category
        deployment = [string]$moduleMetadata[$moduleId].deployment
        assemblyCount = $owned.Count
        methodCount = [int](($owned | ForEach-Object { [int]$_['methodCount'] } | Measure-Object -Sum).Sum)
    })
}

$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Could not resolve source commit.' }
$totalMethods = [int](($assemblies | ForEach-Object { [int]$_['methodCount'] } | Measure-Object -Sum).Sum)
$inventory = [ordered]@{
    schemaVersion = 1
    generatedFromCommit = $commit
    scope = 'Runtime-reachable mod-owned Bannerlord 1.4.7 assemblies plus integrated GameInterface.Services.Separatism methods; third-party dependencies and dormant historical implementation DLLs are excluded.'
    summary = [ordered]@{
        moduleCount = $modules.Count
        assemblyCount = $assemblies.Count
        methodCount = $totalMethods
    }
    modules = $modules.ToArray()
    assemblies = $assemblies.ToArray()
}

$parent = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[IO.File]::WriteAllText(
    $resolvedOutput,
    ($inventory | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false))
)

Write-Host "WROTE $resolvedOutput"
Write-Host "MODULES=$($modules.Count) ASSEMBLIES=$($assemblies.Count) METHODS=$totalMethods COMMIT=$commit"
