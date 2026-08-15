# Friend Edition mod-function review

Review baseline: `ee7a3fc281df689d55f39434837f21f6213cd377` (2026-08-11)

Migration note (2026-08-15): this document is evidence for the pinned
`v1.4.7` payload set, not proof that every player-visible function is complete
or that its counts survive Bannerlord `v1.4.8`. The
[`v1.4.8` migration guide](../docs/bannerlord-1.4.8-migration.md) requires a
fresh inventory plus one reconciled strict gate before release.

Binary ledger: [`generated/workshop-function-inventory.json`](generated/workshop-function-inventory.json)

Authority ledger: [`generated/workshop-authority-audit.json`](generated/workshop-authority-audit.json)

Prior decompiler audit: [`WorkshopModIntegrationAudit.md`](WorkshopModIntegrationAudit.md)

## Scope and coverage

This is the semantic review for every runtime-reachable, mod-owned assembly in
the pinned Bannerlord 1.4.7 Friend Edition set, plus the integrated Separatism implementation. The generated
ledger records every metadata method, including constructors, accessors, compiler-generated
closures/state machines, private helpers, and methods with no body. Each record carries the exact
assembly SHA-256, identity, declaring type, return/parameter shape, generic arity, method flags,
metadata token, and RVA.

The authority ledger reconciles one-for-one with all 41,050 method records. At
this evidence baseline, 13,310 of the 13,729 metadata-required routes have a
disposition. That count is not the release-completion count: the current strict
Fourberie gameplay gate rejects **495** unique routes (419 unclassified plus 76
unsafe presentation classifications), while the metadata ratchet retains 419
reviewed open routes. The migration must regenerate both views and collapse
them into one authoritative zero-open result. The shrink-only ratchet
(`tools/WorkshopIntegration/fourberie-open-routes.json`) records the un-adapted
stealth/fight-club/banditry mission stack (`FStealthMissionLogic`, mission controllers, spawners,
`FourbCom`, `InsideMissionsHelper` — end-of-mission consequences mutate campaign state on the
entering client with no Coop route), unrouted behavior/menu consequences
(`FourberieBehavior.PlayerActionsConsequences`, `PickAction`, fight-club/bandit/escape residues),
and a handful of VM canonical-state writes. Classifying an open route requires giving it a real
Coop owner first; adding a new open route fails the ratchet.

Historical version-specific implementation DLLs that the 1.4.7 loaders cannot select and bundled
third-party libraries are not treated as mod gameplay functions. Their bytes and dependency
closure remain covered by the Workshop suite audit. Semantic review is deepest for engine
lifecycle callbacks, Harmony patches, campaign/mission mutation, randomness, persistence,
filesystem/UI access, and local-player/global-singleton assumptions. Pure accessors and
compiler-generated support methods are still present in the raw ledger but share their owning
type's disposition rather than repeating identical prose for tens of thousands of methods.

| Module/surface | Assemblies | Types | Methods | Public methods | Methods with bodies |
|---|---:|---:|---:|---:|---:|
| Bannerlord.Harmony | 1 | 157 | 1,489 | 1,016 | 1,412 |
| Bannerlord.ButterLib | 2 | 431 | 3,097 | 1,882 | 2,734 |
| Bannerlord.UIExtenderEx | 1 | 273 | 1,963 | 1,295 | 1,780 |
| Bannerlord.MBOptionScreen (MCM) | 4 | 1,537 | 14,570 | 8,950 | 13,519 |
| RBM | 5 | 282 | 1,334 | 750 | 1,334 |
| ImprovedGarrisons | 1 | 174 | 2,171 | 1,458 | 2,162 |
| DismembermentPlus | 1 | 19 | 286 | 245 | 210 |
| Fourberie | 1 | 240 | 2,703 | 1,224 | 2,703 |
| Bannerlord.Diplomacy | 2 | 1,177 | 12,121 | 7,169 | 11,617 |
| UnblockableThrust | 1 | 3 | 24 | 21 | 24 |
| PlayerSettlement | 2 | 184 | 1,082 | 512 | 1,072 |
| Separatism (integrated) | 1 filtered surface | 38 | 210 | 49 | 205 |
| **Total** | **22** | **4,515** | **41,050** | **24,571** | **38,772** |

## Function-family dispositions

