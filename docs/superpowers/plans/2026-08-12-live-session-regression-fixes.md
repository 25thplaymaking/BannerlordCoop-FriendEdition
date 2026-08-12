# Live-session regression fixes implementation plan

> Local patch only. Do not push, deploy, restart, signal, or otherwise alter the live server. Do not create, migrate, or replace the campaign save.

**Goal:** Fix the four regressions reproduced in the August 11 play session while preserving the current save: hideout boss-stage reconnect loops, Fourberie safehouse prisoner/item transfers, Send Troops loot loss, and tournament skill awards.

**Compatibility:** All changes are runtime/network behavior changes. They must not add persisted campaign fields or require a new save. Fourberie protocol additions must remain protobuf-compatible for the paired launcher bundle and bump the adapter contract only if its serialized state shape changes.

## Task 1: Abort unresumable hideout map events on disconnect

**Files:**
- Modify: `source/Coop.Core/Server/Services/Players/Handlers/PlayerPartyVisibilityHandler.cs`
- Test: `source/Coop.Tests/Server/Services/Players/PlayerPartyVisibilityHandlerTests.cs` or `source/E2E.Tests/Services/MapEvents/HideoutMapEventTests.cs`

1. Add a failing regression test that disconnecting the controlling player during a hideout map event finalizes the event and parks the party, rather than retaining a resumable map-event reference.
2. Run the narrow test and confirm the old behavior fails the new assertion.
3. Special-case `MapEventType.Hideout` in the disconnect parking path: record the deferred party, publish the existing disconnect notification, and request authoritative map-event finalization. Leave field battles and sieges on the existing reconnectable path.
4. Verify the finalization callback clears the map event and completes the deferred park without awarding a victory.
5. Run the narrow hideout/disconnect tests and the broader player-visibility tests.
6. Commit the verified increment locally.

## Task 2: Preserve auto-resolve battle rewards through map-event destruction

**Files:**
- Modify: `source/GameInterface/Services/MapEvents/MapEventRegistry.cs`
- Test: `source/E2E.Tests/Services/MapEvents/CoopBattleFinalizeTests.cs`

1. Add a failing Send Troops regression test that applies authoritative results, destroys the completed map event with no battle mission active, and asserts the winner remains in `PlayerEncounterState.CaptureHeroes` with loot/prisoner/member rosters staged.
2. Run the narrow test and confirm the map-event destruction fallback currently closes and nulls the encounter.
3. Make the destruction fallback preserve an encounter already staged at `CaptureHeroes`; keep its existing cleanup for abandoned/non-result encounters.
4. Run the new test plus existing map-event finalization/leave tests to prove manual and abandoned encounters still close correctly.
5. Commit the verified increment locally.

## Task 3: Make Fourberie safehouse transfers authoritative and observable

**Files:**
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieRecruitMessages.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieAuthority.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieCompatibilityHandler.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieOperations.cs`
- Modify: `source/GameInterface/Services/WorkshopMods/Fourberie/FourberieStateMessages.cs` only if an explicit stable-ID safehouse roster snapshot is required for late join/reconciliation
- Modify: `source/GameInterface/Services/Inventory/Patches/InventoryLogicPatches.cs`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Fourberie/FourberieAuthorityTests.cs`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Fourberie/FourberieOperationProtocolTests.cs`
- Test: `source/GameInterface.Tests/Services/WorkshopMods/Fourberie/FourberieStateCodecTests.cs` if state shape changes

1. Add a failing prisoner test proving the server-side prisoner removal, generated loot, and Roguery XP run through Coop's normal mutation publishers instead of being hidden inside `AllowedThread`.
2. Remove `AllowedThread` from successful authoritative prisoner mutations while retaining the reversible transaction/rollback boundary. Use Fourberie's canonical crime-base settlement on the client and return a visible rejection when a request cannot be submitted.
3. Add failing protocol tests for a `TransferSafehouseItems` operation carrying bounded, deduplicated stable item IDs, optional modifier IDs, and signed deltas.
4. Extend the request protocol and command-key deduplication for item selections without changing existing operation semantics.
5. Intercept Fourberie's stash `InventoryLogic.DoneLogic` before generic `TradeAttempted`: recognize the canonical crime-base party roster, derive net deposits/withdrawals, submit the explicit operation, reset the speculative local inventory, and suppress the generic unresolved-roster route.
6. Add failing authority tests for valid deposit/withdrawal and rejection of overdrawn, foreign-settlement, malformed, or duplicated transfers.
7. Apply the item deltas atomically on the server between the authenticated player's roster and the canonical safehouse roster, with the ordinary Coop item-roster publishers enabled.
8. If roster identity cannot be guaranteed on reconnect/late join, add a stable-ID safehouse item snapshot to Fourberie's authoritative state, include it in validation/fingerprinting, and apply it transactionally on clients. Do not add save data.
9. Run all Fourberie unit tests and the GameInterface test project.
10. Commit the verified increment locally.

## Task 4: Award tournament skill XP per participant

**Files:**
- Modify: `source/GameInterface/Services/Tournaments/Handlers/TournamentSessionHandler.cs`
- Modify: `source/GameInterface/Services/Tournaments/Handlers/TournamentHitProgressionHandler.cs`
- Test: `source/GameInterface.Tests/Services/Tournaments/TournamentHitProgressionCleanupTests.cs`
- Test: `source/GameInterface.Tests/Services/Tournaments/TournamentCompletionTransactionTests.cs`

1. Add failing tests proving one participant's accepted live hit does not suppress fallback tournament XP for another participant, and that a participant with accepted hit progression is not double-awarded.
2. Replace the session-wide live-combat flag with per-session/per-controller coverage recorded only after a hit progression message is accepted.
3. Remove the unconditional coverage mark from match-result acceptance.
4. At tournament completion, apply fallback progression only to eligible hero participants without accepted hit coverage; retain the existing all-participant path for fully simulated tournaments.
5. Clear coverage on completion, cancellation, and session teardown.
6. Run the tournament test suite and GameInterface tests.
7. Commit the verified increment locally.

## Task 5: Verify and prepare the local draft

**Files:**
- Modify: `STATUS.md`
- Modify: relevant `docs/` contracts if behavior descriptions changed

1. Update `STATUS.md` to replace the stale claim that the earlier hideout/Fourberie fixes were confirmed with the actual regression causes and local-patch status.
2. Build `source/Coop.sln` with `"C:\Program Files\dotnet\dotnet.exe"` and run all affected test projects.
3. Inspect `git diff --check`, `git status --short`, and the local commit list; ensure the user's pre-existing mission-file edits and `work/` directory were never staged.
4. Confirm no save-schema or migration files changed.
5. Stop and report the local branch/commits and verification evidence. Do not push or deploy until Bryce explicitly authorizes it.
