#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..\WorkshopIntegration.psm1') -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Write-TestFile {
    param([string]$Path, [string]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-TestModule {
    param([string]$Root, [string]$Id, [string]$Name, [string]$Version, [string]$Dll)
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $xml = @"
<Module>
  <Name value="$Name" />
  <Id value="$Id" />
  <Version value="$Version" />
  <SingleplayerModule value="true" />
  <MultiplayerModule value="false" />
  <SubModules><SubModule><Name value="$Name" /><DLLName value="$Dll" /><SubModuleClassType value="$Id.Entry" /></SubModule></SubModules>
</Module>
"@
    Write-TestFile -Path (Join-Path $Root 'SubModule.xml') -Content $xml
    Write-TestFile -Path (Join-Path $Root "bin\Win64_Shipping_Client\$Dll") -Content "$Id runtime"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$productionManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'deploy\workshop-mods.json') -Raw | ConvertFrom-Json
$approvedWorkshopIds = @(
    'Bannerlord.Harmony', 'Bannerlord.ButterLib', 'Bannerlord.UIExtenderEx',
    'Bannerlord.MBOptionScreen', 'ImprovedGarrisons', 'DismembermentPlus', 'Fourberie',
    'Bannerlord.Diplomacy', 'UnblockableThrust', 'PlayerSettlement'
)
$approvedActiveOrder = @(
    'Bannerlord.Harmony', 'Bannerlord.ButterLib', 'Bannerlord.UIExtenderEx',
    'Bannerlord.MBOptionScreen', 'Native', 'SandBoxCore', 'CustomBattle', 'Sandbox',
    'StoryMode', 'PlayerSettlement', 'Coop', 'ImprovedGarrisons', 'DismembermentPlus',
    'Fourberie', 'Bannerlord.Diplomacy', 'UnblockableThrust'
)
$productionIds = @($productionManifest.modules | ForEach-Object { [string]$_.moduleId })
Assert-True ([int]$productionManifest.suite.expectedModuleCount -eq 10) 'production suite must require exactly ten Workshop modules'
Assert-True (($productionIds -join ',') -ceq ($approvedWorkshopIds -join ',')) 'production suite must contain the exact approved ten-module set without RBM'
Assert-True ((@($productionManifest.activationPolicy.client.exactModuleOrder) -join ',') -ceq ($approvedActiveOrder -join ',')) 'client exact order must match the approved ten-module loadout'
Assert-True ((@($productionManifest.activationPolicy.client.activeModuleOrder) -join ',') -ceq ($approvedActiveOrder -join ',')) 'client active order must match the approved ten-module loadout'
Assert-True (@($productionManifest.activationPolicy.client.stagedInactiveModuleIds).Count -eq 0) 'client suite must not retain stale inactive Workshop entries'
Assert-True ((@($productionManifest.activationPolicy.server.exactActiveModuleOrder) -join ',') -ceq ($approvedActiveOrder -join ',')) 'server active order must match the approved ten-module loadout'
Assert-True (@($productionManifest.activationPolicy.server.guardedModuleIds).Count -eq 0) 'server suite must not retain stale guarded Workshop entries'
Assert-True ((@($productionManifest.activationPolicy.server.neverActivateModuleIds) -join ',') -ceq 'BirthAndDeath') 'only optional TaleWorlds BirthAndDeath remains excluded'
$launcherConfig = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\CoopLauncher\launcher-config.json') -Raw | ConvertFrom-Json
$launcherOrder = @(([string]$launcherConfig.moduleToken).Split('*') | Where-Object { $_ -and $_ -notin @('_MODULES_') })
Assert-True (($launcherOrder -join ',') -ceq ($approvedActiveOrder -join ',')) 'launcher token must match the approved ten-module loadout'
Write-Host 'PASS: production catalog inputs agree on the exact active ten-module loadout and retired RBM'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('friend-edition-workshop-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $library = Join-Path $testRoot 'SteamLibrary'
    $workshopRoot = Join-Path $library 'steamapps\workshop'
    $contentRoot = Join-Path $workshopRoot 'content\261550'
    $moduleA = Join-Path $contentRoot '1001'
    $moduleB = Join-Path $contentRoot '1002'
    Write-TestModule -Root $moduleA -Id 'Canonical.Harmony' -Name 'Canonical Harmony' -Version 'v1.0.0' -Dll 'Canonical.Harmony.dll'
    Write-TestFile -Path (Join-Path $moduleA 'bin\Win64_Shipping_Client\0Harmony.dll') -Content 'canonical harmony dependency'
    Write-TestModule -Root $moduleB -Id 'Gameplay.Mod' -Name 'Gameplay Mod' -Version 'v2.0.0' -Dll 'Gameplay.Mod.dll'
    Write-TestFile -Path (Join-Path $moduleB 'bin\Win64_Shipping_Client\0Harmony.dll') -Content 'dangerous duplicate'
    Write-TestFile -Path (Join-Path $moduleB 'LICENSE.txt') -Content 'fixture license'
    New-Item -ItemType Directory -Path (Join-Path $contentRoot '9999') -Force | Out-Null
    $coopInput = Join-Path $testRoot 'CoopInput'
    Write-TestModule -Root $coopInput -Id 'Coop' -Name 'Coop' -Version 'v0.1.1' -Dll 'Coop.dll'
    Write-TestFile -Path (Join-Path $coopInput 'mod-config.default.json') -Content '{ "difficulty": { "birthAndDeath": true } }'
    Write-TestFile -Path (Join-Path $coopInput 'bin\Win64_Shipping_Client\0Harmony.dll') -Content 'second Harmony provider'
    $dotnet = if (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe') { 'C:\Program Files\dotnet\dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $inspectorProject = Join-Path $PSScriptRoot '..\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj'
    & $dotnet build $inspectorProject -c Release --nologo -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the fixture assembly metadata inspector.' }
    $managedFixture = Join-Path $PSScriptRoot '..\AssemblyInspector\bin\Release\net8.0\FriendEdition.WorkshopAssemblyInspector.dll'
    Copy-Item -LiteralPath $managedFixture -Destination (Join-Path $moduleA 'bin\Win64_Shipping_Client\0Harmony.dll') -Force
    Copy-Item -LiteralPath $managedFixture -Destination (Join-Path $coopInput 'bin\Win64_Shipping_Client\0Harmony.dll') -Force
    Write-TestFile -Path (Join-Path $coopInput 'bin\Win64_Shipping_Client\TaleWorlds.Core.dll') -Content 'stale game API copy'
    Write-TestFile -Path (Join-Path $coopInput 'bin\Win64_Shipping_Client\SandBox.View.dll') -Content 'stale game module copy'
    Write-TestFile -Path (Join-Path $coopInput 'bin\Win64_Shipping_Client\Newtonsoft.Json.dll') -Content 'stale JSON copy'
    $fixtureHarmony = Get-Item -LiteralPath $managedFixture
    $fixtureHarmonyHash = (Get-FileHash -LiteralPath $managedFixture -Algorithm SHA256).Hash.ToLowerInvariant()

    $acf = @'
"AppWorkshop"
{
  "appid" "261550"
  "NeedsUpdate" "0"
  "NeedsDownload" "0"
  "WorkshopItemsInstalled"
  {
    "1001" { "size" "1" "timeupdated" "10" "manifest" "manifest-a" }
    "1002" { "size" "1" "timeupdated" "20" "manifest" "manifest-b" }
  }
  "WorkshopItemDetails"
  {
    "1001" { "manifest" "manifest-a" "timeupdated" "10" "latest_timeupdated" "10" "latest_manifest" "manifest-a" }
    "1002" { "manifest" "manifest-b" "timeupdated" "20" "latest_timeupdated" "20" "latest_manifest" "manifest-b" }
  }
}
'@
    Write-TestFile -Path (Join-Path $workshopRoot 'appworkshop_261550.acf') -Content $acf

    $manifestObject = [ordered]@{
        schemaVersion = 1
        suite = [ordered]@{
            id = 'fixture-suite'; displayName = 'Fixture Suite'; appId = '261550'; expectedModuleCount = 2
            requireExactSubscriptionSet = $true; audience = 'test'; memberCount = 3
            permissionDate = '2026-08-08'; permissionAttestation = 'Fixture permission.'; baseModuleIds = @()
        }
        coopModule = [ordered]@{
            moduleId = 'Coop'
            exclusions = @(
                [ordered]@{
                    glob = 'bin/*/0Harmony.dll'; minimumMatches = 1; reason = 'one provider'
                    assemblyClosure = [ordered]@{ providerModuleId = 'Canonical.Harmony'; allowUnsignedProviderUpgrade = $false }
                },
                [ordered]@{ glob = 'bin/*/TaleWorlds.*.dll'; minimumMatches = 1; reason = 'game API files never ship in Coop' },
                [ordered]@{ glob = 'bin/*/SandBox*.dll'; minimumMatches = 1; reason = 'game module files never ship in Coop' },
                [ordered]@{ glob = 'bin/*/Newtonsoft.Json.dll'; minimumMatches = 1; reason = 'noncanonical JSON never ships in Coop' }
            )
        }
        sideBySideAssemblyAllowances = @()
        assemblyPolicies = @(
            [ordered]@{ fileNamePattern = '0Harmony.dll'; allowedModuleIds = @('Canonical.Harmony') },
            [ordered]@{ fileNamePattern = 'TaleWorlds.*.dll'; allowedModuleIds = @() },
            [ordered]@{ fileNamePattern = 'SandBox*.dll'; allowedModuleIds = @() },
            [ordered]@{ fileNamePattern = 'Newtonsoft.Json.dll'; allowedModuleIds = @('Canonical.Json') }
        )
        activationPolicy = [ordered]@{
            client = [ordered]@{
                activateAllManagedModules = $false
                exactModuleOrder = @('Canonical.Harmony', 'Coop', 'Gameplay.Mod')
                activeModuleOrder = @('Canonical.Harmony', 'Coop')
                stagedInactiveModuleIds = @('Gameplay.Mod')
                disabledModuleIds = @('BirthAndDeath')
            }
            server = [ordered]@{
                canonicalHarmonyPolicy = 'fixture canonical provider'; neverActivateModuleIds = @('BirthAndDeath'); guardedModuleIds = @('Gameplay.Mod')
                exactActiveModuleOrder = @('Canonical.Harmony', 'Coop')
                harmonyPreflight = [ordered]@{
                    selectedNightly = 'fixture'; scriptPath = 'Verify-ServerHarmony.ps1'; expectationPath = 'SERVER-HARMONY.json'
                    platform = 'Win64_Shipping_Client'; moduleId = 'Canonical.Harmony'; moduleVersion = 'v1.0.0'
                    requiredModuleIds = @('Canonical.Harmony', 'Coop'); harmonyMustPrecede = @('Coop')
                    payloads = @([ordered]@{
                        path = 'bin/Win64_Shipping_Client/0Harmony.dll'; size = $fixtureHarmony.Length; sha256 = $fixtureHarmonyHash
                        assemblyFullName = 'FriendEdition.WorkshopAssemblyInspector, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
                    })
                }
            }
        }
        modules = @(
            [ordered]@{
                loadOrder = 0; placement = 'before Native'; workshopId = '1001'; steamManifestId = 'manifest-a'
                workshopUrl = 'https://example.invalid/1001'; moduleId = 'Canonical.Harmony'; name = 'Canonical Harmony'; version = 'v1.0.0'; creator = 'Fixture A'
                permission = [ordered]@{ status = 'fixture'; scope = 'test' }; exclusions = @()
            },
            [ordered]@{
                loadOrder = 100; placement = 'after Friend Edition'; workshopId = '1002'; steamManifestId = 'manifest-b'
                workshopUrl = 'https://example.invalid/1002'; moduleId = 'Gameplay.Mod'; name = 'Gameplay Mod'; version = 'v2.0.0'; creator = 'Fixture B'
                permission = [ordered]@{ status = 'fixture'; scope = 'test' }
                exclusions = @([ordered]@{ glob = 'bin/*/0Harmony.dll'; minimumMatches = 1; reason = 'fixture duplicate' })
            }
        )
    }
    $manifestPath = Join-Path $testRoot 'manifest.json'
    Write-TestFile -Path $manifestPath -Content ($manifestObject | ConvertTo-Json -Depth 20)

    $beforeA = Get-DirectorySnapshot -Root $moduleA
    $beforeB = Get-DirectorySnapshot -Root $moduleB
    $manifest = Read-WorkshopSuiteManifest -Path $manifestPath
    $workshop = Find-BannerlordWorkshop -WorkshopRoot $workshopRoot -AppId '261550' -NonInteractive
    $beforeCoop = Get-DirectorySnapshot -Root $coopInput
    $plan = New-WorkshopSuitePlan -Manifest $manifest -Workshop $workshop -CoopModuleRoot $coopInput
    Assert-True ($plan.Modules.Count -eq 2) 'exact ACF subscriptions should be planned'
    Assert-True (@($plan.Modules[1].ExcludedFiles).Count -eq 1) 'duplicate Harmony should be excluded'
    Assert-True ($plan.AssemblyAudit.ClosureProofs.Count -eq 1) 'assembly reference closure should prove the Coop Harmony exclusion'
    $inspectorRequest = @([ordered]@{
        moduleId = 'Inspector.Fixture'; relativePath = 'Inspector.Fixture.dll'; path = $managedFixture
        included = $true; platform = 'Win64_Shipping_Client'
    })
    $workshopModule = Get-Module WorkshopIntegration
    $fixtureInspection = @(& $workshopModule { param($files) Invoke-AssemblyInspector -Files $files } $inspectorRequest)[0]
    Assert-True (@($fixtureInspection.methods).Count -gt 0) 'assembly inspector must enumerate the complete managed method surface'
    $requestConstructor = @($fixtureInspection.methods | Where-Object {
        [string]$_.declaringType -ceq 'InspectionRequest' -and
        [string]$_.name -ceq '.ctor' -and
        @($_.parameterTypes).Count -eq 1 -and
        [string]$_.parameterTypes[0] -ceq 'InspectionFile[]'
    })
    Assert-True ($requestConstructor.Count -eq 1 -and
        [string]$requestConstructor[0].returnType -ceq 'System.Void' -and
        [int]$requestConstructor[0].genericArity -eq 0 -and
        [string]$requestConstructor[0].metadataToken -match '^0x06[0-9A-F]{6}$') 'method inventory must preserve declaring type, return type, parameter shape, arity, and token'
    Assert-True (@($plan.Coop.ExcludedFiles).Count -eq 4) 'Coop plan must exclude Harmony, TaleWorlds, Sandbox, and noncanonical JSON payloads'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'dry-run-output'))) 'planning/dry run must not create output'

    $output = Join-Path $testRoot 'suite-output'
    $result = Invoke-WorkshopSuiteStage -Plan $plan -OutputRoot $output
    Assert-True ($result.ModuleCount -eq 2) 'two modules should be staged'
    Assert-True ($result.SourceUnchanged) 'stage should report source unchanged'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Modules\Canonical.Harmony\SubModule.xml')) 'canonical module root should stay separate'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Modules\Gameplay.Mod\SubModule.xml')) 'gameplay module root should stay separate'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\Gameplay.Mod\bin\Win64_Shipping_Client\0Harmony.dll'))) 'duplicate runtime should not be staged'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\Coop\bin\Win64_Shipping_Client\0Harmony.dll'))) 'Coop must not contribute a second Harmony provider'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\Coop\bin\Win64_Shipping_Client\TaleWorlds.Core.dll'))) 'Coop must not stage TaleWorlds game API copies'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\Coop\bin\Win64_Shipping_Client\SandBox.View.dll'))) 'Coop must not stage Sandbox game-module copies'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\Coop\bin\Win64_Shipping_Client\Newtonsoft.Json.dll'))) 'Coop must not stage noncanonical JSON copies'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Modules\Coop\WorkshopSuite\MANIFEST.json')) 'managed receipt must survive Modules-only installation'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'SERVER-ACTIVATION.txt')) 'role-aware server activation guidance must be shipped'
    $clientPolicyText = Get-Content -LiteralPath (Join-Path $output 'CLIENT-LOAD-ORDER.txt') -Raw
    Assert-True ($clientPolicyText -match '\[ACTIVE\]\s+Canonical\.Harmony' -and $clientPolicyText -match '\[STAGED-INACTIVE\]\s+Gameplay\.Mod') 'client instructions must distinguish active from staged-inactive modules'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Verify-ServerHarmony.ps1')) 'server Harmony preflight must be shipped'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Verify-ServerHarmony.py')) 'cross-platform server Harmony preflight must be shipped'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'SERVER-HARMONY.json')) 'pinned server Harmony expectation must be shipped'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Setup-ManagedSuiteClient.ps1')) 'guided client installer must be shipped'
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'Run-ClientSetup.cmd')) 'double-click client setup launcher must be shipped'
    $managedReceipt = Get-Content -LiteralPath (Join-Path $output 'Modules\Coop\WorkshopSuite\MANIFEST.json') -Raw | ConvertFrom-Json
    $rootManifest = Get-Content -LiteralPath (Join-Path $output 'MANIFEST.json') -Raw | ConvertFrom-Json
    Assert-True ([bool]$rootManifest.clientInstaller.activatesOnlyActiveModuleOrder -and @($rootManifest.clientInstaller.files).Count -eq 2) 'suite must pin the friendly active-only client installer'
    Assert-True ([bool]$rootManifest.coop.birthAndDeath.packagedDefaultEnabled -and -not [bool]$rootManifest.coop.birthAndDeath.runtimeValueVerified -and -not [bool]$rootManifest.coop.birthAndDeath.taleWorldsModuleActive) 'suite must distinguish the true seed default from an unverified runtime value while TaleWorlds BirthAndDeath stays disabled'
    $gameplayReceipt = @($managedReceipt.modules | Where-Object { [string]$_.moduleId -ceq 'Gameplay.Mod' })[0]
    Assert-True ([int]$gameplayReceipt.loadOrder -eq 100) 'managed receipt must pin exact numeric load order'
    Assert-True (Test-Path -LiteralPath (Join-Path $moduleB 'bin\Win64_Shipping_Client\0Harmony.dll')) 'excluded source file must remain in Workshop source'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'Modules\9999'))) 'stale empty Workshop directory must not be staged'

    $fakeServerRoot = Join-Path $testRoot 'fixture-server'
    $fakeHarmonyRoot = Join-Path $fakeServerRoot 'Modules\Canonical.Harmony'
    New-Item -ItemType Directory -Path $fakeHarmonyRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $moduleA 'SubModule.xml') -Destination $fakeHarmonyRoot
    New-Item -ItemType Directory -Path (Join-Path $fakeHarmonyRoot 'bin\Win64_Shipping_Client') -Force | Out-Null
    Copy-Item -LiteralPath $managedFixture -Destination (Join-Path $fakeHarmonyRoot 'bin\Win64_Shipping_Client\0Harmony.dll')
    $runtimeConfigPath = Join-Path $testRoot 'CoopData\mod-config.json'
    Write-TestFile -Path $runtimeConfigPath -Content "{ // runtime, not the staged default`n `"difficulty`": { `"birthAndDeath`": true, },`n}"
    & (Join-Path $output 'Verify-ServerHarmony.ps1') -ServerRoot $fakeServerRoot -RuntimeModConfigPath $runtimeConfigPath -ActiveModuleIds @('Canonical.Harmony', 'Coop') -NonInteractive

    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        # Expected-negative child processes write to stderr. Windows PowerShell 5.1 wraps
        # native stderr as a non-terminating NativeCommandError, so temporarily use Continue
        # and assert the child's captured exit code rather than relaxing production scripts.
        $ErrorActionPreference = 'Continue'
        $guardedOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Verify-ServerHarmony.ps1') `
            -ServerRoot $fakeServerRoot -RuntimeModConfigPath $runtimeConfigPath -ActiveModuleIds 'Canonical.Harmony,Coop,Gameplay.Mod' -NonInteractive 2>&1
        $guardedExit = $LASTEXITCODE
        $extraServerOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Verify-ServerHarmony.ps1') `
            -ServerRoot $fakeServerRoot -RuntimeModConfigPath $runtimeConfigPath -ActiveModuleIds 'Canonical.Harmony,Coop,Unreviewed.Mod' -NonInteractive 2>&1
        $extraServerExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedErrorActionPreference }
    Assert-True ($guardedExit -eq 1 -and (($guardedOutput -join "`n") -match "blocks staged-inactive module 'Gameplay.Mod'")) 'production server preflight must reject guarded original modules'
    Assert-True ($extraServerExit -eq 1 -and (($extraServerOutput -join "`n") -match 'must exactly match the pinned order')) 'production server preflight must reject extra unreviewed modules'
    $python = $null
    foreach ($candidate in @(Get-Command python3, python -ErrorAction SilentlyContinue)) {
        try {
            & $candidate.Source --version 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { $python = $candidate.Source; break }
        }
        catch { }
    }
    if ($null -ne $python) {
        & $python (Join-Path $output 'Verify-ServerHarmony.py') --server-root $fakeServerRoot --runtime-mod-config $runtimeConfigPath --active-modules 'Canonical.Harmony,Coop'
        if ($LASTEXITCODE -ne 0) { throw 'Cross-platform server Harmony/config preflight failed.' }
    }
    else {
        Write-Host 'SKIP: Python server preflight execution (no functional Python runtime on this Windows host)'
    }

    $fakeGameRoot = Join-Path $testRoot 'client-game'
    Write-TestModule -Root (Join-Path $fakeGameRoot 'Modules\Native') -Id 'Native' -Name 'Native' -Version 'v1.0.0' -Dll 'Native.dll'
    $staleGameplay = Join-Path $fakeGameRoot 'Modules\Gameplay.Mod'
    New-Item -ItemType Directory -Path $staleGameplay -Force | Out-Null
    Write-TestFile -Path (Join-Path $staleGameplay 'old-client-copy.txt') -Content 'recoverable old module'
    $launcherPath = Join-Path $testRoot 'client-documents\LauncherData.xml'
    Write-TestFile -Path $launcherPath -Content @'
