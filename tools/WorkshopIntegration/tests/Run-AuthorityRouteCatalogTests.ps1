#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$generator = Join-Path $repoRoot 'tools\WorkshopIntegration\Generate-AuthorityRouteCatalog.ps1'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('friend-edition-route-catalog-tests-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($module in @('GameInterface', 'Coop.Core')) {
        $projectRoot = Join-Path $testRoot "source\$module"
        Write-Utf8File (Join-Path $projectRoot "$module.csproj") @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>$module</AssemblyName></PropertyGroup>
</Project>
"@
    }
    Write-Utf8File (Join-Path $testRoot 'source\GameInterface\Routes.cs') @'
namespace Common.Messaging {
  public interface ICommand { }
  public enum AuthorityRouteKind { Command = 0, BootstrapQuery = 1 }
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
  public sealed class AuthorityRouteAttribute : System.Attribute { public AuthorityRouteAttribute(string id, AuthorityRouteKind kind) { } }
}
public sealed class IntentA { }
[Common.Messaging.AuthorityRoute("fixture.command", Common.Messaging.AuthorityRouteKind.Command)]
public sealed class RequestA : Common.Messaging.ICommand { }
public sealed class ResultA { }
public static class AuthorityRoute<TIntent,TRequest,TResult> { public static void Define() { } }
public sealed class OwnerA { public void Register() { AuthorityRoute<IntentA, RequestA, ResultA>.Define(); } }
public interface IWorkshopCapabilitySource { }
public enum WorkshopSnapshotReadiness { Unknown, Ready }
public sealed class FixtureCapabilitySource : IWorkshopCapabilitySource {
  public string SessionId => "session";
  public System.Collections.Generic.IEnumerable<int> CaptureCapabilities() {
    bool routeReady = IsRegistered("fixture.command", Common.Messaging.AuthorityRouteKind.Command);
    bool current = WorkshopSnapshotReadiness.Ready == WorkshopSnapshotReadiness.Ready && SessionId != null;
    yield return routeReady && current ? 1 : 0;
  }
  private bool IsRegistered(string route, Common.Messaging.AuthorityRouteKind kind) => true;
}
'@
    Write-Utf8File (Join-Path $testRoot 'source\Coop.Core\Routes.cs') @'
namespace Common.Messaging {
  public interface ICommand { }
  public enum AuthorityRouteKind { Command = 0, BootstrapQuery = 1 }
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
  public sealed class AuthorityRouteAttribute : System.Attribute { public AuthorityRouteAttribute(string id, AuthorityRouteKind kind) { } }
}
public sealed class IntentB { }
[Common.Messaging.AuthorityRoute("fixture.bootstrap", Common.Messaging.AuthorityRouteKind.BootstrapQuery)]
public sealed class RequestB : Common.Messaging.ICommand { }
public sealed class ResultB { }
public static class AuthorityRoute<TIntent,TRequest,TResult> { public static void Define() { } }
public sealed class OwnerB { public void Register() { AuthorityRoute<IntentB, RequestB, ResultB>.Define(); } }
public sealed class NetworkRequestInternalProbe : Common.Messaging.ICommand { }
'@
    Write-Utf8File (Join-Path $testRoot 'source\Fixture.Tests\RouteContractTests.cs') @'
