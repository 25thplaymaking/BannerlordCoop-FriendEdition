# Workshop Module Integration — Implementation Plan (Increments 0 & 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the `workshop-integration` branch loadable and green (increment 0), then give it a reusable module contract proven by wiring Diplomacy's state through Coop's own replication (increment 1).

**Architecture:** No new transport. Module state replicates through `IAutoSync`; module intent uses Coop's existing patch → internal message → client handler → server handler → interface shape; gating uses `HarmonyPatchCategoryRegistration` plus `ModConfigAuthority`. A module that is absent, fingerprint-mismatched, or disabled contributes zero Harmony patches.

**Tech Stack:** C# / .NET Framework 4.7.2 + net6.0 test assemblies, HarmonyLib, Autofac, protobuf-net, xunit 2.9.3.

## Global Constraints

- Work in `C:\Users\Bryce\Documents\ServerWork\workshop-integration` on branch `25vid/workshop-integration`.
- Build with the x64 SDK: `"C:\Program Files\dotnet\dotnet.exe"`. Bare `dotnet` resolves to a runtimes-only x86 tree.
- Build tests with `build source/CoopTests.slnf -c Release`. Building a single test csproj directly fails on NuGet audit errors (NU1903/NU1902 for Scriban 7.2.0).
- **`dotnet test` does not work on this machine** — IPv4 loopback is broken system-wide, so vstest.console never connects to its testhost. Use the xunit in-process console runner (setup in Task 0).
- Run every suite with `-parallel none`. `E2E.Tests` disables parallelisation by attribute; the others need the flag.
- Never run the whole `E2E.Tests` assembly in one process as a pass/fail signal — it is order-unstable (a baseline run produced 114 failures where a scoped run produced 0). Scope to a namespace or class.
- CI (`pull_request.yml`, 8 E2E shards) is the gate of record. A push alone triggers no workflow.
- The `Nightly Release` workflow is disabled and stays disabled.
- Frameworks (UIExtenderEx, ButterLib, MCM) stay staged-inactive. Do not route them.

---

### Task 0: Set up the working test runner

**Files:**
- Create: none (tooling only)

**Interfaces:**
- Produces: the `RUNNER` invocation every later task uses to run tests.

- [ ] **Step 1: Build the test solution filter**

```bash
cd /c/Users/Bryce/Documents/ServerWork/workshop-integration
"/c/Program Files/dotnet/dotnet.exe" build source/CoopTests.slnf -c Release -v q --nologo
```

Expected: `0 Error(s)`.

- [ ] **Step 2: Copy the xunit console runner beside each test assembly**

```bash
for p in GameInterface.Tests E2E.Tests Coop.Tests Coop.IntegrationTests; do
  d=$(find source/$p/bin -path "*Release/net6.0/$p.dll" | head -1)
  cp ~/.nuget/packages/xunit.runner.console/2.9.3/tools/net6.0/xunit.console.* \
     ~/.nuget/packages/xunit.runner.console/2.9.3/tools/net6.0/xunit.runner.*.dll \
     "$(dirname "$d")/"
done
```

- [ ] **Step 3: Verify the runner works**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -namespace "GameInterface.Tests.Services.WorkshopMods"
```

Expected: `Total: 233, Errors: 0, Failed: 6`. Those six are the subject of Tasks 2–5.

Note: these runner binaries are build output and are not committed. Re-copy after any clean.

---

### Task 1: Stop one absent mod from aborting all of Coop's patching

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopPatchCategories.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityPatches.cs:25`
- Modify: `source/GameInterface/GameInterfaceModule.cs`
- Test: `source/E2E.Tests/Services/WorkshopMods/Core/AbsentModuleSafetyTests.cs`

**Interfaces:**
- Produces: `WorkshopPatchCategories.Diplomacy` (const string), consumed by Task 6 and Task 8.

**Background:** `DiplomacySharedMutationAuthorityPatch.TargetMethods()` returns
`DiplomacyCompatibilityPolicy.ResolveSharedMutationMethods()`, which yields nothing when Diplomacy
is not loaded. `Harmony.PatchAllUncategorized` throws `ArgumentException: Undefined target method`
inside `GameInterface.PatchAll()`, aborting every remaining patch — AutoSync included. Categorising
the class removes it from `PatchAllUncategorized`; the category is then applied only when the
assembly resolves.

- [ ] **Step 1: Write the failing test**

