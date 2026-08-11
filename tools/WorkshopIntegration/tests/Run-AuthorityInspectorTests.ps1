#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Assert-Contains {
    param([object[]]$Values, [string]$Expected, [string]$Message)
    Assert-True (@($Values | Where-Object { [string]$_ -ceq $Expected }).Count -eq 1) $Message
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Required x64 .NET SDK not found: $dotnet" }

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('friend-edition-authority-inspector-' + [guid]::NewGuid().ToString('N'))
try {
    $fixtureRoot = Join-Path $testRoot 'fixture'
    $fixtureProject = Join-Path $fixtureRoot 'AuthorityFixture.csproj'
    Write-Utf8File -Path $fixtureProject -Content @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
'@
    Write-Utf8File -Path (Join-Path $fixtureRoot 'AuthorityFixture.cs') -Content @'
namespace TaleWorlds.CampaignSystem
{
    public static class Hero
    {
        public static object MainHero => null;
    }
}

namespace TaleWorlds.CampaignSystem.Actions
{
    public static class ChangeRelationAction
    {
        public static void ApplyRelationChangeBetweenHeroes(object first, object second, int value) { }
    }
}

public static class AuthorityFixture
{
    public static int Pure(int value) => value + 1;
    public static object ReadsMainHero() => TaleWorlds.CampaignSystem.Hero.MainHero;
    public static void MutatesRelation() => TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes(null, null, 1);
    public static void CallsMutationHelper() => MutatesRelation();
}
'@

    & $dotnet build $fixtureProject -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Authority fixture build failed.' }

    $fixtureAssembly = Join-Path $fixtureRoot 'bin\Release\net8.0\AuthorityFixture.dll'
    $requestPath = Join-Path $testRoot 'request.json'
    $resultPath = Join-Path $testRoot 'result.json'
    @{
        files = @(@{
            moduleId = 'AuthorityFixture'
            relativePath = 'AuthorityFixture.dll'
            path = $fixtureAssembly
            included = $true
            platform = 'fixture'
        })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $requestPath -Encoding utf8

    $inspectorProject = Join-Path $repoRoot 'tools\WorkshopIntegration\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj'
    & $dotnet run --project $inspectorProject -c Release -- $requestPath $resultPath
    if ($LASTEXITCODE -ne 0) { throw 'Assembly inspector failed.' }

    $inspection = (Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json).files[0]
    $pure = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'Pure' })[0]
    $main = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'ReadsMainHero' })[0]
    $mutation = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'MutatesRelation' })[0]
    $caller = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'CallsMutationHelper' })[0]

    Assert-True (@($pure.authorityEvidence.directSignals).Count -eq 0) 'pure method was flagged'
    Assert-Contains @($main.authorityEvidence.directSignals) 'global-player:TaleWorlds.CampaignSystem.Hero.get_MainHero' 'MainHero read was not classified'
    Assert-Contains @($mutation.authorityEvidence.directSignals) 'campaign-mutation:TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes' 'campaign mutation was not classified'
    Assert-Contains @($caller.authorityEvidence.transitiveSignals) 'calls-authority-sensitive:AuthorityFixture.MutatesRelation' 'authority-sensitive helper call was not propagated'

    Write-Host 'PASS: pure methods remain unflagged'
    Write-Host 'PASS: global player access is classified'
    Write-Host 'PASS: campaign mutation is classified'
    Write-Host 'PASS: authority sensitivity propagates through mod-owned calls'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $resolvedTest = [System.IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTest.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTest) -notlike 'friend-edition-authority-inspector-*') {
            throw "Refusing unsafe test cleanup path: $resolvedTest"
        }
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

exit 0
