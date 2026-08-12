# Launcher Startup Self-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the portable Calradia Co-op launcher securely update its own executable from GitHub Releases when it opens.

**Architecture:** A focused `LauncherSelfUpdater` checks and verifies the release manifest, stages a newer executable beside the installed launcher, and starts that verified copy in apply mode. A separate `LauncherUpdateApplier` performs the Windows process handoff, atomic replacement, rollback, relaunch, and cleanup; normal WPF startup remains responsible for the existing suite/client updates.

**Tech Stack:** .NET 8, WPF, `HttpClient`, SHA-256, xUnit, GitHub Actions, GitHub Releases.

## Global Constraints

- Keep the existing portable single-file launcher; do not add an installer framework or background service.
- Stable uses the rolling `launcher-app` feed; nightly is opt-in through a distinct manifest URL.
- Never overwrite `launcher-config.json`, logs, passwords, or unrelated neighboring files.
- Never execute an unverified payload, silently downgrade, or allow more than one self-update restart per launch chain.
- Offline checks continue with the installed launcher; reached-invalid feeds, bad hashes, and unsafe paths fail closed.
- Preserve the existing mod-suite/client updater behavior and keep unrelated dirty worktree files untouched.

---

## File structure

- Create `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`: manifest validation, version decision, download, SHA-256 verification, staging, and apply-process launch.
- Create `tools/CoopLauncher/Services/LauncherUpdateApplier.cs`: apply-mode parsing, safe layout checks, replacement transaction, rollback, relaunch, and post-start cleanup.
- Create `tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs`: feed, version, hash, offline, and staging tests.
- Create `tools/CoopLauncher.Tests/LauncherUpdateApplierTests.cs`: path, replacement, rollback, relaunch argument, cleanup, and config-preservation tests.
- Modify `tools/CoopLauncher/LauncherConfig.cs`: add the launcher manifest URL and its JSON model.
- Modify `tools/CoopLauncher/launcher-config.json`: configure the stable launcher feed without a password.
- Modify `tools/CoopLauncher/App.xaml.cs`: route apply/cleanup modes before normal window startup.
- Modify `tools/CoopLauncher/MainWindow.xaml.cs`: check launcher updates before locating/updating Bannerlord and close on handoff.
- Modify `tools/CoopLauncher/README.md`: document self-update and stable/nightly configuration.
- Modify `.github/workflows/launcher-app-release.yml`: embed a monotonic version and publish executable, zip, and manifest to stable/manual or nightly/development feeds.
- Modify `.github/scripts/verify-release-safety.py`: enforce launcher channel, manifest, hash, and public-password safety.

### Task 1: Feed contract and verified staging

**Files:**
- Create: `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`
- Create: `tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs`
- Modify: `tools/CoopLauncher/LauncherConfig.cs`
- Modify: `tools/CoopLauncher/launcher-config.json`
- Test: `tools/CoopLauncher.Tests/LauncherConfigTests.cs`

**Interfaces:**
- Consumes: `LauncherConfig.LauncherManifestUrl`, installed executable path, and current assembly version.
- Produces: `LauncherUpdateManifest`, `LauncherUpdateOutcome`, `LauncherUpdateResult`,
  `LauncherUpdateCommand`, and `LauncherSelfUpdater.CheckAndStageAsync(...)` for Tasks 2 and 3.

- [x] **Step 1: Write failing configuration and manifest tests**

Add tests proving the shipped config has a non-empty HTTPS `launcher-app/launcher.json` URL, old configs inherit that default, manifests require a numeric version, HTTPS executable URL, and exact 64-character SHA-256, and remote versions must be strictly greater than the embedded version.

```csharp
Assert.Equal(
    "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/launcher.json",
    config.LauncherManifestUrl);
Assert.False(LauncherSelfUpdater.IsManifestValid(new LauncherUpdateManifest { Version = "bad" }));
Assert.True(LauncherSelfUpdater.IsNewer("2026.8.11.2", new Version(2026, 8, 11, 1)));
```

