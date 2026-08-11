# Friend Edition mod-function review

Review baseline: `e0ee48bc2` (2026-08-11)

Binary ledger: [`generated/workshop-function-inventory.json`](generated/workshop-function-inventory.json)

Prior decompiler audit: [`WorkshopModIntegrationAudit.md`](WorkshopModIntegrationAudit.md)

## Scope and coverage

This is the current semantic review for every runtime-reachable, mod-owned Bannerlord 1.4.7
assembly in the Friend Edition set, plus the integrated Separatism implementation. The generated
ledger records every metadata method, including constructors, accessors, compiler-generated
closures/state machines, private helpers, and methods with no body. Each record carries the exact
assembly SHA-256, identity, declaring type, return/parameter shape, generic arity, method flags,
metadata token, and RVA.

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
| Separatism (integrated) | 1 filtered surface | 23 | 156 | 32 | 152 |
| **Total** | **22** | **4,497** | **40,996** | **24,554** | **38,709** |

## Function-family dispositions

| Surface | Function families reviewed | Current disposition and errors found |
|---|---|---|
| Harmony | wrapper boot, patch processors, owner queries/unpatch, load order, debug/log UI | Keep the exact pinned wrapper as the sole provider. Owner-specific unpatch is valid; blanket cleanup and wrapper debug UI remain forbidden in production paths. Existing package tests enforce one runtime. |
| ButterLib | version loader; DI/services; submodule wrappers; delayed lifecycle; save/object extension injection; crash reporting; settings/filesystem; distance/geopolitics; UI helpers | Client framework and dedicated fork must remain separate runtime builds. Original lifecycle/save/crash/UI functions are not authority owners. The server-kit patcher currently identifies the implementation too broadly and must require an exact fingerprint/signature/count before patching. |
| UIExtenderEx | submodule lifecycle; extension registration; VM mixins; prefab/widget/brush factories; movie loading; command execution; caches | Client presentation only. No campaign authority or server UI load. Global registries/caches must be torn down per process/mission. Its original USER32/exit paths cannot execute headlessly. |
| MCM | loader/API/UI adapter; global/per-campaign/per-save providers; local serialization and migration; settings screens/save/exit | Presentation only. Gameplay configuration must come from Coop's server snapshot, never peer-local MCM files. The current catalog says every framework is active on the server while comments still say it cannot run there; catalog, manifest, and actual load roles must be reconciled. |
| RBM | entry/XML merge; configuration; combat formula/damage/posture; AI/tactics/spawn; tournament roster/prize; UI/input | Retired from the production loadout after the native initialization crash. The catalog and `deploy/workshop-mods.json` still require/activate it, while launcher/server tokens omit it. Remove that contradiction; do not revive individual slices during this plan. |
| ImprovedGarrisons | initialization; campaign behaviors/events; party creation/removal; recruitment/upgrade; finance/food/speed models; settings/log UI; sidecar save managers | Server owns ticks, parties, rosters, costs, food, and persistence; clients render/send intent. Verify no client `OnApplicationTick` mutation and no localized-name/sidecar authority remains. Current compatibility tests cover broad gating but not the full recruit/upgrade/capture/save lifecycle. |
| DismembermentPlus | mission registration; blow validation; random limb choice; mesh/entity/effects; slow motion; settings/error UI | Cosmetic client presentation only, driven by one accepted authority event. Original `new Random`, GUID, local agent indices, `Agent.Main`, WinForms, and time changes are not deterministic authority. It needs an event/late-agent regression gate before being called synchronized. |
| Fourberie | submodule/application/mission hooks; behavior registration and `SyncData`; menus/conversations; recruiting/spawning/party ticks; crime/safehouse/fight-club/contracts; fourteen models; mission controllers | Menus may be client presentation, but authoritative creation remains unsafe. `FourberieRecruitHandler` verifies a peer hero and then invokes the original static method with only `int`, so the original still targets `MainHero/MainParty/_agentsParty`; fail these routes closed until explicit player context exists. `InitializeBehaviorsOnlyPrefix` returns `false` after a caught partial-add failure despite its fallback comment. Existing manifest tests are stale: twelve methods intentionally moved from blocked to presentation/behavior categories but the tests still require `UnsupportedPlayerAction`. |
| Diplomacy | loader; campaign behaviors/managers; war/peace/agreement/cooldown/exhaustion; kingdom/clan/influence patches; UI/viewmodels; save types; civil war/rebel functions | Server owns every campaign mutation; client UI renders snapshots and sends intent. Donate-gold routing has an explicit player path, but civil-war, barter, kingdom, influence, and banner-editor patches collide with Coop and Separatism. Diplomacy may supply policy/UI; it is not a second mutation owner. |
| UnblockableThrust | submodule/config and defend-collision postfix | Keep as a pure rule inside Coop's accepted blow/collision authority. Never allow a parallel damage path. Add combined shield/parry/chamber/mounted coverage; RBM interaction is irrelevant while RBM remains retired. |
| PlayerSettlement | module load; template/blacklist loading; dynamic object registration; behavior/save schema; build/overwrite/rebuild; placement/map UI; AI/army/siege/null fixes | Loading/read-only preview may remain, but construction/rebuild and campaign-object registration stay blocked. Its patch set overlaps Coop buildings, map click/time, armies, sieges, visuals, town visits, and persistence. Do not build the previously proposed custom settlement system in this plan. |
| Separatism | campaign-event adapter; chaos/lord/national/anarchy/union decisions; kingdom create/reactivate/destroy; clan move; hostile cleanup; relations/wars/policies; colors/names/text; readiness/territory/random helpers; loyalty thresholds; global friend/enemy and diplomatic-barter prefixes | Integrated and server-gated, but not yet certified. Required fixes/tests are detailed below. Separatism is the selected rebellion coordinator; Diplomacy civil-war initiation must defer to it. |

