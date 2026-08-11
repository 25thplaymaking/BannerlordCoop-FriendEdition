# Combat and Separatism Authority Implementation Plan

> Execution scope: finish the three smallest gameplay-module authority slices without widening the approved architecture. Every changed behavior must be test-first, server-authorized where it mutates shared state, and represented by an exact authority disposition.

## Task 1: Certify UnblockableThrust as a pure authoritative rule

**Files:**
- Modify: `build/workshop/authority-dispositions.json`
- Modify: focused combat authority tests under `source/Missions.Tests/`
- Regenerate: `doc/generated/workshop-authority-audit.json`

1. Add a failing exact-audit test proving all four active UnblockableThrust methods have a reviewed disposition, owner, and executable test evidence.
2. Extend the pure-rule tests for inactive module, non-thrust, shield, source authority, foot, and mounted cases.
3. Add the exact `PurePolicy` disposition records owned by `UnblockableThrustAuthorityPatch.ResolveCrushThrough`.
4. Run the focused tests and development authority validator.

## Task 2: Replicate DismembermentPlus presentation from the accepted blow

**Files:**
- Modify: `source/Missions/WorkshopMods/Combat/CombatModAuthorityPolicy.cs`
- Modify: `source/Missions/WorkshopMods/Combat/CombatModHarmonyPatches.cs`
- Create/modify: Dismemberment presentation messages and handler under `source/Missions/WorkshopMods/Combat/`
- Modify: capability source under `source/Missions/WorkshopMods/Combat/`
- Modify: focused unit and E2E combat tests
- Modify: `build/workshop/authority-dispositions.json`

1. Add failing tests for a bounded event containing stable attacker/victim identity, selected body part and deterministic seed; reject malformed, duplicate and stale events.
2. Add failing convergence tests proving the authoritative accepted-blow peer publishes once and all clients apply the same cosmetic event without applying gameplay damage.
3. Implement the typed replicated-cosmetic event, dedupe window and client-only presentation adapter.
4. Keep slow motion local-disabled during Coop and expose an enabled server capability only when the audited binary and presentation route are available.
5. Replace the live-Coop blanket guard with the replicated route and classify every DismembermentPlus candidate exactly.
6. Run focused tests, build, and authority validators.

## Task 3: Restore Separatism's omitted fallen-clan conversation

**Files:**
- Modify: `source/GameInterface/Services/Separatism/SeparatismCampaignBehavior.cs`
- Modify: `source/GameInterface/Services/Separatism/SeparatismCampaignService.cs`
- Create: typed request/result messages and handler under `source/GameInterface/Services/Separatism/`
- Modify/create: Separatism capability source
- Modify: unit and E2E Separatism tests
- Modify: `build/workshop/authority-dispositions.json`

1. Add failing tests for the original conversation visibility conditions and capability gating.
2. Add failing authorization tests for trusted `NetPeer`, controlled actor re-derivation, target stable ID, kingdom/revision preconditions, war/minor-faction/ruler checks, duplicate request replay, and rollback.
3. Register the original player line on clients; make its consequence send only the typed request.
4. Perform the clan-to-kingdom transition on the server through the existing Separatism/Coop membership funnel and return a typed result for clean UI recovery.
5. Publish the capability only when Separatism is enabled and the server route is ready.
6. Classify all Separatism candidates exactly, with structural campaign methods owned by the existing server callback and the restored conversation owned by the new server command.
7. Run focused tests, build, development/release authority validators, and the affected E2E slice.

## Task 4: Verify and publish the increment

1. Regenerate workshop inventories and exact authority audit.
2. Run all affected unit/integration/E2E suites.
3. Update `STATUS.md`, `doc/WorkshopFunctionReview.md`, and `doc/CHANGELOG.md` to remove stale blocked/omitted claims.
4. Commit and push only the verified increment; do not include `work/`.

