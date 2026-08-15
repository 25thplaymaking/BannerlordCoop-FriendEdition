# Friend Edition Bannerlord v1.4.8 migration plan

Date: 2026-08-15

Owner: Friend Edition

Target: Bannerlord `v1.4.8`, War Sails disabled

Runbook: [`../../bannerlord-1.4.8-migration.md`](../../bannerlord-1.4.8-migration.md)

## Objective

Move the complete current Friend Edition feature set and exact ten-module server
contract from Bannerlord `v1.4.7` to `v1.4.8` without enabling War Sails,
reintroducing RBM, losing campaign state, weakening exact-binary/authority
controls, or mixing game/mod upgrades into one unverifiable cut.

## Baseline decisions

1. Reproduce upstream Bannerlord Coop PR #2919's `GameVersion` bump.
2. Keep War Sails and TaleWorlds `BirthAndDeath` disabled.
3. Preserve mod release identifiers that happen to contain `1.4.7` unless the
   corresponding payload changes.
4. Freeze all currently pinned Workshop payloads for the first base-game proof.
5. Adopt DismembermentPlus's explicit `v1.4.8` payload as the only required
   gameplay payload increment.
6. Keep Fourberie `v1.4.7.5` for the base cut; audit `v1.4.7.6` in a subsequent
   increment.
7. Keep the existing campaign and deploy only after a byte-verified rollback
   snapshot and canary proof.
8. Use the migration to finish the active mod-function piping: zero blocked,
   unsupported, unsafe-presentation, placeholder, or unclassified gameplay
   routes is a release gate.

## Phase 0 — source and documentation baseline

- [x] Inventory repository documentation and identify the owning docs for
  compatibility, integration, authority, packaging, deployment, and status.
- [x] Confirm upstream PR #2919 is a one-line `GameVersion` change with green
  build, unit, and eight-shard E2E checks.
- [x] Reconcile the live ten-module catalog against current Steam manifests.
- [x] Write the `v1.4.8` migration runbook and this phased plan.
- [x] Change `source/Coop/Coop.csproj` to `v1.4.8`.
- [x] Change Coop Workshop compatibility tags and tester-facing supported-version
  text to `v1.4.8`.
- [x] Record the migration in `doc/CHANGELOG.md` and `STATUS.md` without claiming
  a release or live deployment.

Exit: the branch clearly identifies the migration target and carries a complete,
reviewable execution contract. This phase alone is not release-ready.

## Phase 1 — acquire and reconcile Bannerlord v1.4.8

- [ ] Create isolated `v1.4.7` and `v1.4.8` client inventories.
- [ ] Acquire the matching `v1.4.8` dedicated-server distribution.
- [ ] Enumerate versions, sizes, and hashes of all official base/module DLLs used
  by Friend Edition and the server support closure.
- [ ] Confirm War Sails is absent from both activation lists.
- [ ] Diff managed public APIs plus the exact Harmony target shapes used by Coop.
- [ ] Generate a new `server-ui-support-sha256.json` from actual `v1.4.8` bytes;
  retain the old manifest in release provenance, not as the active record.

Exit: every official input is identified and the compatibility diff has no
unresolved target or reference changes.

## Phase 2 — base Coop and frozen-suite proof

- [ ] Build all production/test projects against `v1.4.8` assemblies.
- [ ] Run unit/integration suites and all eight E2E shards.
- [ ] Run exact Harmony target-resolution and module identity tests.
- [ ] Run development and release authority validation with the frozen ten-module
  payload set.
- [ ] Boot the dedicated server and one rendered client on an isolated copy of
  `friendallmods1`.
- [ ] Exercise connect, save transfer, map load, save/restart, field battle,
  mounted death, inventory drag-and-drop, and every mod initialization path.

Exit: the only unresolved compatibility input is a deliberately isolated mod
payload upgrade, not the base game.

## Phase 3 — module increments

### 3A. Reconcile the authority ledgers

- [ ] Regenerate the 41,050-method function inventory and authority evidence
  from the exact frozen payloads.
- [ ] Reconcile the 419-route Fourberie metadata ratchet with the strict
  495-route rejection result; publish one joined count and make it the only
  release gate.
- [ ] Require every active authority-sensitive method to resolve to one allowed
  disposition, named owner, live route, and focused test.
- [ ] Reject stale owner/test references, client-presentation paths that reach
  shared mutation, hidden blocked options, and placeholder-only handlers.
- [ ] Re-run the cross-mod single-owner matrix after every module increment.

Exit: the generated ledgers and human status documents agree exactly. No
success count is inferred from an older payload or a weaker validator.

### 3B. Framework layer

- [ ] Validate exact Harmony `v2.4.2.248`, ButterLib `v2.11.1`, UIExtenderEx
  `v2.13.3`, and MCM `v5.12.2` bytes on `v1.4.8`.
- [ ] Preserve the `Implementation.1.4.7`/`v1.4.7` assembly names if those exact
  framework releases remain compatible.
- [ ] If a framework changes, process it alone and regenerate every downstream
  receipt, overlay, dependency, and method-shape proof before continuing.

### 3C. Improved Garrisons closure re-proof

- [ ] Re-run all 723 exact candidate classifications and resolve every owner and
  test reference.
- [ ] Exercise all 29 management/template/mobile-party families through visible
  client options, including hostile encounters, building reserves, rollback,
  restart, and late join.
- [ ] Verify sidecar state is imported only by the server and canonical state is
  revisioned, replay-safe, and filtered to the authenticated clan.

### 3D. Diplomacy exact closure

