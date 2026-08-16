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

namespace Common.Messaging
{
    public interface ICommand { }
    public enum AuthorityRouteKind { Command = 0, BootstrapQuery = 1 }
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
    public sealed class AuthorityRouteAttribute : System.Attribute
    {
        public AuthorityRouteAttribute(string routeId, AuthorityRouteKind kind) { }
    }
}

[Common.Messaging.AuthorityRoute("fixture.command", Common.Messaging.AuthorityRouteKind.Command)]
public sealed class RoutedFixtureCommand : Common.Messaging.ICommand { }

public sealed class AuthorityFixtureSingleton { }

public static class AuthorityFixture
{
    public static readonly System.Collections.Generic.Dictionary<int, int> Shared = new();
    public static int Counter;
    private static AuthorityFixtureSingleton _instance;
    public static AuthorityFixtureSingleton Instance
    {
        get => _instance ??= new AuthorityFixtureSingleton();
        set => _instance = value;
    }

    public static int Pure(int value) => value + 1;
    public static object ReadsMainHero() => TaleWorlds.CampaignSystem.Hero.MainHero;
    public static void MutatesRelation() => TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes(null, null, 1);
    public static void CallsMutationHelper() => MutatesRelation();
    public static void MutatesSharedDictionary() => Shared.Add(1, 2);
    public static int ReadsSharedDictionary() => Shared.Count;
    public static void WritesSharedField() => Counter = 1;
    public static void MutatesLocalDictionary()
    {
        var local = new System.Collections.Generic.Dictionary<int, int>();
        local.Add(1, 2);
    }
    public static int UsesCachedLambda() => System.Linq.Enumerable.Count(new[] { 1 }, value => value > 0);
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
    $routedType = @($inspection.types | Where-Object { $_.name -ceq 'RoutedFixtureCommand' })[0]
    $pure = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'Pure' })[0]
    $main = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'ReadsMainHero' })[0]
    $mutation = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'MutatesRelation' })[0]
    $caller = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'CallsMutationHelper' })[0]
    $sharedMutation = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'MutatesSharedDictionary' })[0]
    $sharedRead = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'ReadsSharedDictionary' })[0]
    $sharedWrite = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'WritesSharedField' })[0]
    $localMutation = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'MutatesLocalDictionary' })[0]
    $cachedLambda = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'UsesCachedLambda' })[0]
    $singleton = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'get_Instance' })[0]
    $singletonSetter = @($inspection.methods | Where-Object { $_.declaringType -ceq 'AuthorityFixture' -and $_.name -ceq 'set_Instance' })[0]

    Assert-True (@($pure.authorityEvidence.directSignals).Count -eq 0) 'pure method was flagged'
    Assert-Contains @($main.authorityEvidence.directSignals) 'global-player:TaleWorlds.CampaignSystem.Hero.get_MainHero' 'MainHero read was not classified'
    Assert-Contains @($mutation.authorityEvidence.directSignals) 'campaign-mutation:TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes' 'campaign mutation was not classified'
    Assert-Contains @($caller.authorityEvidence.transitiveSignals) 'calls-authority-sensitive:AuthorityFixture.MutatesRelation' 'authority-sensitive helper call was not propagated'
    Assert-Contains @($sharedMutation.authorityEvidence.directSignals) 'shared-state-mutation:AuthorityFixture.Shared' 'shared dictionary mutation was not classified'
    Assert-True (@($sharedRead.authorityEvidence.directSignals).Count -eq 0) 'shared dictionary read was flagged as a mutation'
    Assert-Contains @($sharedWrite.authorityEvidence.directSignals) 'shared-state-write:AuthorityFixture.Counter' 'static field write was not classified'
    Assert-True (@($localMutation.authorityEvidence.directSignals).Count -eq 0) 'local dictionary mutation was flagged as shared state'
    Assert-True (@($cachedLambda.authorityEvidence.directSignals).Count -eq 0) 'compiler delegate cache was flagged as shared state'
    Assert-True (@($singleton.authorityEvidence.directSignals).Count -eq 0) 'lazy singleton cache was flagged as shared state'
    Assert-True (@($singletonSetter.authorityEvidence.directSignals).Count -eq 0) 'singleton cache setter was flagged as shared state'
    Assert-Contains @($routedType.interfaces) 'Common.Messaging.ICommand' 'ICommand implementation was not catalogued'
    Assert-True ([string]$routedType.authorityRoute.routeId -ceq 'fixture.command') 'AuthorityRoute route ID was not decoded'
    Assert-True ([int]$routedType.authorityRoute.kind -eq 0) 'AuthorityRoute kind was not decoded'

    Write-Host 'PASS: pure methods remain unflagged'
    Write-Host 'PASS: global player access is classified'
    Write-Host 'PASS: campaign mutation is classified'
    Write-Host 'PASS: authority sensitivity propagates through mod-owned calls'
    Write-Host 'PASS: shared collection and static-field mutation are classified without flagging local collections'
    Write-Host 'PASS: routed ICommand type metadata is decoded without loading the assembly'
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
