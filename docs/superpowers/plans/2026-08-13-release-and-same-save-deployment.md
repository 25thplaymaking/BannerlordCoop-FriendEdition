# Release And Same-Save Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the verified fixes to `development`, the launcher feed, and grain.silo while loading the exact existing `friendallmods1` save.

**Architecture:** Run the complete deterministic local gates, build separate Serilog 4.x client and Serilog 2.x server payloads, update the release pairing pins, and publish the launcher/client update through the repository’s documented workflows. Before stopping the host, inventory and hash the live save; after restart, prove the same filename and bytes were used and verify serving health from logs, port, and deployment ledger.

**Tech Stack:** PowerShell 7, .NET SDK x64, Git/GitHub CLI, SSH, repository deployment scripts, launcher GitHub Actions.

**Spec:** `doc/COOP-OPS-WORKFLOW.md`

## Global Constraints

- Do not create, migrate, rename, replace, or delete a campaign save.
- Reconcile the pre-deploy live save inventory against the post-restart inventory and backup before claiming success.
- Client `GameInterface.dll` uses Serilog 4.x; server `GameInterface.dll` uses Serilog 2.x.
- Re-pair both physical `DedicatedServer.Core` locations to the exact four deployed Coop DLL hashes.
- Do not launch or focus the game or launcher GUI.
- Do not claim rendered UI verification from headless tests.

---

### Task 1: Run complete source and package gates

**Files:**
- Modify: `docs/coop-next-update-bugsweep.md`
- Modify: `STATUS.md`

**Interfaces:**
- Consumes: the three implementation commits and repository build/package scripts.
- Produces: a clean commit with exact test counts, hashes, and honest rendered-validation status.

- [ ] **Step 1: Run focused test namespaces/classes**

Use the in-process xUnit console runner with `DOTNET_TC_CallCounting=0` for Diplomacy, capture completion, battle teardown, raid, Fourberie, and Separatism coverage; require zero failures.

- [ ] **Step 2: Run full build and test assemblies**

Build Release with `C:\Program Files\dotnet\dotnet.exe`. Run every test assembly in-process from a working directory where UI movie fixtures resolve; require zero unexpected failures and record intentional skips.

- [ ] **Step 3: Run release safety, XAML, launcher, server-kit, packaging, authority, and native-hook gates**

Use the exact commands in `doc/COOP-OPS-WORKFLOW.md`; stop on any failure.

- [ ] **Step 4: Correct the status board**

Remove the contradictory “live confirmed” wording for the previous Claude release. Record the new root causes, commits, exact green counts, and that rendered kingdom/raid verification remains pending until a player performs it.

- [ ] **Step 5: Commit**

```powershell
git add docs/coop-next-update-bugsweep.md STATUS.md docs/superpowers/plans
git commit -m "docs: record verified lifecycle fixes"
```

### Task 2: Publish client and launcher update

**Files:**
- No source files unless a release gate exposes a defect.

**Interfaces:**
- Consumes: repository GitHub workflows and rolling `client-stable`/`launcher-app` manifests.
- Produces: a published client manifest whose source commit and SHA-256 match the release payload; the existing launcher must observe it without an app rebuild unless launcher source changed.

- [ ] **Step 1: Push the verified `development` commits**

Push only after the local clean-tree and test gates pass.

- [ ] **Step 2: Dispatch and follow the documented stable client workflow**

Wait by GitHub run status, not an agent polling loop. Require all build, test, package, and publication jobs green.

- [ ] **Step 3: Verify the rolling manifest and artifact**

Download/read the published manifest, verify its source commit, version, URL, size, and SHA-256, then independently hash the downloaded archive.

- [ ] **Step 4: Verify launcher delivery behavior**

Run launcher unit tests and the headless feed preparation path against the published manifest; confirm the existing launcher reports the new client version and exact payload hash.

### Task 3: Deploy paired server and restart the same save

**Files:**
- Modify: `STATUS.md`

**Interfaces:**
- Consumes: verified Serilog 2.x server DLLs, the compatibility patcher, and `friendallmods1` live host configuration.
- Produces: an immutable rollback directory, deployment ledger, serving host, same-save proof, and status commit.

- [ ] **Step 1: Inventory the stopped-server target and save before mutation**

Over SSH, record process state, service/launch command, every live save filename with size/mtime/SHA-256, the configured save identity, all target DLL/core hashes, and free space. Reconcile that the configured save is exactly `friendallmods1` before proceeding.

- [ ] **Step 2: Stop and back up recoverably**

Stop through the documented supervisor, wait for process exit, copy the current module/core files and exact live save to a timestamped `_mod_backups/pre-<commit>-<utc>` directory, and verify backup hashes.

- [ ] **Step 3: Build and deploy the server pair**

Build the server DLLs with Serilog 2.x, run the release-pairing transform against their exact hashes, upload to staging, verify staging hashes, then atomically replace the two documented target sets.

- [ ] **Step 4: Restart and verify health**

Start with the unchanged launch configuration. Require release-pin verification, deployment-ledger success, `CAMPAIGN LOADED`, `friendallmods1`, `SERVING`, UDP 4200 bound, repeated pulses, zero restarts, and no fatal startup markers.

- [ ] **Step 5: Reconcile same-save proof and commit status**

Compare post-start save filenames against the pre-stop inventory and backup; record any legitimate timestamp/content change caused by the running campaign without replacing the identity. Add the rollback path, deployed hashes, launcher manifest version/hash, and honest live/rendered boundary to `STATUS.md`, then commit and push.