- [x] **Step 2: Run the focused tests and verify they fail**

Run:
`"C:\Program Files\dotnet\dotnet.exe" test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherConfigTests|FullyQualifiedName~LauncherSelfUpdaterTests" --consoleLoggerParameters:ErrorsOnly`

Expected: FAIL because the launcher feed property, manifest, and updater do not exist.

- [x] **Step 3: Add the minimal feed model and decision logic**

Add the default property and model:

```csharp
public string LauncherManifestUrl { get; set; } =
    "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/launcher.json";

public sealed class LauncherUpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("launcherUrl")] public string LauncherUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}
```

Define `LauncherUpdateOutcome { Disabled, UpToDate, Restarting, Offline, Failed }`,
`LauncherUpdateResult`, a `LauncherUpdateCommand` that owns the exact apply-mode switch/arguments,
strict `Version.TryParse` comparison, HTTPS payload validation, and an injectable `HttpClient`
constructor for deterministic tests.

- [x] **Step 4: Write failing fetch/download tests**

Use a test `HttpMessageHandler` to prove: no newer build does not download, DNS/timeout-style exceptions return `Offline`, reached HTTP errors return `Failed`, a wrong SHA returns `Failed` and is never launched, and a valid newer executable is staged under `.calradia-launcher-update-*` beside the target.

- [x] **Step 5: Implement minimal verified staging**

Implement:

```csharp
public Task<LauncherUpdateResult> CheckAndStageAsync(
    string executablePath,
    Version currentVersion,
    Action<double, string> progress,
    Func<ProcessStartInfo, Process?>? startProcess = null);
```

Fetch with a bounded client timeout, download to the same-volume stage, verify SHA-256, create
apply-mode arguments through `LauncherUpdateCommand`, start only the verified file, and delete the
stage on pre-launch failure.

- [x] **Step 6: Run focused tests and commit**

Run the command from Step 2. Expected: PASS.

Commit only Task 1 files:

```powershell
git add -- tools/CoopLauncher/Services/LauncherSelfUpdater.cs tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs tools/CoopLauncher/LauncherConfig.cs tools/CoopLauncher/launcher-config.json tools/CoopLauncher.Tests/LauncherConfigTests.cs
git commit -m "Add verified launcher update feed"
```

### Task 2: Atomic replacement, rollback, and cleanup

**Files:**
- Create: `tools/CoopLauncher/Services/LauncherUpdateApplier.cs`
- Create: `tools/CoopLauncher.Tests/LauncherUpdateApplierTests.cs`
- Modify: `tools/CoopLauncher/Services/LauncherSelfUpdater.cs`

**Interfaces:**
- Consumes: Task 1's `LauncherUpdateCommand`, verified staged executable, target path, old process ID,
  and SHA-256.
- Produces: `TryParseApplyArguments`, `ApplyAndRelaunch`, `BuildCompletionArguments`, and `ScheduleCleanup` for Task 3.

- [x] **Step 1: Write failing filesystem transaction tests**

Create temporary directories and prove the applier rejects a stage outside a `.calradia-launcher-update-*` child of the target directory, replaces the target bytes, keeps `launcher-config.json` byte-identical, and restores the original target when the injected relaunch action throws.

```csharp
LauncherUpdateApplier.ApplyAndRelaunch(
    stagedExe, targetExe, expectedSha, _ => throw new InvalidOperationException("launch failed"));
Assert.Equal("old launcher", File.ReadAllText(targetExe));
Assert.Equal(originalConfig, File.ReadAllBytes(configPath));
```

- [x] **Step 2: Run the applier tests and verify they fail**

Run:
`"C:\Program Files\dotnet\dotnet.exe" test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release -p:NuGetAudit=false --filter FullyQualifiedName~LauncherUpdateApplierTests --consoleLoggerParameters:ErrorsOnly`

Expected: FAIL because the applier does not exist.

- [x] **Step 3: Implement the replacement transaction**

