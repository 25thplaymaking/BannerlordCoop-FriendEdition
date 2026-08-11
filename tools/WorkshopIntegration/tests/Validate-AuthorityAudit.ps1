#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$Release,
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..\..'),
    [string]$AuditPath = 'doc\generated\workshop-authority-audit.json',
    [string]$PolicyPath = 'tools\WorkshopIntegration\authority-dispositions.json',
    [string]$InventoryPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:DefaultAllowedDispositions = @(
    'ClientPresentation',
    'PurePolicy',
    'ServerCallback',
    'ServerCommand',
    'ReplicatedCosmetic',
    'CoopOwnerReplacement',
    'FrameworkLifecycle',
    'Retired'
)
$script:ForbiddenDispositions = @(
    '',
    'Unclassified',
    'Blocked',
    'Unsupported',
    'GuardedFeatureBlocked',
    'NotAllowed'
)

function Test-AuthorityAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Records,
        [string[]]$AllowedDispositions = $script:DefaultAllowedDispositions,
        [switch]$Release
    )

    $issues = New-Object System.Collections.Generic.List[string]
    $classified = 0
    $unclassified = 0
    $blocked = 0
    $inactive = 0
    $required = 0

    foreach ($record in $Records) {
        if (-not [bool]$record.active) {
            $inactive++
            continue
        }
        if (-not [bool]$record.requiresDisposition) { continue }

        $required++
        $disposition = [string]$record.disposition
        $label = if ($record.PSObject.Properties['moduleId']) {
            '{0}/{1}' -f [string]$record.moduleId, [string]$record.metadataToken
        }
        else {
            "record[$required]"
        }

        if ($disposition -in $script:ForbiddenDispositions) {
            if ($disposition -in @('Blocked', 'Unsupported', 'GuardedFeatureBlocked', 'NotAllowed')) {
                $blocked++
                $issues.Add("$label uses forbidden disposition '$disposition'")
            }
            else {
                $unclassified++
                $issues.Add("$label is unclassified")
            }
            continue
        }

        if ($disposition -notin $AllowedDispositions) {
            $unclassified++
            $issues.Add("$label uses unknown disposition '$disposition'")
            continue
        }

        $classified++
        if ([string]::IsNullOrWhiteSpace([string]$record.owner)) {
            $issues.Add("$label has no Coop owner")
        }
        if (@($record.tests | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
            $issues.Add("$label has no route test")
        }
    }

    $result = [pscustomobject]@{
        Records = $Records.Count
        Required = $required
        Classified = $classified
        Unclassified = $unclassified
        Blocked = $blocked
        Inactive = $inactive
        Issues = $issues.ToArray()
    }

    if ($Release -and $issues.Count -gt 0) {
        throw "AUTHORITY AUDIT RELEASE FAILED: $($issues.Count) issue(s). $($issues -join '; ')"
    }

    return $result
}

function Assert-SelfTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "AUTHORITY AUDIT SELF-TEST: $Message" }
}

function Invoke-SelfTest {
    $valid = [pscustomobject]@{
        active = $true
        requiresDisposition = $true
        disposition = 'ServerCommand'
        owner = 'Example.Handler'
        tests = @('ExampleTests.Routes')
    }
    $unclassified = [pscustomobject]@{
        active = $true
        requiresDisposition = $true
        disposition = 'Unclassified'
        owner = ''
        tests = @()
    }
    $blocked = [pscustomobject]@{
        active = $true
        requiresDisposition = $true
        disposition = 'Blocked'
        owner = 'Example.Guard'
        tests = @('ExampleTests.Blocks')
    }

    $validDevelopment = Test-AuthorityAudit -Records @($valid)
    $unclassifiedDevelopment = Test-AuthorityAudit -Records @($unclassified)
    $blockedDevelopment = Test-AuthorityAudit -Records @($blocked)
    Assert-SelfTest ($validDevelopment.Issues.Count -eq 0 -and $validDevelopment.Classified -eq 1) 'valid route was rejected'
    Assert-SelfTest ($unclassifiedDevelopment.Unclassified -eq 1 -and $unclassifiedDevelopment.Issues.Count -eq 1) 'development mode hid an unclassified route'
    Assert-SelfTest ($blockedDevelopment.Blocked -eq 1 -and $blockedDevelopment.Issues.Count -eq 1) 'development mode hid a blocked route'

    Test-AuthorityAudit -Records @($valid) -Release | Out-Null
    foreach ($invalid in @($unclassified, $blocked)) {
        $threw = $false
        try { Test-AuthorityAudit -Records @($invalid) -Release | Out-Null }
        catch { $threw = $true }
        Assert-SelfTest $threw "release mode accepted '$($invalid.disposition)'"
    }

    Write-Host 'PASS: development authority audit reports unclassified and blocked routes'
    Write-Host 'PASS: release authority audit rejects unclassified and blocked routes'
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

$repo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedAudit = if ([IO.Path]::IsPathRooted($AuditPath)) {
    [IO.Path]::GetFullPath($AuditPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repo $AuditPath))
}
$resolvedPolicy = if ([IO.Path]::IsPathRooted($PolicyPath)) {
    [IO.Path]::GetFullPath($PolicyPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repo $PolicyPath))
}
if (-not (Test-Path -LiteralPath $resolvedAudit -PathType Leaf)) { throw "AUTHORITY AUDIT: generated audit is missing: $resolvedAudit" }
if (-not (Test-Path -LiteralPath $resolvedPolicy -PathType Leaf)) { throw "AUTHORITY AUDIT: disposition policy is missing: $resolvedPolicy" }

