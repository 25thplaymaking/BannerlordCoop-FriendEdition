# Mod Authority Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the exact-method authority audit, fail-closed disposition validator, common module declarations, and server capability protocol required before completing individual mod routes.

**Architecture:** Extend the existing metadata inspector with deterministic IL call/member evidence and transitive authority flags. Materialize a checked-in exact-hash/token audit whose release validator rejects unclassified or blocked active gameplay functions. Bring every active gameplay mod under `IWorkshopModule`, then publish typed server capabilities so clients expose only operations with live routes.

**Tech Stack:** C# 10 / .NET 6 and .NET Standard 2.0, `System.Reflection.Metadata`, PowerShell 7, protobuf-net, Autofac, LiteNetLib, xUnit 2.9.3.

## Global Constraints

- Use `C:\Program Files\dotnet\dotnet.exe`; bare `dotnet` resolves to the wrong x86 runtime tree.
- Work only on `25vid/workshop-integration`; keep the stable feed and live gameplay payload unchanged.
- Preserve the exact ten-module Workshop contract and keep RBM retired.
- No production behavior change may be written before its focused test fails for the expected reason.
- Every authority record is keyed by module ID, exact assembly SHA-256, and metadata token.
- `Blocked`, `Unsupported`, `GuardedFeatureBlocked`, `NotAllowed`, unclassified, and placeholder-only dispositions are invalid in release mode.
- Client capability state is advisory for presentation only; the server must independently authorize every request.
- Do not add a generic reflection-based remote invocation framework.

---

### Task 1: Emit deterministic authority evidence from managed IL

**Files:**
- Create: `tools/WorkshopIntegration/AssemblyInspector/AuthoritySignalScanner.cs`
- Modify: `tools/WorkshopIntegration/AssemblyInspector/Program.cs`
- Create: `tools/WorkshopIntegration/tests/Run-AuthorityInspectorTests.ps1`
- Modify: `tools/WorkshopIntegration/tests/Run-Tests.ps1`

**Interfaces:**
- Consumes: `MetadataReader`, `PEReader`, `MethodDefinitionHandle`, and the existing `MethodInspection` records.
- Produces: `AuthorityEvidence(string[] DirectSignals, string[] TransitiveSignals, string[] CalledMembers)` on each `MethodInspection`.

- [ ] **Step 1: Write the failing synthetic-assembly test**

Create a temporary net6.0 fixture from `Run-AuthorityInspectorTests.ps1` containing:

```csharp
namespace TaleWorlds.CampaignSystem { public static class Hero { public static object MainHero => null; } }
namespace TaleWorlds.CampaignSystem.Actions { public static class ChangeRelationAction { public static void ApplyRelationChangeBetweenHeroes(object a, object b, int value) { } } }
public static class AuthorityFixture
{
    public static int Pure(int value) => value + 1;
    public static object ReadsMainHero() => TaleWorlds.CampaignSystem.Hero.MainHero;
    public static void MutatesRelation() => TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes(null, null, 1);
    public static void CallsMutationHelper() => MutatesRelation();
}
```

Invoke the inspector and assert:

```powershell
Assert-True ($pure.authorityEvidence.directSignals.Count -eq 0) 'pure method was flagged'
Assert-Contains $main.authorityEvidence.directSignals 'global-player:TaleWorlds.CampaignSystem.Hero.MainHero'
Assert-Contains $mutation.authorityEvidence.directSignals 'campaign-mutation:TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyRelationChangeBetweenHeroes'
Assert-Contains $caller.authorityEvidence.transitiveSignals 'calls-authority-sensitive:AuthorityFixture.MutatesRelation'
```

- [ ] **Step 2: Run the inspector test and verify RED**

Run:

```powershell
& '.\tools\WorkshopIntegration\tests\Run-AuthorityInspectorTests.ps1'
```

Expected: FAIL because `authorityEvidence` is absent from the inspector result.

- [ ] **Step 3: Implement IL decoding and direct signal classification**

Add `AuthoritySignalScanner` with these exact members:

