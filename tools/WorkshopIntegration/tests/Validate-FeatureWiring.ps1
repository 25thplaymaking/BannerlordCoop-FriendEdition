#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$Release,
    [string]$RepoRoot = '',
    [string]$LedgerPath = 'tools\WorkshopIntegration\workshop-feature-wiring.json',
    [string]$InputsPath = 'deploy\bannerlord-1.4.8-inputs.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Join-Path $PSScriptRoot '..\..\..' }

$repo = [IO.Path]::GetFullPath($RepoRoot)
function Resolve-RepoPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $path))
}

$ledgerFile = Resolve-RepoPath $LedgerPath
$inputsFile = Resolve-RepoPath $InputsPath
if (-not (Test-Path -LiteralPath $ledgerFile -PathType Leaf)) { throw "FEATURE WIRING: ledger is missing: $ledgerFile" }
if (-not (Test-Path -LiteralPath $inputsFile -PathType Leaf)) { throw "FEATURE WIRING: migration inputs are missing: $inputsFile" }

$ledger = Get-Content -LiteralPath $ledgerFile -Raw | ConvertFrom-Json
$inputs = Get-Content -LiteralPath $inputsFile -Raw | ConvertFrom-Json
if ([int]$ledger.schemaVersion -ne 1) { throw 'FEATURE WIRING: schemaVersion must be 1' }
if ([string]$ledger.targetGameVersion -cne [string]$inputs.targetGameVersion) {
    throw 'FEATURE WIRING: target game version does not match migration inputs'
}

$expectedModules = @($inputs.managedSuite.modules | ForEach-Object { [string]$_.moduleId } | Sort-Object)
$actualModules = @($ledger.modules | ForEach-Object { [string]$_.moduleId } | Sort-Object)
if (($expectedModules -join "`n") -cne ($actualModules -join "`n")) {
    throw "FEATURE WIRING: module inventory does not reconcile with the managed suite (expected=$($expectedModules -join ','), actual=$($actualModules -join ','))"
}

$evidence = @{}
foreach ($root in @((Join-Path $repo 'source'), (Join-Path $repo 'tools\WorkshopIntegration'))) {
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File |
                 Where-Object { $_.Extension -in @('.cs', '.ps1') }) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($content, '\b[A-Za-z_][A-Za-z0-9_]*\b')) {
            $evidence[$match.Value] = $true
        }
    }
}

$issues = [Collections.Generic.List[string]]::new()
$featureIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$releaseStatuses = @($ledger.allowedReleaseStatuses | ForEach-Object { [string]$_ })
$featureCount = 0
$releaseReady = 0

foreach ($module in @($ledger.modules)) {
    $moduleId = [string]$module.moduleId
    $inputModule = @($inputs.managedSuite.modules | Where-Object { [string]$_.moduleId -ceq $moduleId })
    if ($inputModule.Count -ne 1 -or [string]$module.version -cne [string]$inputModule[0].version) {
        $issues.Add("$moduleId version does not match the exact managed payload")
    }
    $families = @($module.featureFamilies)
    if ($families.Count -eq 0) { $issues.Add("$moduleId has no reviewed function families") }

    foreach ($feature in $families) {
        $featureCount++
        $id = "$moduleId/$([string]$feature.id)"
        if (-not $featureIds.Add($id)) { $issues.Add("duplicate feature ID $id") }
        foreach ($field in @('playerFunction', 'status', 'owner')) {
            if ([string]::IsNullOrWhiteSpace([string]$feature.$field)) { $issues.Add("$id has no $field") }
        }
        $ownerTokens = @([regex]::Matches([string]$feature.owner, '[A-Za-z_][A-Za-z0-9_]*') |
            ForEach-Object Value | Where-Object { $_.Length -ge 8 } |
            Sort-Object { $_.Length } -Descending)
        if ($ownerTokens.Count -eq 0 -or
            @($ownerTokens | Where-Object { $evidence.ContainsKey($_) }).Count -eq 0) {
            $issues.Add("$id names no executable owner '$([string]$feature.owner)'")
        }
        if (@($feature.routes | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
            $issues.Add("$id has no executable route inventory")
        }
        $tests = @($feature.tests | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($tests.Count -eq 0) { $issues.Add("$id has no focused tests") }
        foreach ($test in $tests) {
            $tokens = @([regex]::Matches([string]$test, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object Value)
            $method = if ($tokens.Count -eq 0) { '' } else { $tokens[-1] }
            if ([string]::IsNullOrEmpty($method) -or -not $evidence.ContainsKey($method)) {
                $issues.Add("$id names missing focused test '$test'")
            }
        }

        if ([string]$feature.status -in $releaseStatuses) { $releaseReady++ }
        elseif ($Release) { $issues.Add("$id is not release-ready ($([string]$feature.status))") }
    }
}

if ($Release -and $issues.Count -gt 0) {
    throw "FEATURE WIRING RELEASE FAILED: $($issues.Count) issue(s). $($issues -join '; ')"
}

$resultLabel = if ($issues.Count -eq 0) { 'PASS' } else { 'REVIEW' }
Write-Host ("{0}: modules={1} featureFamilies={2} releaseReady={3} open={4} issues={5}" -f `
    $resultLabel, `
    $actualModules.Count, $featureCount, $releaseReady, ($featureCount - $releaseReady), $issues.Count)
foreach ($issue in $issues) { Write-Warning $issue }
exit 0
