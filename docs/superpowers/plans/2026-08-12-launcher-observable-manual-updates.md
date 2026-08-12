# Launcher Observable Manual Updates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the launcher query and display launcher, mod-suite, and co-op-client versions at startup, require an explicit **PREPARE YOUR ARMY** click to install updates, and enable **MARCH TO WAR** only after all three components are verified current.

**Architecture:** Split manifest querying from payload installation in the two updater services. Add immutable component/snapshot models plus a coordinator that checks all feeds, reduces them into one primary action, and carries a member-approved preparation transaction across a launcher self-update restart. Keep WPF responsible only for rendering coordinator state and dispatching the current primary action.

**Tech Stack:** .NET 8, C# 12, WPF, `HttpClient`, xUnit, GitHub Actions rolling release manifests.

## Global Constraints

- Startup queries manifests automatically but downloads no executable or zip payload.
- Launcher, mod-suite, and co-op-client installed/available versions remain independently visible.
- **PREPARE YOUR ARMY** installs all known required updates; **MARCH TO WAR** is available only when every component is verified current.
- Empty, unreachable, timed-out, HTTP-error, or malformed required feeds fail closed and show **THE COURT JESTER IS ASLEEP** with a retry action.
- Payload URLs must be HTTPS and payload SHA-256 values must validate before installation.
- Launcher replacement remains atomic, rollback-capable, and limited to one restart in a preparation transaction.
- No update path launches Bannerlord, accesses campaign save locations, changes server configuration, or creates a save.
- Use `C:\Program Files\dotnet\dotnet.exe` for every .NET command on this machine.
- Preserve unrelated changes in `source/Missions/CoopMissionComponent.cs`, `source/Missions/CoopMissionController.cs`, and `work/`.

---

## File structure

- Create `tools/CoopLauncher/Services/ArmoryUpdateCoordinator.cs`: component status records, aggregate state reducer, concurrent manifest checks, and preparation sequence.
- Modify `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`: check-only and stage-known-manifest operations; continuation flag.
- Modify `tools/CoopLauncher/Services/ModUpdater.cs`: check-only suite/client planning and checked-manifest installation.
- Modify `tools/CoopLauncher/Services/LauncherUpdateApplier.cs`: one-time continue-preparation intent across replacement.
- Modify `tools/CoopLauncher/App.xaml.cs`: parse completion/continuation state and pass it into the window.
- Modify `tools/CoopLauncher/MainWindow.xaml`: three version rows and blocking armory copy.
- Modify `tools/CoopLauncher/MainWindow.xaml.cs`: snapshot rendering and adaptive primary-button routing.
- Create `tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs`: reducer, no-download, fail-closed, re-check, and ordering tests.
- Modify existing updater/applier tests for separated APIs and restart continuation.
- Modify `tools/CoopLauncher/README.md`: document the observable manual workflow.

---

### Task 1: Component status model and check-only launcher feed

**Files:**
- Create: `tools/CoopLauncher/Services/ArmoryUpdateCoordinator.cs`
- Modify: `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`
- Create: `tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs`
- Modify: `tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs`

**Interfaces:**
- Produces: `ArmoryComponent`, `ComponentUpdateState`, `ComponentUpdateStatus`, `LauncherUpdateCheck`, `ModUpdateCheck`, `ArmoryPrimaryAction`, and `ArmorySnapshot`.
- Produces: `Task<LauncherUpdateCheck> LauncherSelfUpdater.CheckAsync(Version currentVersion)`.
- Later tasks consume: `ArmorySnapshot.IsVerified`, `ArmorySnapshot.HasUpdates`, and `ArmorySnapshot.PrimaryAction`.

- [ ] **Step 1: Write the failing reducer tests**

```csharp
[Theory]
[InlineData(ComponentUpdateState.Current, ComponentUpdateState.Current,
    ComponentUpdateState.Current, ArmoryPrimaryAction.Launch)]
[InlineData(ComponentUpdateState.Current, ComponentUpdateState.UpdateAvailable,
    ComponentUpdateState.Current, ArmoryPrimaryAction.Prepare)]
[InlineData(ComponentUpdateState.Current, ComponentUpdateState.Unverified,
    ComponentUpdateState.Current, ArmoryPrimaryAction.RetryCheck)]
public void Snapshot_ReducesRequiredComponentsFailClosed(
    ComponentUpdateState launcher, ComponentUpdateState suite,
    ComponentUpdateState client, ArmoryPrimaryAction expected)
{
    ArmorySnapshot snapshot = Snapshot(launcher, suite, client);
    Assert.Equal(expected, snapshot.PrimaryAction);
}
```