<UserData>
  <SingleplayerData>
    <ModDatas>
      <UserModData><Id>BirthAndDeath</Id><LastKnownVersion>v1.0.0</LastKnownVersion><IsSelected>true</IsSelected></UserModData>
      <UserModData><Id>Unrelated.Mod</Id><LastKnownVersion>v1.0.0</LastKnownVersion><IsSelected>true</IsSelected></UserModData>
    </ModDatas>
  </SingleplayerData>
</UserData>
'@
    $clientBackupRoot = Join-Path $testRoot 'client-backups'
    $clientValidateOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Setup-ManagedSuiteClient.ps1') `
        -GamePath $fakeGameRoot -LauncherDataPath $launcherPath -BackupRoot $clientBackupRoot -ValidateOnly -NonInteractive 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and (($clientValidateOutput -join "`n") -match 'VALIDATION PASSED')) "guided client installer ValidateOnly must resolve its package beside the script and accept an explicit arbitrary game path without writes. Output: $($clientValidateOutput -join ' | ')"
    Assert-True (Test-Path -LiteralPath (Join-Path $staleGameplay 'old-client-copy.txt')) 'client ValidateOnly must not replace an existing module'

    $noncanonicalGameplay = Join-Path $fakeGameRoot 'Modules\OldGameplayCopy'
    Write-TestModule -Root $noncanonicalGameplay -Id 'Gameplay.Mod' -Name 'Old Gameplay Copy' -Version 'v1.0.0' -Dll 'Old.Gameplay.dll'
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $duplicateValidateOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Setup-ManagedSuiteClient.ps1') `
            -SuiteRoot $output -GamePath $fakeGameRoot -LauncherDataPath $launcherPath -BackupRoot $clientBackupRoot -ValidateOnly -NonInteractive 2>&1
        $duplicateValidateExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedErrorActionPreference }
    Assert-True ($duplicateValidateExit -eq 1 -and (($duplicateValidateOutput -join "`n") -match [regex]::Escape($noncanonicalGameplay))) 'client ValidateOnly must fail with the exact noncanonical duplicate path'

    $clientInstallOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Setup-ManagedSuiteClient.ps1') `
        -SuiteRoot $output -GamePath $fakeGameRoot -LauncherDataPath $launcherPath -BackupRoot $clientBackupRoot -NonInteractive 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and (($clientInstallOutput -join "`n") -match 'INSTALLATION PASSED')) "guided client installer must complete against the fixture game. Output: $($clientInstallOutput -join ' | ')"
    foreach ($id in @('Canonical.Harmony', 'Gameplay.Mod', 'Coop')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $fakeGameRoot "Modules\$id\SubModule.xml")) "client installer must copy separate module $id"
    }
    Assert-True (@(Get-ChildItem -LiteralPath $clientBackupRoot -Filter 'old-client-copy.txt' -File -Recurse).Count -eq 1) 'client installer must preserve the prior same-ID module in its durable backup'
    Assert-True (-not (Test-Path -LiteralPath $noncanonicalGameplay)) 'client installer must move the old noncanonical duplicate out of live Modules'
    Assert-True (@(Get-ChildItem -LiteralPath $clientBackupRoot -Filter 'Old.Gameplay.dll' -File -Recurse).Count -eq 1) 'client installer must preserve the noncanonical duplicate in its durable backup'
    [xml]$installedLauncher = Get-Content -LiteralPath $launcherPath -Raw
    $selectedIds = @($installedLauncher.SelectNodes('//SingleplayerData/ModDatas/UserModData') | Where-Object { [string]$_.IsSelected -ieq 'true' } | ForEach-Object { [string]$_.Id })
    Assert-True (($selectedIds -join ',') -ceq 'Canonical.Harmony,Coop') 'client installer must select only activeModuleOrder'
    $gameplayLauncher = @($installedLauncher.SelectNodes('//SingleplayerData/ModDatas/UserModData') | Where-Object { [string]$_.Id -ceq 'Gameplay.Mod' })
    Assert-True ($gameplayLauncher.Count -eq 1 -and [string]$gameplayLauncher[0].IsSelected -ieq 'false') 'guarded gameplay module must stay staged-inactive on the client'
    $birthLauncher = @($installedLauncher.SelectNodes('//SingleplayerData/ModDatas/UserModData') | Where-Object { [string]$_.Id -ceq 'BirthAndDeath' })
    Assert-True ($birthLauncher.Count -eq 1 -and [string]$birthLauncher[0].IsSelected -ieq 'false') 'TaleWorlds BirthAndDeath must be disabled by client setup'

    $installerSource = Get-Content -LiteralPath (Join-Path $output 'Setup-ManagedSuiteClient.ps1') -Raw
    Assert-True ($installerSource -match '\$transactionRoot\s*=\s*Join-Path\s+\$modulesRoot' -and
        $installerSource -notmatch '\$installStage\s*=\s*Join-Path\s+\$backupRunRoot') 'all live module swaps must use staging on the game/Modules volume, independent of a C:-vs-G: backup path'

    $faultGameRoot = Join-Path $testRoot 'fault-client-game'
    Write-TestModule -Root (Join-Path $faultGameRoot 'Modules\Native') -Id 'Native' -Name 'Native' -Version 'v1.0.0' -Dll 'Native.dll'
    $oldHarmonyRoot = Join-Path $faultGameRoot 'Modules\Canonical.Harmony'
    Write-TestModule -Root $oldHarmonyRoot -Id 'Canonical.Harmony' -Name 'Old Canonical Harmony' -Version 'v0.9.0' -Dll 'Old.Canonical.dll'
    Write-TestFile -Path (Join-Path $oldHarmonyRoot 'old-build-marker.txt') -Content 'must survive injected failure'
    $faultLauncherPath = Join-Path $testRoot 'fault-client-documents\LauncherData.xml'
    Write-TestFile -Path $faultLauncherPath -Content @'