```csharp
using E2E.Tests.Environment;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Core;

public sealed class AbsentModuleSafetyTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }

    public AbsentModuleSafetyTests(ITestOutputHelper output)
    {
        // Constructing the environment runs GameInterface.PatchAll() on every instance.
        // With no Workshop mod installed in the harness, that must not throw.
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void PatchAll_WithNoWorkshopModulesInstalled_DoesNotThrow()
    {
        Assert.NotNull(TestEnvironment.Server);
        Assert.NotEmpty(TestEnvironment.Clients);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

```bash
cd source/E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none \
  -class "E2E.Tests.Services.WorkshopMods.Core.AbsentModuleSafetyTests"
```

Expected: FAIL — `HarmonyException: Undefined target method for patch method ... DiplomacySharedMutationAuthorityPatch::Prefix`.

- [ ] **Step 3: Add the category constants**

```csharp
namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Harmony patch categories for Workshop module adapters. Every adapter patch class MUST carry one.
/// An uncategorised adapter patch is applied by Harmony.PatchAllUncategorized during
/// GameInterface.PatchAll(), and a TargetMethods() that resolves nothing — which is exactly what
/// happens when the mod is not installed — throws there and aborts EVERY remaining Coop patch,
/// AutoSync included. Categories let the registrar apply an adapter only when its module is live.
/// </summary>
internal static class WorkshopPatchCategories
{
    internal const string Diplomacy = "CoopWorkshopDiplomacyPatches";
    internal const string ImprovedGarrisons = "CoopWorkshopImprovedGarrisonsPatches";
    internal const string Fourberie = "CoopWorkshopFourberiePatches";
    internal const string PlayerSettlement = "CoopWorkshopPlayerSettlementPatches";
}
```

- [ ] **Step 4: Categorise the offending patch class**

In `DiplomacyCompatibilityPatches.cs`, add the attribute above `internal static class DiplomacySharedMutationAuthorityPatch` (line 25) and the matching using:

```csharp
using GameInterface.Services.WorkshopMods.Core;

