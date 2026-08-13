# Diplomacy Client UI Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every Diplomacy-backed kingdom and encyclopedia surface construct only after a valid host snapshot exists, with a usable client settings object and no exception-swallowing finalizers.

**Architecture:** Patch the audited `GlobalSettings<Diplomacy.Settings>.Instance` getter so a client-only fallback settings object exists when MCM did not register Diplomacy, while preserving a real MCM instance when available. Treat Diplomacy UIExtender types as an explicit lifecycle: disable authoritative surfaces before campaign initialization, permanently retire duplicate civil-war surfaces, and atomically enable the routed surfaces only after the trusted snapshot commits.

**Tech Stack:** C# 10, .NET Standard 2.0, Harmony, Bannerlord UIExtenderEx, xUnit 2.9.3.

**Spec:** `docs/coop-next-update-bugsweep.md`

## Global Constraints

- Bannerlord remains pinned to `v1.4.7`.
- Diplomacy remains the byte-exact supported Workshop DLL; do not fork or modify it.
- The server remains authoritative for all Diplomacy state and operations.
- A missing, malformed, stale, or conflicting snapshot must keep the client UI disabled and disconnect through the existing compatibility failure path.
- Remove finalizers that swallow `NullReferenceException`; do not mask half-built view models.
- Use `C:\Program Files\dotnet\dotnet.exe` for every .NET command.

---

### Task 1: Specify the settings bridge and UI lifecycle

**Files:**
- Create: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyClientSettingsBridge.cs`
- Create: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyClientUiLifecycle.cs`
- Modify: `source/GameInterface.Tests/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityTests.cs`

**Interfaces:**
- Consumes: `DiplomacyCompatibilityPolicy.ResolveType(string)`, UIExtenderEx `GetUIExtenderFor(string)`, `Enable(Type)`, and `Disable(Type)`.
- Produces: `DiplomacyClientSettingsBridge.Reset()`, `DiplomacyClientSettingsBridge.SupplyFallback(ref object)`, `DiplomacyClientUiLifecycle.ResetForCampaign()`, and `DiplomacyClientUiLifecycle.MarkSnapshotReady()`.

- [ ] **Step 1: Write failing lifecycle inventory and transition tests**

Add tests asserting that the gated list contains `DiplomacyPanelPrefabExtension`, `KingdomDiplomacyVMMixin`, `KingdomWarItemVMMixin`, `KingdomTruceItemVMMixin`, `KingdomClanVMMixin`, `EncyclopediaHeroPagePrefabExtension`, and `EncyclopediaHeroPageVMMixin`; that the retired list contains all three `KingdomManagement*` types plus the faction-page/rebel-faction types; and that `MarkSnapshotReady()` cannot enable anything on the server or before a successful reset/disable transition.

- [ ] **Step 2: Run the focused tests and verify RED**

Run the xUnit console runner for `GameInterface.Tests.Services.WorkshopMods.Diplomacy` and expect compilation failures for the two missing lifecycle classes.

- [ ] **Step 3: Implement the client-only fallback and explicit type inventories**

Resolve the closed generic MCM settings getter at runtime. In its Harmony postfix, leave a non-null real provider result untouched; otherwise create one public parameterless `Diplomacy.Settings` instance per campaign and return it only on clients. Implement the UI lifecycle using an injectable internal `Action<string, bool>` test seam: `false` disables each gated and retired type during reset; `true` enables only gated types after readiness; retired types are never enabled.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the same namespace and require zero failures.

- [ ] **Step 5: Commit**

```powershell
git add source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyClientSettingsBridge.cs source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyClientUiLifecycle.cs source/GameInterface.Tests/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityTests.cs
git commit -m "fix(diplomacy): add fail-closed client UI lifecycle"
```

### Task 2: Wire campaign reset and trusted snapshot commit

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyPatchCompatibilityGate.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityHandler.cs`
- Modify: `source/GameInterface.Tests/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityTests.cs`
- Delete: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyUiManagerReadinessPatch.cs`
- Delete: `source/GameInterface/Services/WorkshopMods/Diplomacy/DiplomacyUiReadinessPatch.cs`

**Interfaces:**
- Consumes: Task 1 lifecycle methods and `DiplomacySnapshotApplyResult.Succeeded`.
- Produces: a prefix on supported `Diplomacy.SubModule.OnGameStart` that resets settings/UI before the original reads settings, and a post-commit call that exposes UI only after `revisionGate.Commit(snapshot)` succeeds.

- [ ] **Step 1: Write failing ordering tests**

Add tests proving campaign reset occurs before the original startup hook, successful trusted snapshot apply enables once, already-current snapshots preserve readiness, and malformed/stale/conflicting/apply-failed snapshots never enable.

- [ ] **Step 2: Run the focused tests and verify RED**

Expect lifecycle-call assertions to fail because the handler and campaign patch are not wired.

- [ ] **Step 3: Implement the ordering and remove suppression patches**

Change `DiplomacyCampaignPatchCleanup` to prefix-reset the settings bridge and UI lifecycle, then retain cleanup in the postfix. Call `MarkSnapshotReady()` only after runtime apply and revision commit. Remove `DiplomacyCivilWarUiRetirement`, manager-constructor guards, and screen/encyclopedia finalizers; their inventories are owned by the new lifecycle.

- [ ] **Step 4: Verify source and focused tests**

Run the focused tests, then search for `DiplomacyUiReadinessPatch`, `DiplomacyUiManagerReadinessPatch`, and `FilterTransientReadinessException`; expect no production matches.

- [ ] **Step 5: Commit**

```powershell
git add -A source/GameInterface/Services/WorkshopMods/Diplomacy source/GameInterface.Tests/Services/WorkshopMods/Diplomacy/DiplomacyCompatibilityTests.cs
git commit -m "fix(diplomacy): gate kingdom UI on trusted snapshot"
```