<UserData><SingleplayerData><ModDatas>
  <UserModData><Id>Unrelated.Mod</Id><LastKnownVersion>v1.0.0</LastKnownVersion><IsSelected>true</IsSelected></UserModData>
</ModDatas></SingleplayerData></UserData>
'@
    $faultLauncherHash = (Get-FileHash -LiteralPath $faultLauncherPath -Algorithm SHA256).Hash
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        [Environment]::SetEnvironmentVariable('FRIEND_EDITION_TEST_FAIL_AFTER_FIRST_SWAP', '1')
        $ErrorActionPreference = 'Continue'
        $faultOutput = & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $output 'Setup-ManagedSuiteClient.ps1') `
            -SuiteRoot $output -GamePath $faultGameRoot -LauncherDataPath $faultLauncherPath -BackupRoot (Join-Path $testRoot 'fault-client-backups') -NonInteractive 2>&1
        $faultExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
        [Environment]::SetEnvironmentVariable('FRIEND_EDITION_TEST_FAIL_AFTER_FIRST_SWAP', $null)
    }
    Assert-True ($faultExit -eq 1 -and (($faultOutput -join "`n") -match 'Injected client-installer rollback test failure')) 'fault injection must exercise installer rollback after the first module swap'
    Assert-True (Test-Path -LiteralPath (Join-Path $oldHarmonyRoot 'old-build-marker.txt')) 'same-volume rollback must restore the complete previous module'
    [xml]$restoredOldHarmony = Get-Content -LiteralPath (Join-Path $oldHarmonyRoot 'SubModule.xml') -Raw
    Assert-True ([string]$restoredOldHarmony.Module.Version.value -ceq 'v0.9.0') 'rollback must restore the previous module descriptor/version'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultGameRoot 'Modules\Coop'))) 'rollback must not leave later managed modules live'
    Assert-True ((Get-FileHash -LiteralPath $faultLauncherPath -Algorithm SHA256).Hash -ceq $faultLauncherHash) 'fault before launcher update must leave LauncherData.xml byte-identical'

    $verified = Test-WorkshopSuite -SuiteRoot $output -ExpectedSuiteId 'fixture-suite'
    Assert-True ($verified.VerifiedFiles -gt 0) 'suite hashes should verify'
    $afterA = Get-DirectorySnapshot -Root $moduleA
    $afterB = Get-DirectorySnapshot -Root $moduleB
    Assert-True ($beforeA.Digest -ceq $afterA.Digest) 'canonical Workshop source digest must be unchanged'
    Assert-True ($beforeB.Digest -ceq $afterB.Digest) 'gameplay Workshop source digest must be unchanged'
    $afterCoop = Get-DirectorySnapshot -Root $coopInput
    Assert-True ($beforeCoop.Digest -ceq $afterCoop.Digest) 'Coop input digest must be unchanged'

    Write-TestFile -Path (Join-Path $output 'builder-owned-stale-file.txt') -Content 'remove on force rebuild'
    $second = Invoke-WorkshopSuiteStage -Plan $plan -OutputRoot $output -Force
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $output 'builder-owned-stale-file.txt'))) 'force replacement should replace only the owned suite'
    Assert-True ($second.SourceUnchanged) 'force rebuild should preserve source'

    Write-Host 'PASS: Valve ACF parsing and exact subscription selection'
    Write-Host 'PASS: separate module staging and stale directory omission'
    Write-Host 'PASS: duplicate runtime exclusion without breaking SubModule.xml'
    Write-Host 'PASS: staged checksum verification'
    Write-Host 'PASS: before/after Workshop source SHA-256 digests unchanged'
    Write-Host 'PASS: Coop composition carries managed receipt and exactly one Harmony provider'
    Write-Host 'PASS: read-only server Harmony identity/hash/order preflight'
    Write-Host 'PASS: production server preflight rejects guarded original modules'
    Write-Host 'PASS: production server preflight enforces exact active module equality/order'
    Write-Host 'PASS: guided arbitrary-path client install enables only activeModuleOrder and recoverably removes duplicate old builds'
    Write-Host 'PASS: game-volume swap design and injected-failure rollback preserve the previous client build'
    Write-Host 'PASS: safe builder-owned force replacement'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $resolvedTest = [System.IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTest.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTest) -notlike 'friend-edition-workshop-tests-*') {
            throw "Refusing unsafe test cleanup path: $resolvedTest"
        }
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

# Every failure path above throws. Without this, the exit code of the last native child process
# leaks as the script's own — including the rejection tests' verifier, which is SUPPOSED to exit 1.
exit 0