- [ ] Reconcile the complete Diplomacy method ledger rather than accepting only
  the already-routed operation list.
- [ ] Prove all 66 settings, four managers, sixteen UI types, explicit player
  commands, server callbacks, messenger persistence, and Kingdom close/reopen.
- [ ] Keep Separatism as the single rebellion owner and prove war/peace,
  agreement, cooldown, exhaustion, fief, clan, influence, and callback
  collisions have exactly one mutation path.

### 3E. Player Settlement functional completion

- [ ] Replace the empty-state-only boundary with authenticated server-owned
  build, rebuild, and overwrite transactions.
- [ ] Register the full settlement/town/village/building graph in dependency
  order with stable IDs, validated placement/cost, rollback, and publication.
- [ ] Prove non-empty save/restart, late join, map visuals, parties, armies,
  sieges, capture, workshops, village binding, and safe disable/migration rules.

### 3F. Fourberie zero-open closure

- [ ] Close every strict-gate rejection, including the roughly 300 menu/dialog
  helpers that transitively reach shared state.
- [ ] Assign explicit authority/controller ownership to all mission callbacks,
  end-of-mission consequences, fourteen model outcomes, canonical writes,
  menus, conversations, and lifecycle hooks.
- [ ] Exercise every original visible action plus persistence, rollback,
  reconnect, mission teardown, save/restart, and two-client convergence.

### 3G. DismembermentPlus v1.4.8

- [ ] Archive Workshop manifest `751945004455697202` and calculate complete
  content/configuration/file hashes.
- [ ] Diff `SubModule.xml`, dependencies, assemblies, public/IL surfaces, assets,
  and Harmony targets against pinned manifest `4587731243779119835`.
- [ ] Regenerate and close all DismembermentPlus function/authority records.
- [ ] Update the combat compatibility family, exact identities, target shapes,
  catalog, deployment manifest, suite receipt, server role overlay, and focused
  tests as one atomic increment.
- [ ] Verify deterministic victim-authority cosmetic routing with no damage
  replay, including mounted death and ragdoll cases.

### 3H. Remaining closed modules

- [ ] Revalidate Unblockable Thrust `v1.1.3.1` and its four collision candidates.
- [ ] Revalidate Separatism's 57 candidates, four rebellion/union modes,
  fallen-clan transaction, rollback, and Diplomacy/vanilla collisions.
- [ ] Prove RBM remains absent from catalog, payload, capabilities, options, and
  both role orders.

Exit: the ten-module release set has no unknown bytes, unclassified authority
candidate, missing target, unsupported role, or unresolved save-shape change.

## Phase 4 — Fourberie v1.4.7.6 follow-on

Do not block the base `v1.4.8` release on this optional upstream drift. After the
base release is stable:

- [ ] Archive manifest `1598945672157391038` and reconcile permissions/assets.
- [ ] Re-run the complete Fourberie binary/function/authority/persistence audit.
- [ ] Update all exact identities, receipts, method shapes, canonical snapshot
  logic, tests, and docs together.
- [ ] Run the full Fourberie action/mission/menu/same-save acceptance matrix.
- [ ] Release and deploy as an independently reversible increment.

Exit: Fourberie upstream drift is removed without obscuring the base-game
migration's evidence.

## Phase 5 — package and dedicated-server pairing

- [ ] Regenerate `deploy/workshop-mods.json`, the code catalog, compact suite
  manifest, server UI support manifest, authority inventory, and dispositions
  from exact final inputs.
- [ ] Rebuild server module bins, loader/ButterLib patches, startup hook, and both
  release-paired `DedicatedServer.Core.dll` locations.
- [ ] Run Workshop builder validate-only, dry-run, fixture tests, dependency
  closure, source re-hash, and release authority validation.
- [ ] Build the client archive and server pair; verify every staged hash, receipt,
  load order, module role, and excluded duplicate.
- [ ] Confirm no War Sails module or DLL entered either payload.

Exit: one exact client archive and one exact paired server release pass all
offline gates.

## Phase 6 — same-save canary and promotion

- [ ] Stop production and reconcile the actual live inventory.
- [ ] Create and verify a complete immutable `v1.4.7` rollback snapshot.
- [ ] Deploy to a canary using a copy of `friendallmods1`.
- [ ] Require `SERVING`, UDP 4200, repeated pulses, zero restarts, clean release
  verification, save load, and autosave before rendered entry.
- [ ] Run the complete feature acceptance matrix in the runbook with at least two
  clients.
- [ ] Promote the exact canary bytes to the private stable feed and live server.
- [ ] Update `STATUS.md`, changelog, release provenance, hashes, workflow IDs,
  backup path, and honest rendered/live proof.

Exit: Friend Edition `v1.4.8` is deployed, reversible, and verified without War
Sails. Until every item in this phase is complete, documentation must say
"migration candidate" rather than "shipped" or "live".

## Required PR sequence

1. Source version + runbook/plan (this PR).
2. Official `v1.4.8` binary inventory/API/server-support update.
3. Authority-ledger reconciliation and common strict gate.
4. Improved Garrisons re-proof and Diplomacy exact closure.
5. Player Settlement construction/object-graph completion.
6. Fourberie zero-open model/menu/mission closure.
7. DismembermentPlus `v1.4.8` re-audit and payload update.
8. All-mod collision, package/pairing, and canary release evidence.
9. Fourberie `v1.4.7.6` follow-on after the base release is stable.

Each PR must keep a single input class, update the owning documentation, and
carry enough tests/provenance to roll back independently.