```csharp
internal sealed record AuthorityEvidence(
    string[] DirectSignals,
    string[] TransitiveSignals,
    string[] CalledMembers);

internal static class AuthoritySignalScanner
{
    internal static IReadOnlyDictionary<int, AuthorityEvidence> Scan(
        PEReader pe,
        MetadataReader metadata);
}
```

Decode one- and two-byte `System.Reflection.Emit.OpCode` values, advance operands by
`OperandType`, and resolve inline method/field/type/string tokens with `MetadataReader`.
Record ordinal-sorted, de-duplicated member names. Direct rules must include:

```csharp
("global-player", "Hero.MainHero", "MobileParty.MainParty", "Clan.PlayerClan", "Agent.Main")
("campaign-mutation", ".Actions.", "MBObjectManager.RegisterObject", "AddBehavior", "AddModel")
("persistence", "IDataStore.SyncData", "System.IO.File", "Newtonsoft.Json")
("randomness", "System.Random", "System.Guid.NewGuid", "DateTime.Now", "DateTime.UtcNow")
```

Propagate signals backward through same-assembly call edges until a fixed point. A propagated
record names the immediate mod-owned callee as `calls-authority-sensitive:<type>.<method>`.

- [ ] **Step 4: Attach evidence to every inspected method**

Change the record to:

```csharp
internal sealed record MethodInspection(
    string DeclaringType,
    string Name,
    string ReturnType,
    string[] ParameterTypes,
    int GenericArity,
    string Attributes,
    string ImplementationAttributes,
    string MetadataToken,
    int RelativeVirtualAddress,
    AuthorityEvidence AuthorityEvidence);
```

Methods without bodies receive empty arrays. Inspector ordering remains declaring type then token.

- [ ] **Step 5: Run RED test to GREEN and the existing Workshop tooling suite**

Run:

```powershell
& '.\tools\WorkshopIntegration\tests\Run-AuthorityInspectorTests.ps1'
& '.\tools\WorkshopIntegration\tests\Run-Tests.ps1'
```

Expected: both scripts exit 0; the synthetic test prints four PASS lines and the existing suite
retains all prior PASS lines.

- [ ] **Step 6: Commit the IL evidence increment**

```powershell
git add tools/WorkshopIntegration/AssemblyInspector tools/WorkshopIntegration/tests
git commit -m "Audit mod authority signals from managed IL"
git push origin 25vid/workshop-integration
```

---

### Task 2: Generate and validate exact method dispositions

**Files:**
- Create: `tools/WorkshopIntegration/authority-dispositions.json`
- Create: `tools/WorkshopIntegration/Generate-AuthorityAudit.ps1`
- Create: `tools/WorkshopIntegration/tests/Validate-AuthorityAudit.ps1`
- Create: `doc/generated/workshop-authority-audit.json`
- Modify: `tools/WorkshopIntegration/tests/Validate-FunctionInventory.ps1`
- Modify: `tools/WorkshopIntegration/README.md`

**Interfaces:**
- Consumes: `doc/generated/workshop-function-inventory.json` with `authorityEvidence` per method.
- Produces: schema-1 `workshop-authority-audit.json` and `Validate-AuthorityAudit.ps1 -Release`.

- [ ] **Step 1: Write failing validator fixtures**

In `Validate-AuthorityAudit.ps1`, expose `Test-AuthorityAudit` and add a `-SelfTest` mode with
three in-memory fixtures:

```powershell
$valid = @{ active=$true; requiresDisposition=$true; disposition='ServerCommand'; owner='Example.Handler'; tests=@('ExampleTests.Routes') }
$unclassified = @{ active=$true; requiresDisposition=$true; disposition='Unclassified'; owner=''; tests=@() }
$blocked = @{ active=$true; requiresDisposition=$true; disposition='Blocked'; owner='Example.Guard'; tests=@('ExampleTests.Blocks') }
```

Assert development mode accepts the valid record and reports the other two, while release mode
throws for both unclassified and blocked records.

- [ ] **Step 2: Run validator self-test and verify RED**

Run:

```powershell
& '.\tools\WorkshopIntegration\tests\Validate-AuthorityAudit.ps1' -SelfTest
```

