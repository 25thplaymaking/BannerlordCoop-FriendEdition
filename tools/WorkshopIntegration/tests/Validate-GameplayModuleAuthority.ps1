#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string[]]$ModuleId = @(
        'UnblockableThrust',
        'DismembermentPlus',
        'Separatism',
        'ImprovedGarrisons',
        'Fourberie',
        'Bannerlord.Diplomacy',
        'PlayerSettlement',
        'RebellionsAndDemographics'
    ),
    [string]$AuditPath = 'doc\generated\workshop-authority-audit.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot '..\..\..'
}
$repo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedAudit = if ([IO.Path]::IsPathRooted($AuditPath)) {
    [IO.Path]::GetFullPath($AuditPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repo $AuditPath))
}
if (-not (Test-Path -LiteralPath $resolvedAudit -PathType Leaf)) {
    throw "GAMEPLAY AUTHORITY: audit is missing: $resolvedAudit"
}

$audit = Get-Content -LiteralPath $resolvedAudit -Raw | ConvertFrom-Json
$allowed = @(
    'ClientPresentation',
    'PurePolicy',
    'ServerCallback',
    'ServerCommand',
    'ReplicatedCosmetic',
    'CoopOwnerReplacement',
    'FrameworkLifecycle',
    # Exact methods embedded in an active binary may be retired when their prerequisite module is
    # absent and the active-module contract proves their behavior/options cannot register.
    'Retired'
)

$fourberieCanonicalStatePattern = '^shared-state-(mutation|write):Fourberie\.FourberieBehavior\._(' +
    'townScamTiming|townTributeTiming|townExtoTiming|townRobTiming|townInsuScamTiming|' +
    'townGreedyTiming|townCarambushTiming|townDomiTiming|lastVisitSetAlley|larcenyDailyTiming|' +
    'larcenyJobsTiming|InfiltrationAlertTiming|supportedBandits|stringIntDico|stringClanDico|' +
    'assignedGl|stringHeroIdDico|partnerRecomList|territoryList|partnershipList|crimeValue|' +
    'campaignTimeDictio|stringHeroDico|getSomeHelp|gangLeader|FourbParty|extoVillage|robCastle|' +
    'crimeBase|crimeBaseParty|insucaraF|insubandF|catchbandF|agentsParty|banditsFollowers|playerTroopsF)($|[^A-Za-z0-9_])'

foreach ($module in $ModuleId) {
    $records = @($audit.records | Where-Object { [string]$_.moduleId -ceq $module })
    if ($records.Count -eq 0) { throw "GAMEPLAY AUTHORITY: module '$module' has no audit records" }

    $required = @($records | Where-Object { [bool]$_.active -and [bool]$_.requiresDisposition })
    $open = @($required | Where-Object {
        [string]$_.disposition -notin $allowed -or
        [string]::IsNullOrWhiteSpace([string]$_.owner) -or
        @($_.tests | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0 -or
        ([string]$_.disposition -ceq 'ClientPresentation' -and
            @($_.evidence | Where-Object {
                [string]$_ -match '^campaign-mutation:' -or
                ([string]$module -ceq 'Fourberie' -and
                    ([string]$_ -match '^calls-authority-sensitive:' -or
                     [string]$_ -match $fourberieCanonicalStatePattern))
            }).Count -gt 0)
    })
    if ($open.Count -gt 0) {
        $labels = @($open | ForEach-Object { '{0}/{1}' -f [string]$_.metadataToken, [string]$_.method })
        throw "GAMEPLAY AUTHORITY: module '$module' has $($open.Count) open required route(s): $($labels -join ', ')"
    }

    Write-Host ("PASS: {0} required routes are fully classified ({1})" -f $module, $required.Count)
}

exit 0