- [ ] **Step 2: Write failing launcher check-only tests**

Use a stub handler that counts manifest and executable URLs and asserts
`request.Headers.CacheControl!.NoCache`:

```csharp
LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

Assert.Equal(ComponentUpdateState.UpdateAvailable, check.Status.State);
Assert.Equal("1.0", check.Status.InstalledVersion);
Assert.Equal("2.0.0", check.Status.AvailableVersion);
Assert.NotNull(check.Manifest);
Assert.Equal(1, requests); // manifest only
```

Cover equal/current, empty URL, unreachable host, timeout, HTTP error, malformed JSON, invalid version,
non-HTTPS payload URL, and invalid SHA. Every error/disabled case must be `Unverified`.

- [ ] **Step 3: Run tests and confirm RED**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~ArmoryUpdateCoordinatorTests|FullyQualifiedName~LauncherSelfUpdaterTests" -p:NuGetAudit=false
```

Expected: compile failures because the component models and `CheckAsync` do not exist.

- [ ] **Step 4: Implement immutable status/reducer types**

```csharp
public enum ArmoryComponent { Launcher, ModSuite, CoopClient }
public enum ComponentUpdateState { Current, UpdateAvailable, Unverified }
public enum ArmoryPrimaryAction { Launch, Prepare, RetryCheck }

public sealed record ComponentUpdateStatus(
    ArmoryComponent Component, string Label, string? InstalledVersion,
    string? AvailableVersion, string Notes,
    ComponentUpdateState State, string Detail);

public sealed record LauncherUpdateCheck(
    ComponentUpdateStatus Status, LauncherUpdateManifest? Manifest);

public sealed record ModUpdateCheck(
    ComponentUpdateStatus SuiteStatus, UpdateManifest? SuiteManifest,
    ComponentUpdateStatus ClientStatus, UpdateManifest? ClientManifest);

public sealed record ArmorySnapshot(LauncherUpdateCheck Launcher, ModUpdateCheck Mods)
{
    public IReadOnlyList<ComponentUpdateStatus> Components =>
        [Launcher.Status, Mods.SuiteStatus, Mods.ClientStatus];
    public bool IsVerified =>
        Components.All(item => item.State != ComponentUpdateState.Unverified);
    public bool HasUpdates =>
        Components.Any(item => item.State == ComponentUpdateState.UpdateAvailable);
    public ArmoryPrimaryAction PrimaryAction => !IsVerified
        ? ArmoryPrimaryAction.RetryCheck
        : HasUpdates ? ArmoryPrimaryAction.Prepare : ArmoryPrimaryAction.Launch;
}
```

- [ ] **Step 5: Implement `LauncherSelfUpdater.CheckAsync`**

Send a bounded cache-bypassing manifest GET. Map empty configuration, HTTP, transport, timeout, JSON,
and validation failures to an unverified status. Return current for remote <= installed and update
available only for remote > installed. Do not request `LauncherUrl`. Preserve the existing staging
body behind an internal method that accepts an already validated manifest.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run the Task 1 test command again. Expected: all reducer and launcher check tests pass, with no stage
directory or payload request during checks.

- [ ] **Step 7: Commit**

```powershell
git add -- tools/CoopLauncher/Services/ArmoryUpdateCoordinator.cs tools/CoopLauncher/Services/LauncherSelfUpdater.cs tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs
git commit -m "feat: query launcher updates without installing"
```

---

### Task 2: Check-only suite/client planning and verified installation

**Files:**
- Modify: `tools/CoopLauncher/Services/ModUpdater.cs`
- Modify: `tools/CoopLauncher.Tests/ModUpdaterTests.cs`
- Modify: `tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: component model types from Task 1.
- Produces: `Task<ModUpdateCheck> ModUpdater.CheckAsync(string modulesDir)`.
- Produces: `Task<UpdateResult> ModUpdater.InstallAsync(string modulesDir, ModUpdateCheck check, Action<ArmoryComponent, double, string> progress)`.

- [ ] **Step 1: Write failing manifest-only tests**