Expected: FAIL because the validator does not exist.

- [ ] **Step 3: Define the disposition policy schema**

Create `authority-dispositions.json`:

```json
{
  "schemaVersion": 1,
  "allowedDispositions": [
    "ClientPresentation",
    "PurePolicy",
    "ServerCallback",
    "ServerCommand",
    "ReplicatedCosmetic",
    "CoopOwnerReplacement",
    "FrameworkLifecycle",
    "Retired"
  ],
  "rules": []
}
```

Each future rule has exact fields `moduleId`, `assemblySha256`, `metadataTokens`, `disposition`,
`owner`, `capability`, and `tests`. Rules may group tokens only when all grouped functions share the
same semantic owner.

- [ ] **Step 4: Implement generator and validation modes**

`Generate-AuthorityAudit.ps1` must materialize one record per inventoried method with:

```json
{
  "moduleId": "Fourberie",
  "assemblySha256": "...",
  "metadataToken": "0x06000001",
  "declaringType": "Fourberie.Example",
  "method": "Execute",
  "requiresDisposition": true,
  "evidence": [],
  "disposition": "Unclassified",
  "owner": "",
  "capability": "",
  "tests": []
}
```

`requiresDisposition` is true for direct/transitive evidence, Harmony patches, campaign/mission
behaviors, models, event registration/callbacks, persistence types, menu/VM `Execute`/`On*`
entrypoints, or any method named in the existing compatibility manifests. `-Release` rejects active
unclassified/blocked records and missing owner/tests; development mode emits counts without hiding
them.

- [ ] **Step 5: Run self-test to GREEN and generate the development audit**

Run:

```powershell
& '.\tools\WorkshopIntegration\tests\Validate-AuthorityAudit.ps1' -SelfTest
& '.\tools\WorkshopIntegration\Generate-FunctionInventory.ps1'
& '.\tools\WorkshopIntegration\Generate-AuthorityAudit.ps1'
& '.\tools\WorkshopIntegration\tests\Validate-AuthorityAudit.ps1'
```

Expected: self-test and development validation exit 0; output reports exact classified,
unclassified, blocked, and inactive counts. `-Release` must still fail until the later mod plans
close every active record.

- [ ] **Step 6: Commit the audit contract and generated baseline**

```powershell
git add tools/WorkshopIntegration doc/generated/workshop-function-inventory.json doc/generated/workshop-authority-audit.json
git commit -m "Enforce exact mod authority dispositions"
git push origin 25vid/workshop-integration
```

---

### Task 3: Declare every active gameplay module through one contract

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/ImprovedGarrisons/ImprovedGarrisonsModule.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieModule.cs`
- Create: `source/GameInterface/Services/WorkshopMods/PlayerSettlement/PlayerSettlementModule.cs`
- Modify: `source/GameInterface/GameInterfaceModule.cs`
- Modify: `source/Missions/MissionModule.cs`
- Modify: `source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopModuleContractTests.cs`
- Modify: `source/E2E.Tests/Services/WorkshopMods/Core/AbsentModuleSafetyTests.cs`

**Interfaces:**
- Consumes: existing exact manifest validation in each compatibility namespace.
- Produces: three `IWorkshopModule` implementations and the exact six-active-gameplay-module declaration set.

- [ ] **Step 1: Write the failing declaration test**

Add a theory that reflects the private declaration arrays and requires exactly:

```csharp
new[]
{
    "ImprovedGarrisons",
    "Fourberie",
    "Bannerlord.Diplomacy",
    "PlayerSettlement",
    "DismembermentPlus",
    "UnblockableThrust",
}
```

Assert RBM is absent and every declared ID exists in `FriendEditionWorkshopModuleCatalog`.

- [ ] **Step 2: Run declaration test and verify RED**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test source/GameInterface.Tests/GameInterface.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WorkshopModuleContractTests' --consoleLoggerParameters:ErrorsOnly
```

Expected: FAIL naming ImprovedGarrisons, Fourberie, and PlayerSettlement as missing and RBM as extra.

- [ ] **Step 3: Add the three exact module declarations**

