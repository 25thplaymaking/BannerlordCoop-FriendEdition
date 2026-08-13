# Encounter Completion Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the looter/villager scan and scoreboard exception suppression with explicit, authenticated completion and pre-destruction presentation transitions.

**Architecture:** The client’s native “Capture the enemy” consequence becomes a typed command whose server handler validates the requesting peer, controlled party, exact map event, defeated opposing side, and unclaimed battle before publishing the existing authoritative conclusion event. Immediately before a client destroys an involved map event, it asks the mission’s registered battle observer to latch results while `PlayerEncounter.Battle` still exists.

**Tech Stack:** C# 10, .NET Standard 2.0, Harmony, LiteNetLib, Bannerlord mission APIs, xUnit/E2E.

**Spec:** `docs/coop-next-update-bugsweep.md`

## Global Constraints

- Do not infer battle completion from a global campaign tick scan.
- Do not accept a completion request from a peer that does not control the named player party.
- Do not conclude PvP, settlement, active mission, active simulation, mutual-wipeout, or non-defeated battles through the capture command.
- Do not swallow scoreboard exceptions.
- Preserve the existing authoritative result commit, capture, finalize, and encounter-close pipeline.

---

### Task 1: Route “Capture the enemy” explicitly

**Files:**
- Create: `source/GameInterface/Services/MapEvents/Messages/NetworkCaptureDefeatedEnemy.cs`
- Create: `source/GameInterface/Services/MapEvents/Handlers/CaptureDefeatedEnemyHandler.cs`
- Replace: `source/GameInterface/Services/MapEvents/Patches/Disable/DisableEncounterCaptureTheEnemyOnConsequence.cs`
- Delete: `source/GameInterface/Services/MapEvents/Handlers/BanditEncounterAutoConcludeHandler.cs`
- Create: `source/GameInterface.Tests/Services/MapEvents/CaptureDefeatedEnemyTests.cs`

**Interfaces:**
- Consumes: `IPlayerManager`, `IObjectManager`, `IBattleHostRegistry`, `ServerBattleModeArbiter`, and `AuthoritativeBattleConclusionRequested`.
- Produces: `NetworkCaptureDefeatedEnemy(string mapEventId, string playerPartyId)` and `CaptureDefeatedEnemyHandler.TryResolveWinner(MapEvent, MobileParty, out BattleState)`.

- [ ] **Step 1: Write failing validation tests**

Cover an authenticated player victory and rejection for spoofed party, healthy enemy, opposing player, settlement battle, mutual wipeout, finalized battle, active mission/simulation claim, and duplicate/already-concluded state.

- [ ] **Step 2: Run the focused tests and verify RED**

Run `GameInterface.Tests.Services.MapEvents.CaptureDefeatedEnemyTests`; expect missing type failures.

- [ ] **Step 3: Implement the typed request and server validation**

The Harmony prefix resolves the local map-event and `MobileParty.MainParty` ids, sends the command, and skips the native local mutation. The server verifies `payload.Who` maps to a player whose `MobilePartyId` exactly matches the request, resolves both objects, verifies membership and the pure winner rule, rejects claimed/hosted battles, then publishes `AuthoritativeBattleConclusionRequested(mapEventId, winner, 0)`.

- [ ] **Step 4: Run focused and existing battle-finalization tests**

Require the new unit class plus `CoopBattleFinalizeTests` and `BattleResultDistributionTests` to pass.

- [ ] **Step 5: Commit**

```powershell
git add -A source/GameInterface/Services/MapEvents source/GameInterface.Tests/Services/MapEvents/CaptureDefeatedEnemyTests.cs
git commit -m "fix(encounters): route defeated-enemy completion explicitly"
```

### Task 2: Latch scoreboard results before map-event destruction

**Files:**
- Create: `source/GameInterface/Services/MapEvents/BattlePresentationTeardown.cs`
- Modify: `source/GameInterface/Services/MapEvents/MapEventRegistry.cs`
- Delete: `source/GameInterface/Services/UI/Patches/ScoreboardTickReadinessPatch.cs`
- Create: `source/GameInterface.Tests/Services/MapEvents/BattlePresentationTeardownTests.cs`

**Interfaces:**
- Consumes: `Mission.Current.GetMissionBehavior<BattleObserverMissionLogic>()` and `IBattleObserver.BattleResultsReady()`.
- Produces: `BattlePresentationTeardown.PrepareBeforeDestroy(MapEvent, bool localPartyWasInvolved)` and a pure eligibility rule exercised headlessly.

- [ ] **Step 1: Write failing ordering and eligibility tests**

Prove the presentation callback runs before `IMapEventInitializationBarrier.DestroyGraph`, exactly once for an involved active battle mission, and never for uninvolved, simulation-only, missing-observer, or unrelated map events.

- [ ] **Step 2: Run the focused tests and verify RED**

Expect missing teardown type/order assertions.

- [ ] **Step 3: Implement pre-destruction result latching**

Call the current mission’s `BattleObserverMissionLogic.BattleObserver.BattleResultsReady()` before reward capture/graph destruction while the authoritative map-event state and `PlayerEncounter.Battle` are still readable. Make the call idempotent per map event and log failures without claiming teardown succeeded; graph cleanup still proceeds.

- [ ] **Step 4: Remove the scoreboard finalizer and verify**

Run the focused tests and search production source for `ScoreboardTickReadinessPatch` and `FilterTransientScoreboardException`; expect no matches.

- [ ] **Step 5: Commit**

```powershell
git add -A source/GameInterface/Services/MapEvents source/GameInterface/Services/UI/Patches/ScoreboardTickReadinessPatch.cs source/GameInterface.Tests/Services/MapEvents/BattlePresentationTeardownTests.cs
git commit -m "fix(battles): latch scoreboard before result teardown"
```