Validate full paths and the staged hash again, wait for the old PID with a finite timeout, copy the staged executable to a same-directory pending file, then use `File.Replace(pending, target, backup)`. Relaunch the installed path with:

```text
--launcher-update-complete <helperPid> <stageDirectory> <backupPath>
```

If replacement or relaunch fails, restore the backup and return a failed result. Do not enumerate, move, or delete any path outside the validated target, pending, backup, and stage paths.

- [x] **Step 4: Add argument and cleanup tests, then implement them**

Prove malformed/missing arguments are rejected, an apply chain carries the expected target/PID/hash, cleanup waits for the helper PID and removes only the validated backup/stage, and a cleanup failure is best-effort and logged. Add exact constants for `--apply-launcher-update`, `--launcher-update-complete`, and `--skip-launcher-update-once` so Task 3 does not duplicate strings.

- [x] **Step 5: Run focused tests and commit**

Run the command from Step 2 and the Task 1 focused tests. Expected: PASS.

```powershell
git add -- tools/CoopLauncher/Services/LauncherUpdateApplier.cs tools/CoopLauncher.Tests/LauncherUpdateApplierTests.cs tools/CoopLauncher/Services/LauncherSelfUpdater.cs
git commit -m "Apply launcher updates with rollback"
```

### Task 3: Wire startup and preserve the existing join gate

**Files:**
- Modify: `tools/CoopLauncher/App.xaml.cs`
- Modify: `tools/CoopLauncher/MainWindow.xaml.cs`
- Test: `tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs`
- Test: `tools/CoopLauncher.Tests/LauncherUpdateApplierTests.cs`

**Interfaces:**
- Consumes: the Task 1 updater and Task 2 apply/cleanup modes.
- Produces: a startup path where self-update precedes Bannerlord discovery and mod updates, and only `Failed` blocks normal Join.

- [x] **Step 1: Write failing startup-gate and loop-prevention tests**

Test `MainWindow.CanContinueAfterLauncherUpdate(...)` for every outcome and test argument parsing so `--skip-launcher-update-once` suppresses exactly one check while normal and completion launches still show the window.

```csharp
[InlineData(LauncherUpdateOutcome.Failed, false)]
[InlineData(LauncherUpdateOutcome.Offline, true)]
[InlineData(LauncherUpdateOutcome.UpToDate, true)]
```

- [x] **Step 2: Run focused tests and verify they fail**

Run the two launcher updater test classes. Expected: FAIL because startup does not route these modes.

- [x] **Step 3: Implement app-mode routing and UI orchestration**

In `App.OnStartup`, handle apply mode without creating a window; handle completion mode by showing the normal window and scheduling validated cleanup; pass `skipLauncherUpdate` into `MainWindow` only for the one-shot rollback path. In `MainWindow.OnLoaded`, check self-update immediately after logging. Show progress in the existing update rail, close on `Restarting`, block Join on `Failed`, and otherwise continue into the unchanged game locator, server probe, and `ModUpdater` flow.

- [x] **Step 4: Run all launcher tests and XAML validation**