Each implementation returns its compatibility manifest's module/workshop/fingerprint values,
returns `null` for `PatchCategory` because its handler patches imperatively, calls the manifest's
exact validation resolver from `ResolveInstalledSha256`, and leaves `RegisterSync` empty because the
existing revisioned snapshot is the sole state owner.

Use this shape:

```csharp
internal sealed class ImprovedGarrisonsModule : IWorkshopModule
{
    public string ModuleId => ImprovedGarrisonsCompatibilityManifest.AssemblyName;
    public ulong WorkshopId => 2859265386UL;
    public ModuleFingerprint Fingerprint { get; } = new(
        ImprovedGarrisonsCompatibilityManifest.AssemblyName,
        ImprovedGarrisonsCompatibilityManifest.SupportedAssemblyVersion,
        ImprovedGarrisonsCompatibilityManifest.SupportedSha256);
    public string PatchCategory => null;
    public string ResolveInstalledSha256() => ImprovedGarrisonsCompatibilityManifest.ResolveInstalledSha256();
    public void RegisterSync(AutoSyncRegistry registry) { }
}
```

Give Fourberie and Player Settlement the same contract with their existing manifest constants.

- [ ] **Step 4: Reconcile composition roots**

Set `GameInterfaceModule.DeclaredWorkshopModules` to the four campaign modules. Remove `RbmModule`
from `MissionModule.DeclaredWorkshopModules`; retain DismembermentPlus and UnblockableThrust. Delete
the source comment describing three declarations as follow-on work.

- [ ] **Step 5: Run focused declaration and absence tests to GREEN**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test source/GameInterface.Tests/GameInterface.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WorkshopModuleContractTests|FullyQualifiedName~WorkshopModuleRegistrarTests' --consoleLoggerParameters:ErrorsOnly
& 'C:\Program Files\dotnet\dotnet.exe' test source/E2E.Tests/E2E.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AbsentModuleSafetyTests' --consoleLoggerParameters:ErrorsOnly
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 6: Commit the unified declarations**

```powershell
git add source/GameInterface source/Missions source/GameInterface.Tests source/E2E.Tests
git commit -m "Declare every active gameplay module"
git push origin 25vid/workshop-integration
```

---

### Task 4: Publish server-owned operation capabilities

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopCapability.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopCapabilityMessages.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopCapabilityRegistry.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopCapabilityHandler.cs`
- Create: `source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopCapabilityTests.cs`
- Create: `source/E2E.Tests/Services/WorkshopMods/Core/WorkshopCapabilityE2ETests.cs`

**Interfaces:**
- Produces: `IWorkshopCapabilitySource`, `IWorkshopCapabilityRegistry`, `NetworkRequestWorkshopCapabilities`, and `NetworkWorkshopCapabilities`.
- Consumes: authenticated transport peer, current host config, declared live modules, and per-mod capability sources added by later plans.

- [ ] **Step 1: Write failing registry tests**

Define the wished-for API in tests:

```csharp
var registry = new WorkshopCapabilityRegistry();
var snapshot = new WorkshopCapabilitySnapshot(
    sessionId: "campaign-a",
    revision: 2,
    capabilities: new[] { new WorkshopCapability("Fourberie", "RecruitBandits", true, "") });

Assert.Equal(WorkshopCapabilityApplyResult.Applied, registry.Apply(snapshot));
Assert.True(registry.IsEnabled("Fourberie", "RecruitBandits"));
Assert.Equal(WorkshopCapabilityApplyResult.AlreadyCurrent, registry.Apply(snapshot));
Assert.Equal(WorkshopCapabilityApplyResult.Stale, registry.Apply(new("campaign-a", 1, [])));
Assert.Equal(WorkshopCapabilityApplyResult.Conflict, registry.Apply(new("campaign-a", 2, [])));
```

Also assert malformed IDs, duplicate `(module, operation)` pairs, excessive counts, and forged local
messages are rejected.

- [ ] **Step 2: Run capability tests and verify RED**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test source/GameInterface.Tests/GameInterface.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WorkshopCapabilityTests' --consoleLoggerParameters:ErrorsOnly
```

