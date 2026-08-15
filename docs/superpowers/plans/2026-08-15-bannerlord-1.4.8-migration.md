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

Current milestone: exact official/Workshop inputs acquired, client candidate
packaged, and offline server pair built. The finalization review reduced the
basic Fourberie audit to 47 unclassified methods, but the transitive gameplay
gate still rejects 272 records and the feature ledger remains 28/30. The
candidate is intentionally held for those exact consequence owners, rendered
and same-save acceptance, and live promotion. See the runbook's finalization
review for the bounded completion order.

## Baseline decisions

1. Reproduce upstream Bannerlord Coop PR #2919's `GameVersion` bump.
2. Keep War Sails and TaleWorlds `BirthAndDeath` disabled.
3. Preserve mod release identifiers that happen to contain `1.4.7` unless the
   corresponding payload changes.
4. Keep the eight unchanged Workshop manifests byte-pinned.
5. Adopt DismembermentPlus `v2.0.8.8` manifest `751945004455697202` as an
   independently audited gameplay payload increment.
6. Adopt Fourberie `v1.4.7.6` manifest `1598945672157391038` as a second
   independently audited payload increment in the same migration candidate.
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

- [x] Create isolated `v1.4.7` and `v1.4.8` client inventories.
- [x] Acquire the matching `v1.4.8` dedicated-server distribution.
- [x] Enumerate versions, sizes, and hashes of all official base/module DLLs used
  by Friend Edition and the server support closure.
- [x] Confirm War Sails is absent from both activation lists.
- [ ] Diff managed public APIs plus the exact Harmony target shapes used by Coop.
- [x] Generate a new `server-ui-support-sha256.json` from actual `v1.4.8` bytes;
  retain the old manifest in release provenance, not as the active record.

Exit: every official input is identified and the compatibility diff has no
unresolved target or reference changes.

## Phase 2 — base Coop and frozen-suite proof

- [x] Build all production/test projects against `v1.4.8` assemblies.
- [ ] Run unit/integration suites and all eight E2E shards.
- [x] Run exact Harmony target-resolution and module identity tests.
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

- [x] Regenerate the 41,050-method function inventory and authority evidence
  from the exact frozen payloads.
- [x] Supersede the stale 419/495 Fourberie reports with the exact current
  420-route joined result and make it the only current ratchet.
- [ ] Require every active authority-sensitive method to resolve to one allowed
  disposition, named owner, live route, and focused test.
- [x] Reject stale owner/test references, client-presentation paths that reach
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

- [x] Re-run all 723 exact candidate classifications and resolve every owner and
  test reference.
- [ ] Exercise all 29 management/template/mobile-party families through visible
  client options, including hostile encounters, building reserves, rollback,
  restart, and late join.
- [ ] Verify sidecar state is imported only by the server and canonical state is
  revisioned, replay-safe, and filtered to the authenticated clan.

### 3D. Diplomacy exact closure

- [x] Reconcile the complete Diplomacy method ledger rather than accepting only
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

### 3F. Fourberie v1.4.7.6 zero-open closure

- [x] Acquire exact Workshop manifest `1598945672157391038` and confirm module
  version `v1.4.7.6`.
- [x] Reconcile the new assembly hashes, metadata tokens, persisted fields,
  dependencies, and Harmony targets before carrying any prior disposition.
- [ ] Close every strict-gate rejection (272 in the current transitive gate,
  including 47 unclassified consequence methods), including menu/dialog
  helpers that transitively reach shared state.
- [ ] Assign explicit authority/controller ownership to all mission callbacks,
  end-of-mission consequences, fourteen model outcomes, canonical writes,
  menus, conversations, and lifecycle hooks.
- [ ] Exercise every original visible action plus persistence, rollback,
  reconnect, mission teardown, save/restart, and two-client convergence.

### 3G. DismembermentPlus v1.4.8

- [x] Archive Workshop manifest `751945004455697202` and calculate complete
  content/configuration/file hashes.
- [x] Diff `SubModule.xml`, dependencies, assemblies, public/IL surfaces, assets,
  and Harmony targets against pinned manifest `4587731243779119835`.
- [x] Regenerate and close all DismembermentPlus function/authority records.
- [x] Update the combat compatibility family, exact identities, target shapes,
  catalog, deployment manifest, suite receipt, server role overlay, and focused
  tests as one atomic increment.
- [ ] Verify deterministic victim-authority cosmetic routing with no damage
  replay, including mounted death and ragdoll cases.

### 3H. Remaining closed modules

- [x] Revalidate Unblockable Thrust `v1.1.3.1` and its four collision candidates.
- [ ] Revalidate Separatism's 57 candidates, four rebellion/union modes,
  fallen-clan transaction, rollback, and Diplomacy/vanilla collisions.
- [x] Prove RBM remains absent from catalog, payload, capabilities, options, and
  both role orders.

Exit: the ten-module release set has no unknown bytes, unclassified authority
candidate, missing target, unsupported role, or unresolved save-shape change.

## Phase 4 — package and dedicated-server pairing

- [x] Regenerate `deploy/workshop-mods.json`, the code catalog, compact suite
  manifest, server UI support manifest, authority inventory, and dispositions
  from exact final inputs.
- [ ] Rebuild server module bins, loader/ButterLib patches, startup hook, and both
  release-paired `DedicatedServer.Core.dll` locations.
- [x] Run Workshop builder validate-only, dry-run, fixture tests, dependency
  closure, and source re-hash.
- [ ] Pass zero-open release authority validation (currently held by 47
  unclassified and 272 transitive Fourberie gameplay records).
- [ ] Build the client archive and server pair; verify every staged hash, receipt,
  load order, module role, and excluded duplicate.
- [x] Confirm no War Sails module or DLL entered the client payload or acquired
  dedicated-server distribution; repeat this check after final server pairing.

Exit: one exact client archive and one exact paired server release pass all
offline gates.

## Phase 5 — same-save canary and promotion

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

## Required increment sequence in this PR

1. Source version + runbook/plan.
2. Official `v1.4.8` client/server inventory, API diff, and server-support update.
3. DismembermentPlus `v2.0.8.8` payload re-audit.
4. Fourberie `v1.4.7.6` payload re-audit and zero-open closure.
5. Authority-ledger reconciliation and common strict gate for all modules.
6. Improved Garrisons, Diplomacy, Player Settlement, Unblockable Thrust, and
   Separatism functional re-proof/closure.
7. All-mod collision, package/pairing, and canary evidence.

Each increment must update its owning documentation and carry enough
tests/provenance to distinguish and roll back the changed input even though the
completed migration is reviewed in one PR.