Run:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release -p:NuGetAudit=false --consoleLoggerParameters:ErrorsOnly
python tools/CoopLauncher/check-xaml-resources.py
```

Expected: all tests pass and every XAML resource reference resolves.

- [x] **Step 5: Commit startup integration**

```powershell
git add -- tools/CoopLauncher/App.xaml.cs tools/CoopLauncher/MainWindow.xaml.cs tools/CoopLauncher.Tests/LauncherSelfUpdaterTests.cs tools/CoopLauncher.Tests/LauncherUpdateApplierTests.cs
git commit -m "Run launcher self-update on startup"
```

### Task 4: Publish both channels and verify an exact candidate

**Files:**
- Modify: `.github/workflows/launcher-app-release.yml`
- Modify: `.github/scripts/verify-release-safety.py`
- Modify: `tools/CoopLauncher/README.md`

**Interfaces:**
- Consumes: the Task 1 manifest schema and built assembly version.
- Produces: `launcher-app/launcher.json` for stable and `launcher-nightly/launcher.json` for opt-in nightly.

- [x] **Step 1: Extend the failing release-safety assertions**

Require development pushes to publish only `launcher-nightly`, manual dispatch to select stable or nightly with nightly as the safe default, stable to map to `launcher-app`, and the build to generate `launcher.json` from the exact executable SHA-256. Continue requiring launcher tests, XAML checks, and blank public `serverPassword`.

- [x] **Step 2: Run the safety script and verify it fails**

Run: `python .github/scripts/verify-release-safety.py`

Expected: FAIL because the current workflow is manual-only and publishes no manifest.

- [x] **Step 3: Implement channel-aware release publishing**

Add a path-filtered `development` push trigger plus a manual `channel` choice. Generate a numeric UTC/run version, pass it to `dotnet publish -p:Version=$version`, hash `dist/CalradiaCoop.exe`, and write:

```json
{"version":"<version>","launcherUrl":"https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/<tag>/CalradiaCoop.exe","sha256":"<sha256>","notes":"Launcher source <commit>"}
```

Upload `CalradiaCoop.exe`, `CalradiaCoop-Launcher.zip`, `launcher-config.json`, and `launcher.json` with `--clobber`. Never allow a development push to select `launcher-app`.

- [x] **Step 4: Update documentation and run full local verification**

Document `launcherManifestUrl`, one-time bootstrap behavior for launchers predating self-update, stable/manual versus nightly/development, and config preservation. Run:

```powershell
python .github/scripts/verify-release-safety.py
& "C:\Program Files\dotnet\dotnet.exe" test tools/CoopLauncher.Tests/CoopLauncher.Tests.csproj -c Release -p:NuGetAudit=false --consoleLoggerParameters:ErrorsOnly
python tools/CoopLauncher/check-xaml-resources.py
& "C:\Program Files\dotnet\dotnet.exe" publish tools/CoopLauncher/CoopLauncher.csproj -c Release -p:NuGetAudit=false -p:Version=2026.8.11.1
```

Expected: safety PASS, all launcher tests PASS, XAML PASS, and the publish directory contains non-empty `CalradiaCoop.exe` and blank-password `launcher-config.json`.

- [x] **Step 5: Exercise the replacement harness and commit**

Use the applier test fixture as the local end-to-end harness: install old executable/config bytes in a temporary target directory, stage the published candidate, run the replacement entry point with an injected headless relaunch, and assert the target hash matches the candidate while the config hash is unchanged.

```powershell
git add -- .github/workflows/launcher-app-release.yml .github/scripts/verify-release-safety.py tools/CoopLauncher/README.md
git commit -m "Publish launcher self-update manifests"
```

### Task 5: Final release proof

**Files:**
- Verify only; update this plan's checkboxes as steps complete.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: a verified, publishable branch with no unrelated files staged.

- [x] **Step 1: Run final checks and inspect scope**

Run the Task 4 verification commands, `git diff --check`, `git status --short`, and `git log -5 --oneline`. Confirm only the two pre-existing mission edits and `work/` remain unrelated/dirty.

- [x] **Step 2: Push stable and development branches and publish feeds**

Push the verified commits to `25vid/workshop-integration` and `development`. Allow the development workflow to publish `launcher-nightly`; manually dispatch stable only from the same verified commit. Wait without polling, then verify each public manifest version, executable URL, SHA-256, and asset hash.

- [x] **Step 3: Bootstrap the local installed launcher**

Inventory the current launcher directory, preserve `launcher-config.json`, back up the installed executable, replace only `CalradiaCoop.exe` with the exact stable asset, and verify its hash. This one-time replacement is required because pre-feature launchers cannot self-update.

- [x] **Step 4: Record completion**

Mark all plan checkboxes complete, commit the plan update, and report exact commit, release URLs, hashes, test counts, local install state, and the unchanged unrelated worktree files.
