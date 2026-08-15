# Bannerlord v1.4.8 migration guide

This is the release runbook for moving Bannerlord Coop Friend Edition from the
Bannerlord `v1.4.7` base game to `v1.4.8` while preserving the current ten-module
private server feature set.

## Scope and release status

- Target base game: Mount & Blade II: Bannerlord `v1.4.8`.
- War Sails: disabled and out of scope. Do not add its module, assets, APIs, or
  naval state to this migration.
- Current campaign: preserve the existing `friendallmods1` save unless an
  isolated copy proves that Bannerlord itself requires a migration.
- Workshop contract: the ten modules in
  `FriendEditionWorkshopModuleCatalog`; RBM remains retired and absent.
- Distribution: the existing authorized three-person private Friend Edition
  distribution only.

Changing `<GameVersion>` and the Workshop tag makes a source build identify as
`v1.4.8`; it does not make a Friend Edition release ready. Release readiness
requires the binary, authority, packaging, same-save, and rendered-client gates
in this guide.

### Verified offline candidate — 2026-08-15

The migration inputs and client payload have now been assembled and verified
offline. The machine-readable source of truth is
[`../deploy/bannerlord-1.4.8-inputs.json`](../deploy/bannerlord-1.4.8-inputs.json).

- Client app `261550` is public build `24573425`; the complete 27,259-file
  inventory hashes to
  `152e19803cc17f0424922bb7183299c18d855c68852e3cb8079a41d04a63c972`.
- Dedicated-server app `1863440` is build `24571419`, depot `1863441`, manifest
  `4619456710482553639`; its 3,029-file inventory hashes to
  `688479286fe624e31d989f1727555c9e471dfa7f4f570a03406367067131dd97`.
- The complete rollback snapshot of client build `24127665` contains 27,251
  files and hashes to
  `238c50dd0f543e1b9fbe484e7002316b30d1dbbb401773ed4bd99b72bdcbbffa`.
- All ten exact Workshop manifests were acquired. DismembermentPlus moved to
  `v2.0.8.8`/`751945004455697202`; Fourberie moved to
  `v1.4.7.6`/`1598945672157391038`; the other eight pins remain unchanged.
- The verified client suite contains 847 files. Its ZIP SHA-256 is
  `bbdc850913fb3d943c8105ee26e6ee1eb2ce33c5c9df647c73e84850970bc825`;
  its root manifest SHA-256 is
  `b7274557638e8bfe96eea8de9d22fb972feafc79ac9f3557e6392ccb5f123a64`.
- The production solution builds against the `v1.4.8` assemblies with zero
  errors. Workshop packaging/receipt tests and dedicated-server overlay tests
  pass. The actual `v1.4.8` `TaleWorlds.Library.dll` loader boundary patches
  exactly once and the 16-file server UI-support closure has been repinned.
- A Serilog-2-compatible headless Coop bin was built and release-paired offline.
  The paired `DedicatedServer.Core.dll` hashes to
  `6b3ed5a858aaf3afcab1f7770d76ef976e8bdc097adfcc374d368b4697daf74c`;
  its pairing receipt hashes to
  `8b93ba231e9f822bf0a77b4c3688544b713ca0c1e45f78e0d1a8b634fbdf6c2f`.
  This proves binary pairing, not a successful server boot.
- The regenerated authority ledger contains 41,050 methods and 13,729 required
  candidates: 13,309 classified, 420 unclassified, and zero blocked. Every exact
  candidate for DismembermentPlus, Diplomacy, Improved Garrisons, Player
  Settlement, Separatism, and Unblockable Thrust passes its current structural
  validator. All 420 open routes are Fourberie mission/menu/behavior/VM work.
- `Modules/NavalDLC` is absent and War Sails remains disabled in the staged
  configuration and payload.

This is an offline migration candidate, not a release. The paired Friend
Edition server has not booted on an isolated save or passed rendered multi-client
acceptance, or touched the live server/save. The 420 Fourberie routes and
Player Settlement's deliberately blocked non-empty construction path remain
functional-completion work even though the `v1.4.8` compilation and package
compatibility checks pass.

## Evidence reviewed

The migration is based on the repository's existing documentation and release
controls rather than a new parallel process:

- [`../README.md`](../README.md) defines the supported game version and keeps
  War Sails disabled.
- [`../doc/FriendEdition.md`](../doc/FriendEdition.md) records the private-fork
  scope, same-save practice, live acceptance checks, and rollback evidence.
- [`../doc/COOP-MOD-INTEGRATION.md`](../doc/COOP-MOD-INTEGRATION.md) defines the
  client/server split for Workshop mods.