Use an injected `HttpClient` to distinguish manifest and zip requests:

```csharp
ModUpdateCheck check = await updater.CheckAsync(fixture.Modules);

Assert.Equal(ComponentUpdateState.Current, check.SuiteStatus.State);
Assert.Equal(ComponentUpdateState.UpdateAvailable, check.ClientStatus.State);
Assert.Equal(2, manifestRequests);
Assert.Equal(0, zipRequests);
```

Add missing receipt, older remote, empty URL, unreachable, timeout, HTTP error, malformed JSON, invalid
version, non-HTTPS asset, and invalid hash cases. Missing receipts are update available; feed failures
are unverified.

- [ ] **Step 2: Write failing install-plan tests**

Pass checked suite/client manifests, record the requested payload URLs and progress components, and
prove suite installation precedes client installation. Preserve exact-replacement, rollback,
stale-file removal, and zip-slip tests.

- [ ] **Step 3: Run tests and confirm RED**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~ModUpdaterTests|FullyQualifiedName~ArmoryUpdateCoordinatorTests" -p:NuGetAudit=false
```

Expected: failures because `CheckAsync`, `InstallAsync`, and injected HTTP support are absent.

- [ ] **Step 4: Implement check-only tier planning**

```csharp
public async Task<ModUpdateCheck> CheckAsync(string modulesDir)
{
    Task<TierUpdateCheck> suite = CheckTierAsync(
        _config.SuiteManifestUrl,
        Path.Combine(modulesDir, "coop-suite-version.txt"),
        ArmoryComponent.ModSuite, "Mod suite");
    Task<TierUpdateCheck> client = CheckTierAsync(
        _config.UpdateManifestUrl,
        Path.Combine(modulesDir, "Coop", "installed-version.txt"),
        ArmoryComponent.CoopClient, "Co-op client");
    await Task.WhenAll(suite, client);
    return new ModUpdateCheck(
        suite.Result.Status, suite.Result.Manifest,
        client.Result.Status, client.Result.Manifest);
}
```

Use cache-bypassing GETs, dotted-numeric versions, HTTPS zip URLs, and exact SHA-256 validation.

- [ ] **Step 5: Implement install-only checked plans**

```csharp
if (check.SuiteStatus.State == ComponentUpdateState.UpdateAvailable)
{
    UpdateResult suite = await InstallTierAsync(
        check.SuiteManifest!, modulesDir,
        Path.Combine(modulesDir, "coop-suite-version.txt"),
        ArmoryComponent.ModSuite, "mod suite", progress);
    if (suite.Outcome == UpdateOutcome.Failed) return suite;
}
// Install co-op client second under Modules\Coop.
```

Never fetch a manifest in `InstallAsync`. Retain payload SHA verification, staging, rollback, and
temporary cleanup. Map payload HTTP/timeout/integrity/install failures to `Failed`.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run the Task 2 command again. Expected: all tests pass and check-only tests report zero payload requests.

- [ ] **Step 7: Commit**

```powershell
git add -- tools/CoopLauncher/Services/ModUpdater.cs tools/CoopLauncher.Tests/ModUpdaterTests.cs tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs
git commit -m "feat: separate mod update checks from installs"
```

---

### Task 3: Preparation coordinator and restart continuation

**Files:**
- Modify: `tools/CoopLauncher/Services/ArmoryUpdateCoordinator.cs`
- Modify: `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`
- Modify: `tools/CoopLauncher/Services/LauncherUpdateApplier.cs`
- Modify: `tools/CoopLauncher/App.xaml.cs`
- Modify: related tests.

**Interfaces:**
- Produces: `Task<ArmorySnapshot> ArmoryUpdateCoordinator.CheckAsync(Version currentVersion, string modulesDir)`.
- Produces: `Task<ArmoryPreparationResult> ArmoryUpdateCoordinator.PrepareAsync(string executablePath, Version currentVersion, string modulesDir, Action<ArmoryComponent, double, string> progress)`.
- Produces: `LauncherCompletionRequest.ContinuePreparation` and validated `--continue-army-preparation` propagation.

- [ ] **Step 1: Write failing coordinator order/re-check tests**

```csharp
ArmoryPreparationResult result = await coordinator.PrepareAsync(
    "CalradiaCoop.exe", new Version(1, 0), "Modules", (_, _, _) => { });