Expected: compile failure because capability types do not exist.

- [ ] **Step 3: Implement bounded protobuf contracts and registry**

Use exact public contracts:

```csharp
public sealed class WorkshopCapability
{
    public string ModuleId { get; set; }
    public string Operation { get; set; }
    public bool Enabled { get; set; }
    public string Reason { get; set; }
}

public interface IWorkshopCapabilitySource
{
    IEnumerable<WorkshopCapability> CaptureCapabilities();
}

public interface IWorkshopCapabilityRegistry : IGameAbstraction
{
    bool IsEnabled(string moduleId, string operation);
    WorkshopCapabilityApplyResult Apply(WorkshopCapabilitySnapshot snapshot);
    void Reset();
}
```

Limit module/operation to 128 characters, reason to 512, capabilities to 512, revision to nonnegative,
and require ordinal uniqueness. Snapshot identity is SHA-256 over ordinal-sorted canonical lines.

- [ ] **Step 4: Implement the trusted server handler**

The handler subscribes to `CampaignReady`, `HostModConfigAccepted`, capability request, and snapshot
messages. The server captures sources only after both campaign/config barriers, publishes revision 0,
and replies only to registered peers. A client accepts only its config authority's trusted server
transport; invalid/conflicting snapshots disconnect. Locally published snapshots are ignored.

- [ ] **Step 5: Write and run E2E origin/convergence tests**

Cover server broadcast, late request, forged local origin, stale revision, same-revision conflict,
malformed entry, and two-client identical capability lookup. Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test source/E2E.Tests/E2E.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WorkshopCapabilityE2ETests' --consoleLoggerParameters:ErrorsOnly
```

Expected RED before handler implementation and GREEN after it.

- [ ] **Step 6: Run focused capability suites to GREEN**

Run both focused GameInterface and E2E commands. Expected: zero failed tests.

- [ ] **Step 7: Commit the capability protocol**

```powershell
git add source/GameInterface source/GameInterface.Tests source/E2E.Tests
git commit -m "Publish server-owned mod capabilities"
git push origin 25vid/workshop-integration
```

---

### Task 5: Correct the contract documentation and verify the foundation

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/Core/README.md`
- Modify: `doc/WorkshopFunctionReview.md`
- Modify: `STATUS.md`
- Modify: `doc/CHANGELOG.md`

**Interfaces:**
- Consumes: generated authority audit counts and the exact declared module set.
- Produces: one non-contradictory development status with stable still held.

- [ ] **Step 1: Replace the stale 11-module/RBM contract**

Document the exact ten managed Workshop modules, six active gameplay module declarations, integrated
Separatism, the authority dispositions, and development-vs-release validator commands. Remove claims
that blocked behavior is equivalent to integration.

- [ ] **Step 2: Record foundation evidence without claiming feature completion**

Update status/changelog with exact authority candidate/classification counts, test commands, and the
fact that per-mod blocked routes remain open until their later plans pass. Keep the existing RC
non-stable and superseded for full-feature purposes.

- [ ] **Step 3: Run the complete foundation gate**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build source/CoopTests.slnf -c Release -p:NuGetAudit=false --nologo --consoleLoggerParameters:ErrorsOnly
& 'C:\Program Files\dotnet\dotnet.exe' test source/CoopUnitTests.slnf -c Release --no-build --no-restore --nologo --consoleLoggerParameters:ErrorsOnly
& '.\tools\WorkshopIntegration\tests\Run-Tests.ps1'
& '.\tools\WorkshopIntegration\tests\Validate-FunctionInventory.ps1'
& '.\tools\WorkshopIntegration\tests\Validate-AuthorityAudit.ps1'
git diff --check
```

Expected: build and all tests exit 0; development authority validation prints the remaining per-mod
work; release authority validation is not claimed yet.

- [ ] **Step 4: Commit and push the foundation milestone**

```powershell
git add source/GameInterface/Services/WorkshopMods/Core/README.md doc/WorkshopFunctionReview.md STATUS.md doc/CHANGELOG.md
git commit -m "Record mod authority foundation"
git push origin 25vid/workshop-integration
```