public sealed class RouteContractTests {
  public void CommandRoute() { var request = new RequestA(); var route = "fixture.command"; }
  public void BootstrapRoute() { var request = new RequestB(); var route = "fixture.bootstrap"; }
  public void InternalProbe() { var request = new NetworkRequestInternalProbe(); }
}
'@
    foreach ($module in @('GameInterface', 'Coop.Core')) {
        & $dotnet build (Join-Path $testRoot "source\$module\$module.csproj") -c Release --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "Fixture $module build failed." }
    }
    Write-Utf8File (Join-Path $testRoot 'tools\WorkshopIntegration\authority-dispositions.json') '{"schemaVersion":1,"rules":[]}'
    Write-Utf8File (Join-Path $testRoot 'tools\WorkshopIntegration\authority-route-audit-policy.json') @'
{
  "schemaVersion": 1,
  "allowedMessageDispositions": ["Command", "BootstrapQuery", "Replication", "Internal"],
  "messageDispositions": [
    { "messageType": "NetworkRequestInternalProbe", "disposition": "Internal", "reason": "fixture-local-control-message", "owner": "NetworkRequestInternalProbe", "tests": ["RouteContractTests.InternalProbe"] }
  ],
  "bypassExemptions": []
}
'@
    $output = Join-Path $testRoot 'doc\generated\authority-route-catalog.json'
    & $generator -RepoRoot $testRoot -AssemblyPaths @(
        'source\GameInterface\bin\Release\net8.0\GameInterface.dll',
        'source\Coop.Core\bin\Release\net8.0\Coop.Core.dll') -InspectorProjectPath (Join-Path $repoRoot 'tools\WorkshopIntegration\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj') -OutputPath $output -Release
    if ($LASTEXITCODE -ne 0) { throw 'Valid route catalog fixture failed.' }
    $catalog = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    Assert-True ([int]$catalog.summary.routeCount -eq 2) 'compiled route catalog did not include both assemblies'
    Assert-True ([int]$catalog.summary.issueCount -eq 0) 'valid route catalog reported issues'
    Assert-True ([int]$catalog.summary.capabilitySourceCount -eq 1) 'current-session Ready capability source was not validated'
    Assert-True (@($catalog.routes | Where-Object { [string]$_.routeId -ceq 'fixture.command' -and [string]$_.resultTypes[0] -ceq 'ResultA' }).Count -eq 1) 'typed request/result contract was not recovered'
    Assert-True (@($catalog.messageDispositions | Where-Object { [string]$_.messageType -ceq 'NetworkRequestInternalProbe' -and [string]$_.disposition -ceq 'Internal' }).Count -eq 1) 'checked Internal request disposition was not applied'

    $invalidPolicy = Join-Path $testRoot 'tools\WorkshopIntegration\invalid-route-audit-policy.json'
    Write-Utf8File $invalidPolicy @'
{
  "schemaVersion": 1,
  "allowedMessageDispositions": ["Command", "BootstrapQuery", "Replication", "Internal"],
  "messageDispositions": [
    { "messageType": "NetworkRequestInternalProbe", "disposition": "Internal", "reason": "fixture-local-control-message", "owner": "NetworkRequestInternalProbe", "tests": ["RouteContractTests.MissingFocusedTest"] }
  ],
  "bypassExemptions": []
}
'@
    $missingEvidenceRejected = $false
    try {
        & $generator -RepoRoot $testRoot -AssemblyPaths @(
            'source\GameInterface\bin\Release\net8.0\GameInterface.dll',
            'source\Coop.Core\bin\Release\net8.0\Coop.Core.dll') -RoutePolicyPath $invalidPolicy -InspectorProjectPath (Join-Path $repoRoot 'tools\WorkshopIntegration\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj') -OutputPath $output -Release
    }
    catch { $missingEvidenceRejected = $_.Exception.Message.Contains('Unknown message disposition test') }
    Assert-True $missingEvidenceRejected 'message disposition accepted a nonexistent focused test'

    Add-Content -LiteralPath (Join-Path $testRoot 'source\GameInterface\Routes.cs') -Value @'
public sealed class LegacyBypass { public void Wire(MessageBroker broker) { broker.Subscribe<RequestA>(); } }
public sealed class MessageBroker { public void Subscribe<T>() { } }
'@
    & $dotnet build (Join-Path $testRoot 'source\GameInterface\GameInterface.csproj') -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Bypass fixture rebuild failed.' }
    $bypassRejected = $false
    try {
        & $generator -RepoRoot $testRoot -AssemblyPaths @(
            'source\GameInterface\bin\Release\net8.0\GameInterface.dll',
            'source\Coop.Core\bin\Release\net8.0\Coop.Core.dll') -InspectorProjectPath (Join-Path $repoRoot 'tools\WorkshopIntegration\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj') -OutputPath $output -Release
    }
    catch { $bypassRejected = $_.Exception.Message.Contains('bypasses AuthorityRequestRouter') }
    Assert-True $bypassRejected 'release audit accepted a direct routed-request subscription'

    Write-Host 'PASS: compiled routes reconcile across GameInterface and Coop.Core with typed owners/results/tests'
    Write-Host 'PASS: request taxonomy applies explicit Internal dispositions'
    Write-Host 'PASS: request taxonomy rejects nonexistent focused evidence'
    Write-Host 'PASS: release audit rejects direct routed-command bypasses'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $safeTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $safeTarget = [IO.Path]::GetFullPath($testRoot)
        if (-not $safeTarget.StartsWith($safeTemp, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $safeTarget) -notlike 'friend-edition-route-catalog-tests-*') { throw "Refusing unsafe test cleanup: $safeTarget" }
        Remove-Item -LiteralPath $safeTarget -Recurse -Force
    }
}
exit 0