Assert.Equal(
    ["check-launcher", "check-mods", "install-mods", "check-launcher", "check-mods"],
    calls);
Assert.Equal(ArmoryPreparationOutcome.Completed, result.Outcome);
```

Add unverified re-check/download-nothing, launcher-first/restarting, and post-install
still-outdated/unverified failure tests.

- [ ] **Step 2: Write failing continuation tests**

Assert a button-approved launcher stage carries exactly one `--continue-army-preparation` through
apply mode to the replaced launcher. Reject malformed/duplicate markers. Assert rollback uses only
`--skip-launcher-update-once` and never continues installation.

- [ ] **Step 3: Run tests and confirm RED**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~ArmoryUpdateCoordinatorTests|FullyQualifiedName~LauncherSelfUpdaterTests|FullyQualifiedName~LauncherUpdateApplierTests" -p:NuGetAudit=false
```

Expected: coordinator and continuation symbols are missing.

- [ ] **Step 4: Implement the strict transaction**

Add internal fakeable service interfaces. Production `CheckAsync` starts launcher and mod queries
together. `PrepareAsync` rechecks first, rejects unverified results, stages launcher first, otherwise
installs suite/client, then rechecks and succeeds only for verified/current state.

```csharp
public enum ArmoryPreparationOutcome { Completed, Restarting, Unverified, Failed }
public sealed record ArmoryPreparationResult(
    ArmoryPreparationOutcome Outcome, ArmorySnapshot Snapshot, string Message);
```

- [ ] **Step 5: Carry the one-time intent through replacement**

Add `bool ContinuePreparation` to launcher command/apply/completion records. Include the marker only
for a button-approved transaction. Parse it exactly once, pass
`completion?.ContinuePreparation == true` into `MainWindow`, and keep rollback non-continuing.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run the Task 3 command. Expected: all order, re-check, continuation, cleanup, and rollback tests pass.

- [ ] **Step 7: Commit**

```powershell
git add -- tools/CoopLauncher/Services/ArmoryUpdateCoordinator.cs tools/CoopLauncher/Services/LauncherSelfUpdater.cs tools/CoopLauncher/Services/LauncherUpdateApplier.cs tools/CoopLauncher/App.xaml.cs tools/CoopLauncher.Tests
git commit -m "feat: coordinate member-approved launcher updates"
```

---

### Task 4: Adaptive armory UI and fail-closed routing

**Files:**
- Modify: `tools/CoopLauncher/MainWindow.xaml`
- Modify: `tools/CoopLauncher/MainWindow.xaml.cs`
- Modify: `tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: snapshot and preparation results from Task 3.
- Produces: WPF fields `LauncherUpdateValue`, `SuiteUpdateValue`, `ClientUpdateValue`,
  `ArmoryHeadline`, `ArmoryDetail`, `UpdateBar`, and the adaptive existing `JoinButton`.

- [ ] **Step 1: Write failing copy/format tests**

```csharp
[Theory]
[InlineData(ArmoryPrimaryAction.Launch, "MARCH TO WAR")]
[InlineData(ArmoryPrimaryAction.Prepare, "PREPARE YOUR ARMY")]
[InlineData(ArmoryPrimaryAction.RetryCheck, "TRY THE JESTER AGAIN")]
public void PrimaryCopy_MatchesAggregateAction(
    ArmoryPrimaryAction action, string expected) =>
    Assert.Equal(expected, MainWindow.PrimaryButtonText(action));
```

Also assert unverified headline `THE COURT JESTER IS ASLEEP`, known updates never select launch, and
component display text includes installed and available versions. Add
`MainWindow.CanDispatchPrimaryAction(bool operationActive, ArmorySnapshot? snapshot)` assertions that
return false while an operation is active, preventing repeated clicks from starting overlapping work.

- [ ] **Step 2: Run tests and confirm RED**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~ArmoryUpdateCoordinatorTests" -p:NuGetAudit=false
```

Expected: copy/formatting methods are absent.

- [ ] **Step 3: Add the three-row panel**

Use a compact labeled grid in the existing flexible content row:

```xml
<Grid x:Name="ArmoryPanel">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="92" />
    <ColumnDefinition Width="*" />
  </Grid.ColumnDefinitions>
  <TextBlock Text="LAUNCHER" />
  <TextBlock x:Name="LauncherUpdateValue" Grid.Column="1" />
  <!-- MOD SUITE and CO-OP CLIENT use separate grid rows. -->
</Grid>
```