[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacySharedMutationAuthorityPatch
```

Apply the same attribute to every other patch class in this file whose `TargetMethods()` resolves through `DiplomacyCompatibilityPolicy`.

- [ ] **Step 5: Register the category only when Diplomacy resolves**

In `GameInterfaceModule.cs`, alongside the existing `HarmonyPatchCategoryRegistration` registrations:

```csharp
// Registered ONLY when the assembly resolves. Applying a category whose patch classes have no
// resolvable targets throws exactly like the uncategorised path did.
if (DiplomacyCompatibilityPolicy.ResolveAssembly() != null)
{
    builder.RegisterInstance(new HarmonyPatchCategoryRegistration(
        typeof(GameInterface).Assembly,
        WorkshopPatchCategories.Diplomacy));
}
```

- [ ] **Step 6: Run the test to verify it passes**

Same command as Step 2. Expected: `Total: 1, Failed: 0`.

- [ ] **Step 7: Verify the four previously-blocked authority E2E tests now run**

```bash
cd source/E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none -namespace "E2E.Tests.Services.WorkshopMods"
```

Expected: the `HarmonyException` is gone from all five. Failures that remain are now genuine
assertion failures about authority behaviour — record their messages; they belong to Task 6.

- [ ] **Step 8: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/Core/WorkshopPatchCategories.cs \
        source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityPatches.cs \
        source/GameInterface/GameInterfaceModule.cs \
        source/E2E.Tests/Services/WorkshopMods/Core/AbsentModuleSafetyTests.cs
git commit -m "Stop an absent Workshop mod from aborting Coop's patch application"
```

---

### Task 2: Audit and categorise the three unverified sibling patch classes

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/ImprovedGarrisons/ImprovedGarrisonsAuthorityPatches.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieAuthority.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/PlayerSettlement/PlayerSettlementAuthority.cs`
- Modify: `source/GameInterface/GameInterfaceModule.cs`

**Interfaces:**
- Consumes: `WorkshopPatchCategories` from Task 1.

**Background:** Harmony aborted at the first failing class, so these three were never reached. They
are built the same way and must be assumed to share the flaw.

- [ ] **Step 1: Confirm each class has a dynamic TargetMethods**

```bash
grep -n "TargetMethods\|HarmonyPatchCategory" \
  source/GameInterface/Services/WorkshopMods/ImprovedGarrisons/ImprovedGarrisonsAuthorityPatches.cs \
  source/GameInterface/Services/WorkshopMods/Fourberie/FourberieAuthority.cs \
  source/GameInterface/Services/WorkshopMods/PlayerSettlement/PlayerSettlementAuthority.cs
```

Any class with a `TargetMethods()` and no `HarmonyPatchCategory` has the defect.

- [ ] **Step 2: Add the matching category attribute to each affected class**

For each, add `using GameInterface.Services.WorkshopMods.Core;` and the attribute, e.g.:

```csharp
[HarmonyPatchCategory(WorkshopPatchCategories.ImprovedGarrisons)]
internal static class ImprovedGarrisonsAuthorityPatches
```

- [ ] **Step 3: Register each category conditionally in `GameInterfaceModule.cs`**

Mirror Task 1 Step 5, using each module's own policy resolver:

```csharp
if (ImprovedGarrisonsCompatibilityPolicy.ResolveAssembly() != null)
{
    builder.RegisterInstance(new HarmonyPatchCategoryRegistration(
        typeof(GameInterface).Assembly,
        WorkshopPatchCategories.ImprovedGarrisons));
}
```

If a module's policy class exposes a differently named resolver, use that name — check with
`grep -n "internal static Assembly Resolve" source/GameInterface/Services/WorkshopMods/<Module>/*.cs`.

- [ ] **Step 4: Re-run the absent-module test**

```bash
cd source/E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none \
  -class "E2E.Tests.Services.WorkshopMods.Core.AbsentModuleSafetyTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods source/GameInterface/GameInterfaceModule.cs
git commit -m "Categorise the remaining Workshop adapter patch classes"
```

---

### Task 3: Let Moq proxy the internal module runtime interfaces

**Files:**
- Modify: `source/GameInterface/Properties/AssemblyInfo.cs:34`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityTests.cs:418` (existing test, no edit)

**Background:** `DiplomacyCompatibilityTests.AuthoritativePublisher_SendsEachChangedRevisionOnce`
fails with `Cannot set up IDiplomacyRuntime.get_IsAvailable because it is not accessible to the
proxy generator used by Moq`. `IDiplomacyRuntime` is `internal`; Castle's dynamic proxy needs the
assembly to grant access to `DynamicProxyGenAssembly2`.

- [ ] **Step 1: Run the failing test**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -method "GameInterface.Tests.Services.WorkshopMods.Diplomacy.DiplomacyCompatibilityTests.AuthoritativePublisher_SendsEachChangedRevisionOnce"
```

Expected: FAIL with the Moq accessibility `ArgumentException`.

- [ ] **Step 2: Grant the proxy generator access**

Append to `source/GameInterface/Properties/AssemblyInfo.cs`:

```csharp
// Moq/Castle generate proxies for internal interfaces (IDiplomacyRuntime and the other module
// runtime seams) in a dynamic assembly. GameInterface is not strong-named, so the plain name works.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
```

- [ ] **Step 3: Rebuild and re-run**

```bash
cd /c/Users/Bryce/Documents/ServerWork/workshop-integration
"/c/Program Files/dotnet/dotnet.exe" build source/CoopTests.slnf -c Release -v q --nologo
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -method "GameInterface.Tests.Services.WorkshopMods.Diplomacy.DiplomacyCompatibilityTests.AuthoritativePublisher_SendsEachChangedRevisionOnce"
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add source/GameInterface/Properties/AssemblyInfo.cs
git commit -m "Let Moq proxy internal Workshop module runtime interfaces"
```

---

### Task 4: Fix managed-distribution discovery in the suite receipt

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/Core/WorkshopSuiteReceipt.cs`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopSuiteReceiptTests.cs` (existing, no edit)

**Background:** `RuntimeDiscovery_AcceptsManagedSeparateModules_AndTracksInactiveVisualPackage`
fails `Assert.All` on 6 of 11 items. Each failing item has `ManagedDistributionComponent = False`
where the test expects `True` — including `Bannerlord.ButterLib`, `Bannerlord.UIExtenderEx` and
`Bannerlord.MBOptionScreen`. Discovery is not classifying managed framework modules as distribution
components.

- [ ] **Step 1: Reproduce and capture the full failure**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -method "GameInterface.Tests.Services.WorkshopMods.Core.WorkshopSuiteReceiptTests.RuntimeDiscovery_AcceptsManagedSeparateModules_AndTracksInactiveVisualPackage"
```

Record which 6 of the 11 fail and what each `ModuleId` is.

- [ ] **Step 2: Read the test's expectation and the production rule**

```bash
grep -n "ManagedDistributionComponent" -B 5 -A 10 \
  source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopSuiteReceiptTests.cs \
  source/GameInterface/Services/WorkshopMods/Core/WorkshopSuiteReceipt.cs
```

Decide which side is wrong. The test encodes the intended contract — a module that ships managed
assemblies we pin is a distribution component regardless of whether its *feature* is active on
either role (`FeatureActiveExpectedOnClient/Server = False` for all six failing items). If the
production rule conflates "feature active" with "is a distribution component", the production rule
is wrong; fix it there and leave the test alone.

- [ ] **Step 3: Apply the fix and re-run**

Same command as Step 1. Expected: PASS.

- [ ] **Step 4: Run the whole Core namespace for regressions**

```bash
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -namespace "GameInterface.Tests.Services.WorkshopMods.Core"
```

Expected: only the known protobuf failure remains (Task 6 confirms it is environmental).

- [ ] **Step 5: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/Core/WorkshopSuiteReceipt.cs
git commit -m "Classify managed framework modules as distribution components"
```

---

### Task 5: Fix the two order-dependent ImprovedGarrisons tests

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/ImprovedGarrisons/` (file determined in Step 2)
- Test: `source/GameInterface.Tests/Services/WorkshopMods/ImprovedGarrisons/ImprovedGarrisonsCompatibilityTests.cs:89,204`

**Background:** Both failures are shaped like shared-state leakage between tests:

- `PreinstalledModuleHarmonyPatch_IsRemovedAndPostPurgeAssertionIsEmpty` (line 89) —
  `Assert.Single() Failure: The collection was empty`.
- `CachedAdapterInventory_ReassertsExactTargetsAndRejectsUnknownRuntimeOverlap` (line 204) —
  `Assert.Throws() Failure: No exception was thrown`.

- [ ] **Step 1: Determine whether they are order-dependent**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
# In isolation:
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll GameInterface.Tests.dll \
  -noshadow -parallel none \
  -method "GameInterface.Tests.Services.WorkshopMods.ImprovedGarrisons.ImprovedGarrisonsCompatibilityTests.PreinstalledModuleHarmonyPatch_IsRemovedAndPostPurgeAssertionIsEmpty"
# Whole class:
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll GameInterface.Tests.dll \
  -noshadow -parallel none \
  -class "GameInterface.Tests.Services.WorkshopMods.ImprovedGarrisons.ImprovedGarrisonsCompatibilityTests"
```

If isolation passes and the class fails, the cause is leaked static state — the adapter inventory
cache is the prime suspect (the second test's name says "Cached").

- [ ] **Step 2: Find the cache and its reset seam**

```bash
grep -rn "static.*cache\|Cached\|Reset\|Clear()" \
  source/GameInterface/Services/WorkshopMods/ImprovedGarrisons/*.cs | head -20
```

- [ ] **Step 3: Fix the leakage**

Prefer an explicit reset seam the test can call in its constructor over making production state
per-instance, unless per-instance is clearly correct. If production caches an inventory that must
be reasserted per campaign, an `internal static void ResetForTests()` mirrors what
`DefaultMobilePartyAIModelPatches.ResetPersistedAttackProtections()` already does in this codebase.

- [ ] **Step 4: Verify both orders pass**

Re-run both commands from Step 1. Expected: PASS in isolation and as a class.

- [ ] **Step 5: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/ImprovedGarrisons source/GameInterface.Tests/Services/WorkshopMods/ImprovedGarrisons
git commit -m "Reset ImprovedGarrisons adapter inventory between tests"
```

---

### Task 6: Confirm the protobuf failures are environmental, then close increment 0

**Files:**
- Modify: `doc/WorkshopModIntegrationAudit.md`

**Background:** `WorkshopCompatibilityManifestTests.Protobuf_RoundTrip_PreservesAndValidatesManifest`
and `PlayerSettlementCompatibilityTests.EmptyLateJoinSnapshot_ProtobufRoundTripsAndValidates` fail
under the console runner. Unrelated `Serialization` suites fail the same way on this machine with
and without any source change, which is why they are suspected environmental rather than real.

- [ ] **Step 1: Prove it by control**

```bash
cd /c/Users/Bryce/Documents/ServerWork/workshop-integration
git stash push -- source
"/c/Program Files/dotnet/dotnet.exe" build source/CoopTests.slnf -c Release -v q --nologo
cd source/Common.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  Common.Tests.dll -noshadow -parallel none
cd /c/Users/Bryce/Documents/ServerWork/workshop-integration && git stash pop
```

If `Common.Tests` — which this branch does not touch — fails its serialization test identically,
the cause is the runner, not the code.

- [ ] **Step 2: Confirm on CI, which is the gate of record**

Push the branch and open a draft PR so `pull_request.yml` runs both suites under vstest:

```bash
git push origin 25vid/workshop-integration
gh pr create --repo 25thplaymaking/BannerlordCoop-FriendEdition \
  --base development --head 25vid/workshop-integration --draft \
  --title "Workshop module integration: increment 0" \
  --body "Increment 0 of doc/specs/2026-08-09-workshop-module-integration-design.md. Draft: CI verification of the protobuf round-trip tests that fail only under the local console runner."
```

If they pass on CI, they are environmental. If they fail on CI, they are real and get their own
task before increment 1 starts.

- [ ] **Step 3: Record the outcome in the audit**

Replace the "executed runtime-test count is zero" claim in
`doc/WorkshopModIntegrationAudit.md` with the actual figures, the runner used, and the CI verdict
on the two protobuf tests.

- [ ] **Step 4: Full workshop suite green**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none -namespace "GameInterface.Tests.Services.WorkshopMods"
cd ../../../../E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none -namespace "E2E.Tests.Services.WorkshopMods"
```

Expected: 233 and 52 respectively, 0 failed (excluding any protobuf tests CI proved environmental).

- [ ] **Step 5: Commit**

```bash
git add doc/WorkshopModIntegrationAudit.md
git commit -m "Record first executed test results for the Workshop integration"
```

---

### Task 7: Define the module contract

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Core/IWorkshopModule.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Core/ModuleFingerprint.cs`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopModuleContractTests.cs`

**Interfaces:**
- Produces: `IWorkshopModule`, `ModuleFingerprint` — consumed by Tasks 8, 9, 10.

- [ ] **Step 1: Write the failing test**

```csharp
using GameInterface.Services.WorkshopMods.Core;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopModuleContractTests
{
    private sealed class StubModule : IWorkshopModule
    {
        public string ModuleId => "Stub.Module";
        public ulong WorkshopId => 1234567890UL;
        public ModuleFingerprint Fingerprint =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", new string('a', 64));
        public string PatchCategory => "CoopWorkshopStubPatches";
        public void RegisterSync(GameInterface.AutoSync.AutoSyncRegistry registry) { }
    }

    [Fact]
    public void Fingerprint_RejectsAnInvalidSha256()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", "not-a-hash"));
    }

    [Fact]
    public void Module_ExposesItsIdentityAndPatchCategory()
    {
        IWorkshopModule module = new StubModule();

        Assert.Equal("Stub.Module", module.ModuleId);
        Assert.Equal(1234567890UL, module.WorkshopId);
        Assert.Equal("CoopWorkshopStubPatches", module.PatchCategory);
        Assert.Equal("Stub.Assembly", module.Fingerprint.AssemblyName);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -class "GameInterface.Tests.Services.WorkshopMods.Core.WorkshopModuleContractTests"
```

Expected: build failure — `IWorkshopModule` does not exist.

- [ ] **Step 3: Write `ModuleFingerprint`**

```csharp
using System;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// The exact bytes a module is pinned to. Identity is by content, not by version string: a Workshop
/// update that keeps its version but changes its assembly must not silently satisfy the pin.
/// </summary>
public sealed class ModuleFingerprint
{
    public string AssemblyName { get; }
    public string AssemblyVersion { get; }
    public string Sha256 { get; }

    public ModuleFingerprint(string assemblyName, string assemblyVersion, string sha256)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("Assembly name is required", nameof(assemblyName));
        if (string.IsNullOrWhiteSpace(assemblyVersion))
            throw new ArgumentException("Assembly version is required", nameof(assemblyVersion));
        if (sha256 == null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA-256 must be 64 hex characters", nameof(sha256));

        AssemblyName = assemblyName;
        AssemblyVersion = assemblyVersion;
        Sha256 = sha256;
    }
}
```

- [ ] **Step 4: Write `IWorkshopModule`**

```csharp
using GameInterface.AutoSync;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Everything Coop needs to know to integrate one Workshop module. Implementing this is the entire
/// surface a new mod requires: the registrar handles presence, fingerprinting, config gating, patch
/// application and sync registration on its behalf.
/// </summary>
public interface IWorkshopModule
{
    string ModuleId { get; }
    ulong WorkshopId { get; }
    ModuleFingerprint Fingerprint { get; }

    /// <summary>Harmony category holding this module's adapter patches. Applied only when live.</summary>
    string PatchCategory { get; }

    /// <summary>
    /// Declare the module members whose changes replicate. Members are resolved reflectively from
    /// the mod assembly, so this runs only after presence and fingerprint have been confirmed.
    /// </summary>
    void RegisterSync(AutoSyncRegistry registry);

    // NOTE: the design's contract also carries `IEnumerable<ModuleAction> Actions` for inbound
    // intent. It is deliberately absent here — ModuleAction cannot be designed honestly before one
    // real action has been routed. It is added, with its first consumer, in the follow-on plan.
}
```

- [ ] **Step 5: Run the test to verify it passes**

Same command as Step 2. Expected: `Total: 2, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/Core/IWorkshopModule.cs \
        source/GameInterface/Services/WorkshopMods/Core/ModuleFingerprint.cs \
        source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopModuleContractTests.cs
git commit -m "Add the Workshop module contract"
```

---

### Task 8: Config-gated module registrar

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Core/WorkshopModuleRegistrar.cs`
- Modify: `source/GameInterface/Configuration/ModConfigData.cs`
- Modify: `deploy/mod-config.default.json`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopModuleRegistrarTests.cs`

**Interfaces:**
- Consumes: `IWorkshopModule`, `ModuleFingerprint` (Task 7); `WorkshopModuleCatalog` (existing).
- Produces: `WorkshopModuleRegistrar.ResolveLiveModules(IEnumerable<IWorkshopModule>, ModOptions, IWorkshopModuleCatalog) → IReadOnlyList<IWorkshopModule>` — consumed by Task 9 and by `GameInterfaceModule`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Module_DisabledInConfig_IsNotLive()
{
    var module = new StubModule();
    var options = OptionsWith(enabled: false);
    var catalog = CatalogContaining(module.ModuleId, module.Fingerprint.Sha256);

    var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, options, catalog);

    Assert.Empty(live);
}

[Fact]
public void Module_EnabledButFingerprintMismatched_IsNotLive()
{
    var module = new StubModule();
    var options = OptionsWith(enabled: true);
    var catalog = CatalogContaining(module.ModuleId, new string('b', 64));

    var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, options, catalog);

    Assert.Empty(live);
}

[Fact]
public void Module_EnabledAndMatching_IsLive()
{
    var module = new StubModule();
    var options = OptionsWith(enabled: true);
    var catalog = CatalogContaining(module.ModuleId, module.Fingerprint.Sha256);

    var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, options, catalog);

    Assert.Same(module, Assert.Single(live));
}
```

Write `StubModule`, `OptionsWith` and `CatalogContaining` as private helpers in the test class,
modelled on the existing `WorkshopManifestValidatorTests` fixtures.

- [ ] **Step 2: Run it to confirm it fails**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none \
  -class "GameInterface.Tests.Services.WorkshopMods.Core.WorkshopModuleRegistrarTests"
```

Expected: build failure — `WorkshopModuleRegistrar` does not exist.

- [ ] **Step 3: Add the config shape**

In `ModConfigData.cs`, add a per-module enable map alongside the existing options:

```csharp
/// <summary>
/// Per-Workshop-module enablement, keyed by ModuleId. Absent or false means the module contributes
/// no patches and no sync. Server-authoritative: a client cannot opt itself in or out.
/// </summary>
public Dictionary<string, bool> WorkshopModules { get; set; } = new Dictionary<string, bool>();
```

And in `deploy/mod-config.default.json`, add the block with every module present and `false`:

```json
"workshopModules": {
  "Bannerlord.Diplomacy": false,
  "ImprovedGarrisons": false,
  "RBM": false,
  "DismembermentPlus": false,
  "Fourberie": false,
  "UnblockableThrust": false,
  "PlayerSettlement": false
}
```

- [ ] **Step 4: Write the registrar**

```csharp
using GameInterface.Configuration;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Decides which modules are live. A module is live only when config enables it AND the catalog
/// finds it installed at its pinned fingerprint. Everything downstream — patch categories, sync
/// registration, handlers — keys off this one decision, so an absent, mismatched or disabled module
/// contributes nothing at all.
/// </summary>
public static class WorkshopModuleRegistrar
{
    public static IReadOnlyList<IWorkshopModule> ResolveLiveModules(
        IEnumerable<IWorkshopModule> modules,
        ModOptions options,
        IWorkshopModuleCatalog catalog)
    {
        var live = new List<IWorkshopModule>();
        if (modules == null || options == null || catalog == null) return live;

        foreach (var module in modules)
        {
            if (!options.IsWorkshopModuleEnabled(module.ModuleId)) continue;
            if (!catalog.TryGetInstalledSha256(module.ModuleId, out var installedSha)) continue;
            if (!string.Equals(installedSha, module.Fingerprint.Sha256, System.StringComparison.OrdinalIgnoreCase))
                continue;

            live.Add(module);
        }

        return live;
    }
}
```

Add `IsWorkshopModuleEnabled(string moduleId)` to `ModOptions` returning
`WorkshopModules.TryGetValue(moduleId, out var on) && on`. If `IWorkshopModuleCatalog` has no
`TryGetInstalledSha256`, add it to the existing catalog interface and implement it over the data
`WorkshopModuleDiscovery` already gathers — check with
`grep -n "interface IWorkshopModuleCatalog" -A 20 source/GameInterface/Services/WorkshopMods/Core/WorkshopModuleCatalog.cs`.

- [ ] **Step 5: Run the test to verify it passes**

Same command as Step 2. Expected: `Total: 3, Failed: 0`.

- [ ] **Step 6: Wire the registrar into `GameInterfaceModule`**

Replace the per-module conditional registrations added in Tasks 1–2 with one loop over
`ResolveLiveModules`, registering a `HarmonyPatchCategoryRegistration` for each live module's
`PatchCategory`. The Task 1 and 2 tests must still pass afterwards — re-run
`AbsentModuleSafetyTests`.

- [ ] **Step 7: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/Core/WorkshopModuleRegistrar.cs \
        source/GameInterface/Configuration/ModConfigData.cs \
        deploy/mod-config.default.json \
        source/GameInterface/GameInterfaceModule.cs \
        source/GameInterface.Tests/Services/WorkshopMods/Core/WorkshopModuleRegistrarTests.cs
git commit -m "Gate Workshop modules on config and pinned fingerprint"
```

---

### Task 9: Shared E2E gates for every module

**Files:**
- Create: `source/E2E.Tests/Services/WorkshopMods/WorkshopModuleTestBase.cs`
- Create: `source/E2E.Tests/Services/WorkshopMods/Diplomacy/DiplomacyModuleGateTests.cs`

**Interfaces:**
- Consumes: `IWorkshopModule` (Task 7), `WorkshopModuleRegistrar` (Task 8).
- Produces: `WorkshopModuleTestBase` — the base every future module's gate tests derive from. This is the deliverable that makes mod #8 cheap.

- [ ] **Step 1: Write the base with three gates**

```csharp
using E2E.Tests.Environment;
using GameInterface.Services.WorkshopMods.Core;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods;

/// <summary>
/// The gates every Workshop module must pass before it may be enabled on a live server. Deriving a
/// class and supplying the module is the whole cost of covering a new mod.
/// </summary>
public abstract class WorkshopModuleTestBase : IDisposable
{
    protected E2ETestEnvironment TestEnvironment { get; }
    protected abstract IWorkshopModule Module { get; }

    protected WorkshopModuleTestBase(ITestOutputHelper output)
        => TestEnvironment = new E2ETestEnvironment(output);

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void Absent_LoadsWithoutThrowing()
    {
        // The harness has no Workshop mods installed; constructing the environment already ran
        // PatchAll on every instance. Reaching here at all is the assertion.
        Assert.NotNull(TestEnvironment.Server);
    }

    [Fact]
    public void Disabled_ContributesNoPatchCategory()
    {
        // ModConfigProvider.ModOptions is STATIC (a process-wide OptionsBox), not an injected
        // service — do not try to Resolve<IModConfigProvider>(), there is no such interface.
        // Server and clients in this harness therefore share one options instance.
        var options = GameInterface.Configuration.ModConfigProvider.ModOptions;
        Assert.False(options.IsWorkshopModuleEnabled(Module.ModuleId));
    }

    [Fact]
    public void Fingerprint_IsPinnedToExactBytes()
    {
        Assert.Equal(64, Module.Fingerprint.Sha256.Length);
        Assert.NotEqual("0000000000000000000000000000000000000000000000000000000000000000",
                        Module.Fingerprint.Sha256);
    }
}
```

The fourth gate — client intent round-trip — is added in the follow-on plan, with the first routed
action. It cannot be written before there is an action to route.

- [ ] **Step 2: Derive the Diplomacy gates**

```csharp
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.WorkshopMods.Diplomacy;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Diplomacy;

public sealed class DiplomacyModuleGateTests : WorkshopModuleTestBase
{
    public DiplomacyModuleGateTests(ITestOutputHelper output) : base(output) { }

    protected override IWorkshopModule Module => new DiplomacyModule();
}
```

- [ ] **Step 3: Run and confirm it fails**

```bash
cd source/E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none \
  -class "E2E.Tests.Services.WorkshopMods.Diplomacy.DiplomacyModuleGateTests"
```

Expected: build failure — `DiplomacyModule` does not exist. That is Task 10.

---

### Task 10: Diplomacy implements the contract

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyModule.cs`
- Modify: `source/GameInterface/GameInterfaceModule.cs`

**Interfaces:**
- Consumes: `IWorkshopModule`, `ModuleFingerprint` (Task 7); `DiplomacyCompatibilityPolicy` (existing).
- Produces: `DiplomacyModule` — consumed by Task 9's gate tests.

- [ ] **Step 1: Write the module**

```csharp
using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy's declaration. Fingerprint values come from DiplomacyCompatibilityPolicy so the pin
/// has exactly one source of truth.
/// </summary>
internal sealed class DiplomacyModule : IWorkshopModule
{
    public string ModuleId => "Bannerlord.Diplomacy";
    public ulong WorkshopId => 2881380744UL;

    public ModuleFingerprint Fingerprint => new ModuleFingerprint(
        DiplomacyCompatibilityPolicy.SupportedAssemblyName,
        DiplomacyCompatibilityPolicy.SupportedAssemblyVersion,
        DiplomacyCompatibilityPolicy.SupportedAssemblySha256);

    public string PatchCategory => WorkshopPatchCategories.Diplomacy;

    public void RegisterSync(AutoSyncRegistry registry)
    {
        // Resolved reflectively: the type does not exist at compile time. RegisterSync runs only
        // after the registrar has confirmed presence and fingerprint, so these resolve or the
        // module is not live.
        var cooldown = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.CampaignBehaviors.CooldownBehavior");
        if (cooldown == null) return;

        foreach (var name in new[] { "_lastWarDeclaredTime", "_lastPeaceProposalTime" })
        {
            var field = AccessTools.Field(cooldown, name);
            if (field != null) registry.AddField(field);
        }
    }
}
```

Before writing the field list, confirm the real field names:

```bash
"/c/Users/Bryce/.dotnet/tools/ilspycmd.exe" -t Diplomacy.CampaignBehaviors.CooldownBehavior \
  "/p/SteamLibrary/steamapps/workshop/content/261550/2881380744/bin/Win64_Shipping_Client/Bannerlord.Diplomacy.1.4.7.dll" \
  | grep -n "private.*;" | head -20
```

Use the actual names; the two above are illustrative of the shape, not verified.

- [ ] **Step 2: Register it for DI**

In `GameInterfaceModule.cs`:

```csharp
builder.RegisterType<DiplomacyModule>().As<IWorkshopModule>().SingleInstance();
```

- [ ] **Step 3: Run the gate tests**

```bash
cd source/E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none \
  -class "E2E.Tests.Services.WorkshopMods.Diplomacy.DiplomacyModuleGateTests"
```

Expected: `Total: 3, Failed: 0`.

- [ ] **Step 4: Run the whole workshop E2E namespace for regressions**

```bash
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none -namespace "E2E.Tests.Services.WorkshopMods"
```

Expected: 0 failed.

- [ ] **Step 5: Commit**

```bash
git add source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyModule.cs \
        source/GameInterface/GameInterfaceModule.cs \
        source/E2E.Tests/Services/WorkshopMods/WorkshopModuleTestBase.cs \
        source/E2E.Tests/Services/WorkshopMods/Diplomacy/DiplomacyModuleGateTests.cs
git commit -m "Declare Diplomacy through the Workshop module contract"
```

---

### Task 11: Push and verify on CI

- [ ] **Step 1: Run every affected suite locally**

```bash
cd source/GameInterface.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  GameInterface.Tests.dll -noshadow -parallel none -namespace "GameInterface.Tests.Services.WorkshopMods"
cd ../../../../E2E.Tests/bin/Release/net6.0
DOTNET_TC_CallCounting=0 "/c/Program Files/dotnet/dotnet.exe" xunit.console.dll \
  E2E.Tests.dll -noshadow -parallel none -namespace "E2E.Tests.Services.WorkshopMods"
```

- [ ] **Step 2: Push and mark the PR ready**

```bash
git push origin 25vid/workshop-integration
gh pr ready --repo 25thplaymaking/BannerlordCoop-FriendEdition <pr-number>
```

- [ ] **Step 3: Confirm all eight E2E shards pass**

```bash
gh run list --repo 25thplaymaking/BannerlordCoop-FriendEdition --limit 1
```

Do not merge. Merging to `development` is Bryce's decision.

---

## What this plan deliberately does not cover

Routing Diplomacy's *actions* — the inbound intent path that makes a client's "declare war" reach
the server — is not planned here, and no task above pretends to. Writing those tasks honestly
requires enumerating Diplomacy's real player-initiated surface with the mod loaded, which is only
possible once Task 10 has it resolving in the harness. The follow-on plan covers:

- the fourth gate (`Intent_RoundTripsAndConverges`) added to `WorkshopModuleTestBase`,
- `ModuleAction` and the shared intent funnel,
- one task per routed Diplomacy action,

and is written after increment 0 and Tasks 7–11 are green. The design document already fixes the
shape those tasks must follow, so this is sequencing, not an open question.