| Surface | Function families reviewed | Current disposition and errors found |
|---|---|---|
| Harmony | wrapper boot, patch processors, owner queries/unpatch, load order, debug/log UI | Keep the exact pinned wrapper as the sole provider. Owner-specific unpatch is valid; blanket cleanup and wrapper debug UI remain forbidden in production paths. Existing package tests enforce one runtime. |
| ButterLib | version loader; DI/services; submodule wrappers; delayed lifecycle; save/object extension injection; crash reporting; settings/filesystem; distance/geopolitics; UI helpers | Client framework and dedicated fork remain separate runtime builds. Original lifecycle/save/crash/UI functions are not authority owners. The server patcher now requires the exact pinned ButterLib input and exactly five concrete type/method/signature matches; the real pinned input reproduces the deployed server DLL byte-for-byte. |
| UIExtenderEx | submodule lifecycle; extension registration; VM mixins; prefab/widget/brush factories; movie loading; command execution; caches | Client presentation only. No campaign authority or server UI load. Global registries/caches must be torn down per process/mission. Its original USER32/exit paths cannot execute headlessly. |
| MCM | loader/API/UI adapter; global/per-campaign/per-save providers; local serialization and migration; settings screens/save/exit | Presentation only. Gameplay configuration comes from Coop's server snapshot, never peer-local MCM files. The active ten-module contract agrees across catalog, deployment manifest, launcher, and both role orders; the dedicated host has proved the pinned framework cohort live. |
| RBM | entry/XML merge; configuration; combat formula/damage/posture; AI/tactics/spawn; tournament roster/prize; UI/input | Retired from the production loadout after the native initialization crash. It is absent from the catalog, deployment package, launcher, and server/client active orders. Its pinned binaries remain only as an audited historical surface; do not revive individual slices during this plan. |
| ImprovedGarrisons | initialization; campaign behaviors/events; party creation/removal; recruitment/upgrade; finance/food/speed models; settings/log UI; sidecar save managers | All 723 authority candidates are now classified. Management, templates, mobile parties, hostile encounters, culture, rosters, and building reserves have authenticated server routes; `v1.4.8` must re-prove every option, rollback, persistence, restart, and late-join path. |
| DismembermentPlus | mission registration; blow validation; random limb choice; mesh/entity/effects; slow motion; settings/error UI | All 17 candidates are exact-classified. Live Coop suppresses the original local-random `RegisterBlow` path; the victim-authority peer validates once, derives a canonical event ID/seed, applies the original visual routine, and broadcasts a capability-gated cosmetic event. Receivers re-derive authority/identity, reject malformed/duplicate/stale/conflicting events, and never replay damage. Slow motion stays disabled in Coop. |
| Fourberie | submodule/application/mission hooks; behavior registration and `SyncData`; menus/conversations; recruiting/spawning/party ticks; crime/safehouse/fight-club/contracts; fourteen models; mission controllers | Forty-seven explicit operation families now have typed server routes, but the strict gate still rejects 495 UI/mission/lifecycle routes. Mission callbacks, shared-state menu/dialog helpers, model composition, and every remaining canonical write need named owners and end-to-end proof. Existing fail-closed guards are safety evidence, not completed gameplay. |
| Diplomacy | loader; campaign behaviors/managers; war/peace/agreement/cooldown/exhaustion; kingdom/clan/influence patches; UI/viewmodels; save types; civil war/rebel functions | Donate/fief/messenger/peace/war/alliance/pact operations and keep-fief/server callbacks are routed, including the persisted server messenger queue. The complete exact ledger, callbacks, UI lifecycle, and Separatism collision ownership still require one reconciled zero-open gate. |
| UnblockableThrust | submodule/config and defend-collision postfix | Kept as a pure rule inside Coop's accepted collision authority with no parallel damage path. The audited defaults now have combined foot/mounted, shield, parry, and chamber regression coverage; only an authority-owned non-shield blocked thrust crushes through. RBM interaction is irrelevant while RBM remains retired. |
| PlayerSettlement | module load; template/blacklist loading; dynamic object registration; behavior/save schema; build/overwrite/rebuild; placement/map UI; AI/army/siege/null fixes | The original build/rebuild/overwrite outcome must be restored through Coop's existing object, building, map, siege and persistence owners. The current empty-state-only admission and blocked non-empty object graph are incomplete and fail release validation. No parallel custom settlement subsystem will be invented. |
| Separatism | campaign-event adapter; chaos/lord/national/anarchy/union decisions; kingdom create/reactivate/destroy; clan move; hostile cleanup; relations/wars/policies; colors/names/text; readiness/territory/random helpers; loyalty thresholds; global friend/enemy and diplomatic-barter prefixes; fallen-clan conversation | All 57 candidates are exact-classified. Structural callbacks are server-gated; the restored option is capability-gated presentation plus an authenticated, revisioned, replay-safe server command with rollback. Configured settlement rebellion is forced off on clients. |