Add `ArmoryHeadline` and `ArmoryDetail`. Keep the progress rail. Make all text wrap/trim inside
740×470 and reuse existing brushes, fonts, and resources.

- [ ] **Step 4: Replace startup installation with check/render**

On load, locate Bannerlord, render `CHECKING THE ARMORY…`, call coordinator `CheckAsync`, render all
rows, and set the primary action. Do not call a payload installer. A validated continuation invokes
preparation after the check; ordinary startup waits for the member.

- [ ] **Step 5: Route one guarded primary handler**

```csharp
switch (_snapshot.PrimaryAction)
{
    case ArmoryPrimaryAction.RetryCheck:
        await CheckArmoryAsync();
        break;
    case ArmoryPrimaryAction.Prepare:
        await PrepareArmyAsync();
        break;
    case ArmoryPrimaryAction.Launch:
        await LaunchGameAsync();
        break;
}
```

Guard with `_operationActive`. Use `PREPARING YOUR ARMY…` while installing,
`TRY PREPARING AGAIN` after install failure, and sleeping-jester copy when unverified. Retry checks
only; host polling cannot enable the primary button.

- [ ] **Step 6: Run focused tests and XAML validation**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~ArmoryUpdateCoordinatorTests" -p:NuGetAudit=false
python tools/CoopLauncher/check-xaml-resources.py
```

Expected: tests pass and XAML checker exits 0.

- [ ] **Step 7: Commit**

```powershell
git add -- tools/CoopLauncher/MainWindow.xaml tools/CoopLauncher/MainWindow.xaml.cs tools/CoopLauncher.Tests/ArmoryUpdateCoordinatorTests.cs
git commit -m "feat: show actionable launcher update status"
```

---

### Task 5: Documentation, regression verification, visual proof, and publication

**Files:**
- Modify: `tools/CoopLauncher/README.md`
- Verify: `.github/workflows/launcher-app-release.yml`

**Interfaces:**
- Consumes: finished launcher behavior and release workflow.
- Produces: accurate member/operator documentation and a rolling launcher manifest whose version/hash match the executable.

- [ ] **Step 1: Update documentation**

Document startup manifest-only checks, three visible versions, explicit preparation consent, fail-closed
GitHub outages, one-time restart continuation, and the all-current launch gate. Remove claims that
self-update is automatic, unreachable feeds permit joining, or required feed URLs can be empty in a
production launcher.

- [ ] **Step 2: Run all automated verification**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release -p:NuGetAudit=false
python tools/CoopLauncher/check-xaml-resources.py
python .github/scripts/verify-release-safety.py
```

Expected: zero failed tests and both Python checks exit 0.

- [ ] **Step 3: Publish and render headlessly**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' publish tools/CoopLauncher/CoopLauncher.csproj -c Release -p:NuGetAudit=false -p:Version=2026.8.12.8
& 'tools\CoopLauncher\bin\Release\net8.0-windows\win-x64\publish\CalradiaCoop.exe' --shoot 'tools\CoopLauncher\bin\Release\armory-preview.png'
```

Before execution, query the live launcher manifest and increment the revision if it is already at or
above `2026.8.12.8`. Expected: a 740×470 PNG containing all three labeled rows with no clipping.

- [ ] **Step 4: Verify scope and save boundary**

```powershell
git diff --check HEAD~4..HEAD
git status --short
git diff --name-only HEAD~4..HEAD
```

Expected: only launcher, launcher tests/docs, and plan/spec files are in feature commits. The existing
mission files and `work/` remain unstaged. No save file, server config, or deployment path appears.

- [ ] **Step 5: Commit documentation**

```powershell
git add -- tools/CoopLauncher/README.md
git commit -m "docs: explain manual verified launcher updates"
```

- [ ] **Step 6: Push and publish the launcher feed**

Push verified commits to the branch used by the launcher release workflow, wait without assigning an
agent to polling, and inspect the completed workflow/release once. Confirm live `launcher.json` is
newer than `2026.8.12.7`, its SHA-256 matches `CalradiaCoop.exe`, and public
`launcher-config.json` has an empty password. Do not deploy or restart the game server.