$audit = Get-Content -LiteralPath $resolvedAudit -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath $resolvedPolicy -Raw | ConvertFrom-Json
if ([int]$audit.schemaVersion -ne 1) { throw 'AUTHORITY AUDIT: schemaVersion must be 1' }
if ([int]$policy.schemaVersion -ne 1) { throw 'AUTHORITY AUDIT: policy schemaVersion must be 1' }

$inventoryInput = if ([string]::IsNullOrWhiteSpace($InventoryPath)) { [string]$audit.inventoryPath } else { $InventoryPath }
$resolvedInventory = if ([IO.Path]::IsPathRooted($inventoryInput)) {
    [IO.Path]::GetFullPath($inventoryInput)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repo $inventoryInput))
}
if (-not (Test-Path -LiteralPath $resolvedInventory -PathType Leaf)) { throw "AUTHORITY AUDIT: source inventory is missing: $resolvedInventory" }
$inventory = Get-Content -LiteralPath $resolvedInventory -Raw | ConvertFrom-Json
if ([int]$inventory.schemaVersion -ne 1) { throw 'AUTHORITY AUDIT: inventory schemaVersion must be 1' }
if ([string]$audit.generatedFromCommit -cne [string]$inventory.generatedFromCommit) { throw 'AUTHORITY AUDIT: source commit does not match the inventory' }

$records = @($audit.records)
$keys = @($records | ForEach-Object { '{0}|{1}|{2}' -f [string]$_.moduleId, [string]$_.assemblySha256, [string]$_.metadataToken })
if (@($keys | Sort-Object -Unique).Count -ne $keys.Count) { throw 'AUTHORITY AUDIT: duplicate exact method keys' }
foreach ($record in $records) {
    if ([string]$record.assemblySha256 -notmatch '^[0-9a-f]{64}$') { throw "AUTHORITY AUDIT: invalid SHA-256 for $($record.moduleId)/$($record.metadataToken)" }
    if ([string]$record.metadataToken -notmatch '^0x06[0-9A-F]{6}$') { throw "AUTHORITY AUDIT: invalid method token for $($record.moduleId)/$($record.metadataToken)" }
}

$expectedByKey = @{}
foreach ($assembly in @($inventory.assemblies)) {
    foreach ($method in @($assembly.methods)) {
        $key = '{0}|{1}|{2}' -f [string]$assembly.moduleId, [string]$assembly.sha256, [string]$method.metadataToken
        $expectedByKey.Add($key, [pscustomobject]@{ DeclaringType = [string]$method.declaringType; Method = [string]$method.name })
    }
}
if ($records.Count -ne $expectedByKey.Count) {
    throw "AUTHORITY AUDIT: records do not reconcile with inventory (audit=$($records.Count), inventory=$($expectedByKey.Count))"
}
foreach ($record in $records) {
    $key = '{0}|{1}|{2}' -f [string]$record.moduleId, [string]$record.assemblySha256, [string]$record.metadataToken
    if (-not $expectedByKey.ContainsKey($key)) { throw "AUTHORITY AUDIT: record does not reconcile with inventory: $key" }
    $expected = $expectedByKey[$key]
    if ([string]$record.declaringType -cne $expected.DeclaringType -or [string]$record.method -cne $expected.Method) {
        throw "AUTHORITY AUDIT: method identity does not reconcile with inventory: $key"
    }
}

$result = Test-AuthorityAudit -Records $records -AllowedDispositions @($policy.allowedDispositions) -Release:$Release
Write-Host ("PASS: records={0} required={1} classified={2} unclassified={3} blocked={4} inactive={5} issues={6}" -f `
    $result.Records, $result.Required, $result.Classified, $result.Unclassified, $result.Blocked, $result.Inactive, $result.Issues.Count)
exit 0