## Separatism complete functional review

The 210-method raw surface resolves into these gameplay functions:

1. `SeparatismCampaignBehavior` registers session-launch, new-game, load, daily, and daily-clan events. It
   installs the restored fallen-clan option only on clients and dispatches structural campaign work
   only when `ModInformation.IsServer`. `SyncData` is intentionally empty because created kingdoms,
   clan membership, wars, policies, and settlement ownership are native campaign state.
2. `OnNewGameCreated`/`OnGameLoaded` initialize or reconcile separatist state; `OnDailyTick` removes
   empty kingdoms. `OnDailyTickClan` stops after the first successful structural path, preventing a
   second anarchy attempt in the same clan tick.
3. `TryLordRebellionOrFloatingTitle`, `TryNationalRebellion`, `TryAnarchyRebellion`, and `TryUnion`
   cover all four configured separation/union modes. Chance defaults of `1.0` match Separatism 1.3.8
   and are a deliberate gameplay choice, not a porting typo.
4. `TryCreateRebelKingdom` creates/reactivates the kingdom, assigns banner/colors/name/title, moves
   the clan, changes relations, copies policies, inherits wars, and logs. Its transaction restores
   clan membership and presentation, removes or re-eliminates partial kingdoms, and fails closed if
   compensation itself cannot complete.
5. `MoveClan`, `FinishStaleHostileActions`, `ApplyRebellionRelations`, `CopyPolicies`, `InheritWars`,
   `RemoveEmptyKingdoms`, and `DestroyKingdom` mutate native authoritative campaign state. Focused
   E2E coverage proves server/client convergence for chaos, lord, national, anarchy, union, load
   reconciliation, disconnected player-clan protection, rollback, and idempotent cleanup.
6. Readiness/territory/fief weighting, color selection/difference, naming, intro text, and random
   helpers are deterministic inputs except `Roll`/`TakeRandom`; those are safe only because the
   behavior is server-only, but tests need fixed chance/fixtures rather than peer-local randomness.
7. The loyalty model wires both documented start and recovery thresholds. Disabled and null snapshot
   coverage now proves vanilla fallback without dereferencing absent mod options.
8. `Hero.IsFriend`/`IsEnemy` are globally replaced while enabled, altering every mod and vanilla
   caller. The barter prefixes also change join, leave, and defection globally. These remain
   intentional policy hooks; fixed threshold/config tests and 92 Diplomacy collision cases enforce
   the selected one-owner rebellion policy.
9. The recovered original 1.3.8 line `player_is_requesting_fallen_to_join` is restored. Its old
   `persuasion_leave_faction_npc` destination is absent from Bannerlord 1.4.7, so the live option closes
   the conversation and sends a typed command. The server re-derives the ruler from `NetPeer`, validates
   capability/session, stable clan/leader IDs, expected kingdom and membership revision, ruler/minor/war
   rules, commits through `IKingdomMembershipState`, caches duplicate results, and rolls back failure.
10. Disconnected controlled player clans remain protected because the player registry retains their
    controlled campaign objects; the focused E2E suite proves the disconnected state and load-time
    reconciliation behavior.

Existing evidence: 17 Separatism unit test methods (37 cases), 14 synchronized campaign-flow E2E
scenarios, 92 Diplomacy-collision cases, and 8 configuration-authority cases pass in isolated focused
runs. The exact validator confirms 57/57 required Separatism routes are classified.

## Single-owner cross-mod matrix