- [`../doc/COOP-OPS-WORKFLOW.md`](../doc/COOP-OPS-WORKFLOW.md) owns build,
  verification, packaging, and deployment discipline.
- [`../doc/WorkshopModIntegrationAudit.md`](../doc/WorkshopModIntegrationAudit.md)
  and [`../doc/WorkshopFunctionReview.md`](../doc/WorkshopFunctionReview.md)
  own exact binary/method-shape and authority evidence.
- [`../tools/WorkshopIntegration/README.md`](../tools/WorkshopIntegration/README.md)
  owns suite receipts, hashing, packaging, and release authority validation.
- [`../tools/CoopServerModKit/README.md`](../tools/CoopServerModKit/README.md)
  owns dedicated-server overlays and release pairing.
- [`../STATUS.md`](../STATUS.md) is the live program board and must be updated at
  each verified migration milestone.

Upstream Bannerlord Coop PR
[`#2919`](https://github.com/Bannerlord-Coop-Team/BannerlordCoop/pull/2919)
changed only `source/Coop/Coop.csproj` from `v1.4.7` to `v1.4.8`. Its build,
unit-test, and eight E2E jobs passed. Friend Edition carries substantially more
binary-specific compatibility code, so that upstream result is the base-Coop
starting point, not proof for the modded suite.

The [official TaleWorlds patch
post](https://www.reddit.com/r/mountandblade/comments/1vkj1bf/war_sails_modding_kit_release_patch_ws_v128_bl/)
describes Bannerlord `v1.4.8` as a narrow base-game patch. Its reported runtime changes
touch corrupt-file/shader recovery, mounted-agent death presentation, ragdolls,
UI drag-and-drop positioning, and multiplayer decal reset. The nearby Friend
Edition risk areas are therefore mounted-puppet death, DismembermentPlus,
mission presentation, and mod-owned inventory/configuration screens. No War
Sails behavior is required to exercise these paths.

The module matrix also records the current Steam evidence rather than inferring
compatibility from a title: DismembermentPlus's
[change log](https://steamcommunity.com/sharedfiles/filedetails/changelog/2875093027)
explicitly names Bannerlord `v1.4.8` and MCM `5.12.*`; the
[Fourberie item](https://steamcommunity.com/sharedfiles/filedetails/?id=2875710877)
and [Diplomacy item](https://steamcommunity.com/sharedfiles/filedetails/?id=2881380744)
remain separate mod release lines whose `1.4.7` identifiers are not base-game
pins.

## Version names: what changes and what does not

The following are different namespaces and must not be rewritten as if they are
all the Bannerlord version:

- `GameVersion` and the Coop Workshop compatibility tag are base-game pins and
  move to `v1.4.8`.
- `Bannerlord.Diplomacy.1.4.7` is the pinned Diplomacy implementation assembly.
- `Fourberie v1.4.7.6` is the pinned Fourberie module release.
- `Bannerlord.ButterLib.Implementation.1.4.7` and
  `Bannerlord.MBOptionScreen.v1.4.7` are loader-selected framework implementation
  assemblies contained in the currently pinned framework releases.
- Comments that say an API first changed in Bannerlord `v1.4.7` are historical
  implementation notes, not active compatibility pins.

Only change a mod assembly name, module version, hash, method-shape contract, or
Steam manifest when the corresponding mod payload is deliberately upgraded and
re-audited.

## Ten-module upgrade matrix

The upstream state below was reconciled on 2026-08-15. Query Steam again before
cutting a release; a new manifest is an input change, never an automatic upgrade.

| Module | Friend Edition pin | Upstream state at review | v1.4.8 action | Risk and required gate |
| --- | --- | --- | --- | --- |
| Harmony | `v2.4.2.248`, manifest `5023964903723709557` | Same release/manifest; tags stop at `v1.4.7` | Keep exact bytes and verify startup on `v1.4.8` | Low. Prove one Harmony provider, wrapper/runtime hashes, and load order. |
| ButterLib | `v2.11.1`, manifest `6795008217820882669` | Same release/manifest; contains `Implementation.1.4.7` | Keep exact bytes; do not rename the selected implementation | Medium. Client and server loaders, lifecycle patches, and server overlay must pass exact shape/hash checks. |
| UIExtenderEx | `v2.13.3`, manifest `4162172930197019416` | Same release/manifest | Keep exact bytes | Low/medium. Render the Diplomacy UI lifecycle and verify no stale mixin callbacks. |
| MCM v5 | `v5.12.2`, manifest `4045451207505706745` | Same release/manifest; contains `v1.4.7` implementation | Keep exact bytes; do not rename the selected implementation | Medium. Verify authoritative settings fingerprint and client-only presentation split. |
| Improved Garrisons | `v4.2.0.7`, manifest `5143458534246082850` | Same manifest; Workshop compatibility tag remains `v1.4.5` | Keep exact audited payload and revalidate on `v1.4.8` | Medium. All 723 authority candidates, management commands, parties, persistence, and late join remain closed. |
| DismembermentPlus | `v2.0.8.8`, manifest `751945004455697202` | Exact current Workshop payload, explicitly synchronized to Bannerlord `v1.4.8` and MCM `5.12.*` | Adopt and audit the `v1.4.8` payload | High but localized. Recompute all file/assembly hashes, re-decompile, reclassify the authority candidates, revalidate `OnRegisterBlow` and visual calls, update receipts/catalog/tests, and render mounted and unmounted deaths. The new payload no longer bundles TaleWorlds assemblies. |
| Fourberie | `v1.4.7.6`, manifest `1598945672157391038` | Exact current Workshop payload; the author describes the line as compatible with `v1.4.7` and later | Adopt and audit `v1.4.7.6` in the complete migration | High. Its persisted field shape, eight behaviors, fourteen models, explicit operation families, mission/menu routes, and exact Harmony isolation contract must all be re-proven. |
| Diplomacy | `v1.4.7`, manifest `3938505074920035905` | Latest GitHub release remains `v1.4.7`; no `v1.4.8` payload exists | Keep exact payload and implementation assembly name | High-coupling validation, no payload upgrade. Re-prove 66 settings, four managers, sixteen UI extension types, commands/callbacks, persistence, and Kingdom screen lifecycle. |
| Unblockable Thrust | `v1.1.3.1`, manifest `3108412629025003964` | Same manifest; Workshop advertises through `v1.4.7` | Keep exact payload and revalidate | Low. Re-prove its four candidates inside Coop's collision authority and run thrust combat cases. |
| Player Settlement | `v7.5.0`, manifest `6398100776119441137` | Same manifest; page advertises base `v1.4.5` and later DLC support | Keep exact audited payload and revalidate without enabling War Sails | Medium/high. Verify load-before-Coop, empty and populated persistence, dynamic objects, siege behavior, snapshots, and the existing construction fail-closed boundary. |

RBM is not in the ten-module contract or launcher token. Its current upstream
version and War Sails behavior are irrelevant to this migration and must not be
added back incidentally.

## Function-piping completion gate

Compatibility is not the finish line. The regenerated exact-payload ledger now
provides one result: 41,050 methods, 13,729 authority candidates, 13,309
classified, 420 unclassified, and zero blocked. The earlier 419-route metadata
and 495-route strict reports are superseded; their disagreement came from stale
Fourberie payload/token data and a separate presentation filter. All 420 current
open candidates are in Fourberie. Stable requires zero active `Blocked`, `Unsupported`,
`GuardedFeatureBlocked`, `NotAllowed`, placeholder, unsafe-presentation, or
unclassified dispositions.

Every reachable function must end in exactly one named disposition:
`ClientPresentation`, `PurePolicy`, `ServerCallback`, `ServerCommand`,
`ReplicatedCosmetic`, `CoopOwnerReplacement`, `FrameworkLifecycle`, or
`Retired`. A classification counts only when its owner resolves and its focused
authorization, replay/idempotency, rollback, persistence, late-join, and
two-client convergence tests exist.

| Surface | Recorded starting point | Required completion in the v1.4.8 program |
| --- | --- | --- |
| Harmony, ButterLib, UIExtenderEx, MCM | Framework lifecycle and client/server split exist | Re-inventory exact binaries; prove one Harmony owner, headless UI isolation, teardown, server-owned settings, and every loader/method-shape gate. Framework helpers do not receive fake server commands. |
| Improved Garrisons | All 723 candidates are recorded closed and management routes exist | Re-run the exact gate and exercise all 29 management/template/mobile-party families, background ticks, sidecar import, hostile encounters, rollback, restart, and late join. Fix any route that only passes structurally but fails the visible option. |
| DismembermentPlus | All 17 candidates close against `v2.0.8.8`; its public shape remains 286 methods/19 types | Render and re-prove the accepted-blow cosmetic route, dedupe, mounted deaths, ragdolls, teardown, and absence of damage replay on the new game build. |
| Fourberie | Forty-seven operation families are routed; the exact `v1.4.7.6` ledger has 420 open routes | Close all UI/mission/lifecycle routes, including menu/dialog helpers that reach shared state, every mission callback and consequence, all fourteen model outcomes, canonical writes, persistence, and save/restart. Zero-open is required even if an option was previously hidden. |
| Diplomacy | Donate/fief/messenger/peace/war/alliance/pact operations and server callbacks are routed | Close the complete exact ledger; prove all 66 settings, four managers, sixteen UI types, agreement/exhaustion/cooldown callbacks, Kingdom UI lifecycle, persistence, and the single-owner boundary where Separatism owns rebellion mutation. |
| Unblockable Thrust | All four candidates are closed | Re-run exact-shape proof and the foot/mounted, shield, parry, chamber, remote-agent, malformed-config, and accepted-blow integration matrix. |
| Player Settlement | Exact runtime and empty-state lifecycle load; non-empty construction is blocked | Implement server-owned build/rebuild/overwrite and the complete dynamic settlement/town/village/building graph. Prove stable IDs, payment rollback, placement rules, ordered registration, persistence, restart, late join, map visuals, armies, sieges, capture, and safe disable/migration rules. |
| Separatism | All 57 candidates are closed | Re-run exact closure, four rebellion/union modes, fallen-clan command, rollback, persistence, and every Diplomacy/vanilla collision test. Clients must never own structural kingdom mutation. |
| RBM | Retired | Prove it remains absent from catalog, package, capability snapshot, options, and active load order. Do not spend migration work reviving it. |

The owning evidence document is
[`../doc/WorkshopFunctionReview.md`](../doc/WorkshopFunctionReview.md). Update it
from generated artifacts at each module increment; do not hand-edit success
counts before the validator produces them.

## Required migration sequence

### 1. Reconcile official game inputs

1. Update an isolated client installation to Bannerlord `v1.4.8`.
2. Keep War Sails and `BirthAndDeath` disabled.
3. Enumerate `Native`, `SandBoxCore`, `Sandbox`, `CustomBattle`, `StoryMode`, and
   every managed DLL used by the build; record version, size, and SHA-256.
4. Obtain the matching dedicated-server distribution and record the same
   inventory. Client and server changesets may differ, but their semantic game
   versions must both be `v1.4.8`.
5. Preserve the complete `v1.4.7` client/server inventories for rollback.

Do not edit the `v1.4.7.117484` server UI support digest in place with guessed
hashes. Generate a new reconciled `v1.4.8` record from actual binaries.

### 2. Prove base Coop before changing mod payloads

1. Build all Friend Edition projects against the `v1.4.8` assemblies.
2. Diff the public method/type surfaces of referenced `v1.4.7` and `v1.4.8`
   TaleWorlds/SandBox assemblies.
3. Resolve and verify every Harmony target. Pay special attention to agent
   death, mounted-puppet repair, ragdoll/dismemberment presentation, tournament
   setup, async save loading, and inventory drag-and-drop.
4. Run unit, integration, and all eight E2E shards with the ten Workshop payloads
   still byte-identical to the last audited release.
5. Boot server plus rendered client on a disposable copy of the current save.

This isolates a base-game regression from a mod binary regression.

### 2A. Reconcile and close the function ledgers

1. Regenerate the function inventory and authority evidence from the exact
   frozen ten-module payload set.
2. Join every manifest method to one disposition, named owner, route, and test;
   use the regenerated 420-route Fourberie result as the only current ratchet.
3. Reject stale owner/test references and any classification whose transitive IL
   still reaches shared mutation from a client-presentation path.
4. Complete the mod slices in dependency order: framework lifecycle, Improved
   Garrisons re-proof, Diplomacy exact closure, Player Settlement construction,
   Fourberie models/menus/missions, then the changed DismembermentPlus payload.
5. Run the all-mod collision matrix after each slice so a newly completed route
   cannot create a second owner for war, clan movement, parties, settlements,
   persistence, collision/damage, or mission consequences.

The base game cannot be promoted while an existing mod feature remains merely
hidden or fail-closed. Temporary hiding is acceptable only on development
commits while that module's completion slice is in progress.

### 3. Validate frameworks as one dependency layer

Use the existing exact Harmony, ButterLib, UIExtenderEx, and MCM payloads first.
If they load and pass the framework compatibility manifest, do not upgrade them
merely because their implementation filenames contain `1.4.7`. If any framework
must change, upgrade the framework layer before gameplay modules and regenerate
every downstream dependency/receipt proof.

### 4. Upgrade DismembermentPlus alone

The DismembermentPlus Workshop payload is the only currently loaded module with
an explicit Bannerlord `v1.4.8` resynchronization. Treat it as an untrusted new
input until all of the following pass:

1. Archive and hash the new Workshop manifest without modifying the old pin.
2. Decompile the new managed assemblies and diff types, methods, metadata tokens,
   Harmony targets, dependencies, assets, and `SubModule.xml`.
3. Regenerate the function inventory and authority audit for the new hash.
4. Update the compatibility family, exact method shapes, receipts, module
   catalog, deployment manifest, role overlays, and focused tests together.
5. Prove that the victim-authority peer still chooses one deterministic cosmetic
   event and that receivers never replay damage.
6. Render head, arm, and leg events for mounted and unmounted agents, including
   rider death, horse death, ragdolls, duplicate packets, reconnect, and mission
   teardown.

Complete and record this payload increment before assessing the Fourberie
increment; both increments belong to the same migration candidate and PR.

### 5. Upgrade and close Fourberie

Adopt Fourberie `v1.4.7.6` manifest `1598945672157391038` as an explicit second
payload increment. Regenerate its full function inventory, dispositions,
persisted-schema proof, behavior/model contracts, Harmony isolation, commands,
menus, missions, and same-save gates from the new bytes. This migration is not
complete while any of the exact ledger's 420 open routes or a strict-gate
rejection remains.

### 6. Regenerate release-owned artifacts

After the final payload set is fixed:

- update `deploy/workshop-mods.json` and
  `source/GameInterface/Services/WorkshopMods/Core/WorkshopModuleCatalog.cs`
  only for deliberately changed modules;
- update `deploy/WorkshopSuite/MANIFEST.json` from the verified build, never by
  hand-copying guessed hashes;
- regenerate `server-ui-support-sha256.json` from the actual `v1.4.8` support
  closure;
- regenerate function inventory and authority disposition joins for every
  changed assembly;
- rebuild server module bins and release-paired dedicated cores from exact
  audited inputs;
- run the suite builder's validate-only, dry-run, fixture, dependency closure,
  and release authority gates;
- verify the final client archive, server pair, receipt, load order, and every
  staged file hash.

### 7. Feature acceptance matrix

The migration is not complete until a host and at least two rendered clients
exercise these paths on `v1.4.8` with War Sails disabled:

- connect, save transfer, reconnect, late join, save, restart, and autosave;
- campaign movement, settlement entry/exit, encounters, raids, field battles,
  sieges, retreat, captivity, inventories, workshops, clans, kingdoms, armies,
  and tournaments;
- mounted rider death, horse death, remote dismount/switch, loose-horse AI,
  ragdolls, weapon drops/pickups, DismembermentPlus, and Unblockable Thrust;
- Diplomacy Kingdom tab, settings/snapshot, donate, fief, messenger, war, peace,
  alliance, pact, manager callbacks, and UI close/reopen lifecycle;
- Fourberie crime base, schemes, infiltration, prisoner/safehouse loot,
  enterprises, daily/hourly ticks, mission entry/exit, and save/restart;
- Improved Garrisons settings/templates, recruitment, transfer/guard parties,
  building reserve, persistence, and late join;
- Player Settlement empty-state boot plus an isolated populated-save check of
  dynamic objects, AI siege/capture, and snapshot/persistence behavior;
- UI drag-and-drop at 100% and non-100% UI scale because `v1.4.8` changes that
  vanilla presentation path.

All client/server logs must be free of unhandled exceptions, unsupported binary
identity, missing Harmony target, release-verification failure, authority
rejection caused by valid play, and repeated restart markers.

## Same-save deployment and rollback

1. Stop the server and reconcile the exact live process/file inventory.
2. Hash and copy the save, sidecar, module tree, server cores, configs, service
   unit, receipts, and release metadata into a new immutable rollback snapshot.
3. Prove the snapshot inventory and hashes before replacing anything.
4. Deploy the paired client/server `v1.4.8` release to a canary copy first.
5. Require sustained `SERVING`, bound UDP 4200, repeated pulses, zero restarts,
   successful autosave, and unchanged save identity before player entry.
6. Run the rendered acceptance matrix. Promote only the exact tested bytes.
7. On any save, startup, authority, or rendered regression, stop the new service
   and restore the complete `v1.4.7` snapshot. Do not attempt a mixed-version or
   partial-module rollback.

## Release decision

The expected base-game code delta is small, but the release blast radius is not:
Friend Edition deliberately fails closed on unknown game/module bytes. The
migration therefore records one base-game change plus independently proven
DismembermentPlus and Fourberie payload increments in the same candidate. A
passing build alone is insufficient; the exact-binary, authority, package,
rendered, same-save, and rollback gates decide promotion.
