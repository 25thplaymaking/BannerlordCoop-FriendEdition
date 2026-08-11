#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Write-Json {
    param([string]$Path, [object]$Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('friend-edition-authority-generator-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $hash = 'a' * 64
    $inventoryPath = Join-Path $testRoot 'inventory.json'
    $policyPath = Join-Path $testRoot 'policy.json'
    $outputPath = Join-Path $testRoot 'audit.json'

    Write-Json -Path $inventoryPath -Value ([ordered]@{
        schemaVersion = 1
        generatedFromCommit = 'fixture'
        assemblies = @([ordered]@{
            moduleId = 'Fourberie'
            sha256 = $hash
            methods = @(
                [ordered]@{
                    declaringType = 'Fourberie.Utility'
                    name = 'Pure'
                    metadataToken = '0x06000001'
                    authorityEvidence = [ordered]@{ directSignals = @(); transitiveSignals = @(); calledMembers = @() }
                },
                [ordered]@{
                    declaringType = 'Fourberie.Actions.RecruitAction'
                    name = 'Execute'
                    metadataToken = '0x06000002'
                    authorityEvidence = [ordered]@{
                        directSignals = @(
                            'global-player:TaleWorlds.CampaignSystem.Hero.get_MainHero',
                            'campaign-mutation:TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters'
                        )
                        transitiveSignals = @()
                        calledMembers = @('TaleWorlds.CampaignSystem.Hero.get_MainHero')
                    }
                },
                [ordered]@{
                    declaringType = 'Fourberie.HomesSteadsAddOn'
                    name = 'RegisterEvents'
                    metadataToken = '0x06000003'
                    authorityEvidence = [ordered]@{
                        directSignals = @()
                        transitiveSignals = @()
                        calledMembers = @()
                    }
                }
            )
        })
    })
    Write-Json -Path $policyPath -Value ([ordered]@{
        schemaVersion = 1
        allowedDispositions = @('ClientPresentation', 'PurePolicy', 'ServerCallback', 'ServerCommand', 'ReplicatedCosmetic', 'CoopOwnerReplacement', 'FrameworkLifecycle', 'Retired')
        rules = @([ordered]@{
            moduleId = 'Fourberie'
            assemblySha256 = $hash
            metadataTokens = @('0x06000002')
            disposition = 'ServerCommand'
            owner = 'FourberieRecruitHandler'
            capability = 'RecruitBandits'
            tests = @('FourberieRecruitTests.Routes')
        }, [ordered]@{
            moduleId = 'Fourberie'
            assemblySha256 = $hash
            metadataTokens = @('0x06000003')
            disposition = 'Retired'
            owner = 'FriendEditionWorkshopModuleCatalog.AbsentHomesteadsDependency'
            capability = ''
            tests = @('WorkshopModuleCatalogTests.ActiveModulesExcludeHomesteads')
        })
    })

    & (Join-Path $repoRoot 'tools\WorkshopIntegration\Generate-AuthorityAudit.ps1') `
        -RepoRoot $repoRoot -InventoryPath $inventoryPath -PolicyPath $policyPath -OutputPath $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Authority audit generator failed.' }

    $audit = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    Assert-True (@($audit.records).Count -eq 3) 'generator did not preserve the complete method surface'
    $pure = @($audit.records | Where-Object { $_.metadataToken -ceq '0x06000001' })[0]
    $execute = @($audit.records | Where-Object { $_.metadataToken -ceq '0x06000002' })[0]
    Assert-True (-not [bool]$pure.requiresDisposition) 'pure helper was promoted without authority evidence or entrypoint semantics'
    Assert-True ([string]$pure.disposition -ceq 'Unclassified') 'pure helper received a fabricated disposition'
    Assert-True ([bool]$execute.requiresDisposition) 'authority-sensitive Execute entrypoint was omitted'
    Assert-True ([string]$execute.disposition -ceq 'ServerCommand') 'exact hash/token policy was not applied'
    Assert-True ([string]$execute.owner -ceq 'FourberieRecruitHandler') 'route owner was not materialized'
    Assert-True ((@($execute.evidence) -join '|') -ceq 'global-player:TaleWorlds.CampaignSystem.Hero.get_MainHero|campaign-mutation:TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters|entrypoint:Execute') 'evidence was not deterministic'

    $validator = Join-Path $repoRoot 'tools\WorkshopIntegration\tests\Validate-AuthorityAudit.ps1'
    & $validator -RepoRoot $testRoot -AuditPath $outputPath -PolicyPath $policyPath -InventoryPath $inventoryPath
    if ($LASTEXITCODE -ne 0) { throw 'Valid synthetic authority audit was rejected.' }

    $gameplayValidator = Join-Path $repoRoot 'tools\WorkshopIntegration\tests\Validate-GameplayModuleAuthority.ps1'
    & $gameplayValidator -RepoRoot $testRoot -AuditPath $outputPath -ModuleId Fourberie
    if ($LASTEXITCODE -ne 0) { throw 'Gameplay validator rejected proven unreachable embedded add-on code.' }

    $unsafePresentationPath = Join-Path $testRoot 'audit-unsafe-presentation.json'
    $unsafePresentationAudit = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    $unsafePresentationRecord = @($unsafePresentationAudit.records | Where-Object {
        [string]$_.metadataToken -ceq '0x06000002'
    })[0]
    $unsafePresentationRecord.disposition = 'ClientPresentation'
    Write-Json -Path $unsafePresentationPath -Value $unsafePresentationAudit
    $unsafePresentationRejected = $false
    try {
        & $gameplayValidator -RepoRoot $testRoot -AuditPath $unsafePresentationPath -ModuleId Fourberie
    }
    catch {
        $unsafePresentationRejected = $true
    }
    Assert-True $unsafePresentationRejected 'gameplay validator accepted campaign mutation as client presentation'

    $missingPath = Join-Path $testRoot 'audit-missing-record.json'
    $missingAudit = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    $missingAudit.records = @($missingAudit.records | Select-Object -First 1)
    Write-Json -Path $missingPath -Value $missingAudit
    Assert-True (@((Get-Content -LiteralPath $missingPath -Raw | ConvertFrom-Json).records).Count -eq 1) 'missing-record fixture was not reduced'
    $missingRejected = $false
    try {
        & $validator -RepoRoot $testRoot -AuditPath $missingPath -PolicyPath $policyPath -InventoryPath $inventoryPath
    }
    catch {
        $missingRejected = $true
    }
    Assert-True $missingRejected 'validator accepted an audit with a deleted method record'

    Write-Host 'PASS: authority audit preserves every method and flags candidate entrypoints'
    Write-Host 'PASS: exact hash/token dispositions join to only their intended method'
    Write-Host 'PASS: authority validator rejects a missing exact method record'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $resolvedTest = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTest) -notlike 'friend-edition-authority-generator-*') {
            throw "Refusing unsafe test cleanup path: $resolvedTest"
        }
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

exit 0
