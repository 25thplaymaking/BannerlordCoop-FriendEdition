#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$InventoryPath = 'doc\generated\workshop-function-inventory.json',
    [string]$PolicyPath = 'tools\WorkshopIntegration\authority-dispositions.json',
    [string]$OutputPath = 'doc\generated\workshop-authority-audit.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Join-Path $PSScriptRoot '..\..' }

function Resolve-RepoPath {
    param([string]$Repo, [string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $Repo $Path))
}

function Add-Evidence {
    param([Collections.Generic.List[string]]$Evidence, [string]$Value)
    if (-not [string]::IsNullOrWhiteSpace($Value) -and -not $Evidence.Contains($Value)) {
        $Evidence.Add($Value)
    }
}

$repo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedInventory = Resolve-RepoPath -Repo $repo -Path $InventoryPath
$resolvedPolicy = Resolve-RepoPath -Repo $repo -Path $PolicyPath
$resolvedOutput = Resolve-RepoPath -Repo $repo -Path $OutputPath
if (-not (Test-Path -LiteralPath $resolvedInventory -PathType Leaf)) { throw "Authority inventory is missing: $resolvedInventory" }
if (-not (Test-Path -LiteralPath $resolvedPolicy -PathType Leaf)) { throw "Authority policy is missing: $resolvedPolicy" }

$inventory = Get-Content -LiteralPath $resolvedInventory -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath $resolvedPolicy -Raw | ConvertFrom-Json
if ([int]$inventory.schemaVersion -ne 1) { throw 'Function inventory schemaVersion must be 1.' }
if ([int]$policy.schemaVersion -ne 1) { throw 'Authority policy schemaVersion must be 1.' }

$allowedDispositions = @($policy.allowedDispositions)
$ruleByMethod = @{}
foreach ($rule in @($policy.rules)) {
    if ([string]$rule.moduleId -eq '' -or [string]$rule.assemblySha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Authority policy rule must have a module ID and lowercase exact assembly SHA-256.'
    }
    if ([string]$rule.disposition -notin $allowedDispositions) {
        throw "Authority policy rule uses unknown disposition '$($rule.disposition)'."
    }
    foreach ($token in @($rule.metadataTokens)) {
        if ([string]$token -notmatch '^0x06[0-9A-F]{6}$') { throw "Authority policy rule has invalid method token '$token'." }
        $key = '{0}|{1}|{2}' -f [string]$rule.moduleId, [string]$rule.assemblySha256, [string]$token
        if ($ruleByMethod.ContainsKey($key)) { throw "Authority policy has duplicate method key '$key'." }
        $ruleByMethod.Add($key, $rule)
    }
}

$frameworkModules = @('Bannerlord.Harmony', 'Bannerlord.ButterLib', 'Bannerlord.UIExtenderEx', 'Bannerlord.MBOptionScreen')
$activeGameplayModules = @('ImprovedGarrisons', 'DismembermentPlus', 'Fourberie', 'Bannerlord.Diplomacy', 'UnblockableThrust', 'PlayerSettlement', 'Separatism')
$records = New-Object Collections.Generic.List[object]
$matchedRuleKeys = New-Object Collections.Generic.HashSet[string] ([StringComparer]::Ordinal)