## Separatism complete functional review

The 156-method raw surface resolves into these gameplay functions:

1. `SeparatismCampaignBehavior` registers new-game, load, daily, and daily-clan events and dispatches
   only when `ModInformation.IsServer`. `SyncData` is intentionally empty because created kingdoms,
   clan membership, wars, policies, and settlement ownership are native campaign state.
2. `OnNewGameCreated`/`OnGameLoaded` initialize or reconcile separatist state; `OnDailyTick` removes
   empty kingdoms; `OnDailyTickClan` currently evaluates lord/floating-title and anarchy paths in one
   tick, so a clan can undergo two structural attempts without a transition guard.
3. `TryLordRebellionOrFloatingTitle`, `TryNationalRebellion`, `TryAnarchyRebellion`, and `TryUnion`
   cover all four configured separation/union modes. Chance defaults of `1.0` match Separatism 1.3.8
   and are a deliberate gameplay choice, not a porting typo.
4. `TryCreateRebelKingdom` creates/reactivates the kingdom, assigns banner/colors/name/title, moves
   the clan, changes relations, copies policies, inherits wars, and logs. A broad catch spans these
   mutations without compensating rollback; failure can leave a registered partial kingdom or a
   moved clan.
5. `MoveClan`, `FinishStaleHostileActions`, `ApplyRebellionRelations`, `CopyPolicies`, `InheritWars`,
   `RemoveEmptyKingdoms`, and `DestroyKingdom` mutate native authoritative campaign state. Each needs
   idempotent/save-reload assertions around the normal Coop replication path.
6. Readiness/territory/fief weighting, color selection/difference, naming, intro text, and random
   helpers are deterministic inputs except `Roll`/`TakeRandom`; those are safe only because the
   behavior is server-only, but tests need fixed chance/fixtures rather than peer-local randomness.
7. The loyalty model wires both documented start and recovery thresholds. It dereferences the global
   mod snapshot and needs disabled/null snapshot coverage alongside the `ModConfigAuthority` null fix.
8. `Hero.IsFriend`/`IsEnemy` are globally replaced while enabled, altering every mod and vanilla
   caller. The barter prefixes also change join, leave, and defection globally. These are intentional
   policy hooks but require one-owner cross-mod tests with Diplomacy and Fourberie.
9. The recovered original 1.3.8 source adds the conversation line
   `player_is_requesting_fallen_to_join`; the integrated port omits it. This plan will preserve the
   omission and document it as unsupported UI rather than importing another client-driven clan move.
10. Disconnected controlled player clans remain protected because the player registry retains their
    controlled campaign objects; add a reconnect/save test so this stays proven rather than assumed.

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
| Blow/collision/dismemberment | Unblockable, Dismemberment, retired RBM, Coop mission authority | Coop accepted blow transaction | Pure thrust flag plus one cosmetic dismember event; clients never apply damage twice. |
| Tournament roster/prize/reward | retired RBM and Coop tournament services | Coop tournament transaction | RBM remains absent; one frozen roster/prize/reward. |
| Map click/camera/time/buildings | PlayerSettlement and Coop map/building patches | Coop client dispatcher + server building tick | Preview is read-only; construction stays blocked. |
| Army/siege/settlement visuals | PlayerSettlementFixes and Coop army/siege/visual services | Coop services | Port only proven null/custom-object handling into existing owners. |
| Save/config persistence | ButterLib/MCM/IG sidecars, Fourberie save fields, PlayerSettlement metadata, native Separatism state | Coop save/config schemas | Server-only import/migration; stable IDs; late join and restart convergence. |

## Confirmed repair queue from the function review

The queue is intentionally limited to the approved stabilization plan:

- **P0:** live server logs `COOP MODULE VERIFICATION FAILED` for `Coop.Core.dll`,
  `GameInterface.dll`, `Common.dll`, and `Coop.Steam.dll` but continues serving; reconcile the boot
  receipt/hash source and fail closed only after the correct deployment is pinned.
- **P1:** align catalog/manifest/tokens with the ten-module suite and retired RBM; fix Fourberie
  player-context routing/fallback; make Separatism creation transactional and one-transition-per-tick;
  assign Separatism/Diplomacy rebellion ownership; add the missing Separatism branch/save/cross-mod tests.
- **P1:** protect the inherited auto-resolve finalize change with focused paced-win,
  pacer-disconnect, duplicate-finalize, and non-win tests before live certification.
- **P1:** make launcher updates staged, exact, hash-required, rollback-safe, and unable to enable Join
  after a failed required update.
- **P2:** narrow map-readiness exception suppression; guard null mod options; fix the launcher's TCP
  status probe for a UDP-only Coop listener; harden server-kit patch fingerprints/counts and bound
  opt-in diagnostics.
- **Already closed:** missing `BloodBright`, unsafe keep-running dispatcher handler, public password,
  automatic stable publication, and feature-branch release triggers.

No RBM revival, custom settlement implementation, new mod SDK, or campaign-feature redesign is in
scope for this pass.