| Target/domain | Competing functions | Selected owner | Required rule/gate |
|---|---|---|---|
| Harmony runtime/unpatch | Harmony wrapper, ButterLib, RBM/PlayerSettlement embedded providers, all adapters | pinned Bannerlord.Harmony + owner-scoped Coop adapters | One active `0Harmony`; exact hashes; no blanket unpatch. |
| Framework lifecycle/UI/settings | ButterLib, UIExtenderEx, MCM | client framework lifecycle; Coop config authority | Headless never touches UI. MCM cannot supply gameplay truth. Exact client/server runtime split. |
| Hero friend/enemy semantics | Separatism global prefixes, Diplomacy/Fourberie callers | Separatism relation policy on server | Fixed threshold tests; clients cannot decide campaign consequences. |
| Rebellion/civil war | Separatism four modes, vanilla `RebellionsCampaignBehavior`, Diplomacy rebel/civil-war functions | Separatism coordinator | Diplomacy initiation disabled/deferred; one kingdom and one clan move per tick/request. |
| Clan join/leave/defection/barter | Separatism barter prefixes, Diplomacy managers, vanilla barters | Coop server clan/kingdom transaction using Separatism policy | Explicit authority, expected kingdom/revision, rollback, save/reload. |
| War/peace/policies/relations/influence | Separatism, Diplomacy, Fourberie models/actions | Coop kingdom/diplomacy services | Mod formulas are policy inputs; one committed server transaction/delta. |
| Campaign party creation/rosters | Fourberie, ImprovedGarrisons, PlayerSettlement, Coop mobile-party services | Coop server object/party registries | No `MainParty/MainHero` authority; stable player/object ID and idempotent request. |
| Damage/healing/finance models | Fourberie models, Unblockable rule, retired RBM, Coop damage/health/finance | Coop battle/server model | One explicit composition; no Harmony-order winner. |
| Blow/collision/dismemberment | Unblockable, Dismemberment, retired RBM, Coop mission authority | Coop accepted blow transaction | Pure thrust flag plus canonical replicated cosmetic limb/seed event; clients never apply damage twice. All 21 active candidates are exact-classified. |
| Tournament roster/prize/reward | retired RBM and Coop tournament services | Coop tournament transaction | RBM remains absent; one frozen roster/prize/reward. |
| Map click/camera/time/buildings | PlayerSettlement and Coop map/building patches | Coop client dispatcher + server building tick | Route placement/build/rebuild/overwrite through stable IDs and server transactions. |
| Army/siege/settlement visuals | PlayerSettlementFixes and Coop army/siege/visual services | Coop services | Port only proven null/custom-object handling into existing owners. |
| Save/config persistence | ButterLib/MCM/IG sidecars, Fourberie save fields, PlayerSettlement metadata, native Separatism state | Coop save/config schemas | Server-only import/migration; stable IDs; late join and restart convergence. |

## Authority-routing foundation and open release work

The earlier stabilization queue remains closed:

- the live server is paired to the current four Coop hashes and aborts on mismatch;
- auto-resolve completion covers paced wins, duplicate completion, undecided release, and the shared
  client/server completion boundary;
- launcher updates are staged, exact, hash-required, rollback-safe, traversal-safe, and keep Join
  disabled after a required-update failure;
- map readiness suppresses only the audited transient null path; the UDP probe has reply/silence tests;
- null mod options are safe; configuration-authority tests pass; the active ButterLib assembly policy
  records the closed live coactivation gate;
- server-kit transforms require exact hashes and method fingerprints, produce reproducible output,
  restore the fail-closed abort, and bound opt-in diagnostics;
- credential/release containment, the exact ten-module/RBM-retired contract, Fourberie and Separatism
  transaction boundaries, Improved Garrisons lifecycle, Dismemberment replicated presentation, and
  Unblockable shield/parry/chamber/mounted rules have regression coverage.

The new foundation adds deterministic IL authority evidence, one exact audit record per method,
release rejection for blocked/unclassified active candidates, six common gameplay module declarations,
and a trusted server capability snapshot. UnblockableThrust and DismembermentPlus now have exact
closure, and Separatism now has 57/57 exact closure. Per-mod typed routes are still feature work:
Improved Garrisons management, Fourberie actions/models/mission entry, Diplomacy operations, and
Player Settlement construction/persistence.

Only after those routes are classified with owners and focused tests may the release validator pass;
rendered client option coverage and the existing auto-resolve reproduction remain later publication
gates. RBM stays retired, and the work will use existing Coop owners rather than a generic remote
invocation SDK or a parallel custom settlement implementation.