foreach ($assembly in @($inventory.assemblies)) {
    $moduleId = [string]$assembly.moduleId
    $assemblyHash = [string]$assembly.sha256
    $active = $moduleId -ne 'RBM'
    foreach ($method in @($assembly.methods)) {
        $evidence = New-Object Collections.Generic.List[string]
        foreach ($signal in @($method.authorityEvidence.directSignals)) { Add-Evidence -Evidence $evidence -Value ([string]$signal) }
        foreach ($signal in @($method.authorityEvidence.transitiveSignals)) { Add-Evidence -Evidence $evidence -Value ([string]$signal) }

        $declaringType = [string]$method.declaringType
        $methodName = [string]$method.name
        if ($declaringType -match '(?i)(Harmony|Patch|Behavior|Behaviour|Model|CampaignEvent|Mission)') {
            Add-Evidence -Evidence $evidence -Value "type-role:$declaringType"
        }
        if ($methodName -ceq 'Execute' -or $methodName -ceq 'RegisterEvents' -or $methodName -ceq 'SyncData' -or
            $methodName.StartsWith('On', [StringComparison]::Ordinal)) {
            Add-Evidence -Evidence $evidence -Value "entrypoint:$methodName"
        }

        $requiresDisposition = $evidence.Count -gt 0
        $disposition = 'Unclassified'
        $owner = ''
        $capability = ''
        $tests = @()
        if ($moduleId -ceq 'RBM') {
            $disposition = 'Retired'
            $owner = 'FriendEditionWorkshopModuleCatalog'
            $tests = @('WorkshopIntegration.Run-Tests')
        }
        elseif ($moduleId -in $frameworkModules) {
            $disposition = 'FrameworkLifecycle'
            $owner = 'Native framework lifecycle'
            $tests = @('WorkshopIntegration.Run-Tests')
        }

        $key = '{0}|{1}|{2}' -f $moduleId, $assemblyHash, [string]$method.metadataToken
        if ($ruleByMethod.ContainsKey($key)) {
            $rule = $ruleByMethod[$key]
            $disposition = [string]$rule.disposition
            $owner = [string]$rule.owner
            $capability = [string]$rule.capability
            $tests = @($rule.tests)
            $matchedRuleKeys.Add($key) | Out-Null
        }

        $records.Add([ordered]@{
            moduleId = $moduleId
            assemblySha256 = $assemblyHash
            metadataToken = [string]$method.metadataToken
            declaringType = $declaringType
            method = $methodName
            active = $active
            requiresDisposition = $requiresDisposition
            evidence = $evidence.ToArray()
            disposition = $disposition
            owner = $owner
            capability = $capability
            tests = $tests
        })
    }
}

$unmatchedRules = @($ruleByMethod.Keys | Where-Object { -not $matchedRuleKeys.Contains([string]$_) })
if ($unmatchedRules.Count -gt 0) { throw "Authority policy has stale exact method key(s): $($unmatchedRules -join ', ')" }

$required = @($records | Where-Object { [bool]$_.active -and [bool]$_.requiresDisposition })
$classified = @($required | Where-Object { [string]$_.disposition -in $allowedDispositions })
$blockedNames = @('Blocked', 'Unsupported', 'GuardedFeatureBlocked', 'NotAllowed')
$audit = [ordered]@{
    schemaVersion = 1
    generatedFromCommit = [string]$inventory.generatedFromCommit
    inventoryPath = if ([IO.Path]::IsPathRooted($InventoryPath)) { [IO.Path]::GetFileName($InventoryPath) } else { $InventoryPath.Replace('\', '/') }
    summary = [ordered]@{
        recordCount = $records.Count
        requiredCount = $required.Count
        classifiedCount = $classified.Count
        unclassifiedCount = @($required | Where-Object { [string]$_.disposition -ceq 'Unclassified' }).Count
        blockedCount = @($required | Where-Object { [string]$_.disposition -in $blockedNames }).Count
        inactiveCount = @($records | Where-Object { -not [bool]$_.active }).Count
    }
    records = $records.ToArray()
}

$parent = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
[IO.File]::WriteAllText($resolvedOutput, ($audit | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
Write-Host ("WROTE {0}" -f $resolvedOutput)
Write-Host ("RECORDS={0} REQUIRED={1} CLASSIFIED={2} UNCLASSIFIED={3} BLOCKED={4} INACTIVE={5}" -f `
    $audit.summary.recordCount, $audit.summary.requiredCount, $audit.summary.classifiedCount, $audit.summary.unclassifiedCount, $audit.summary.blockedCount, $audit.summary.inactiveCount)
exit 0
