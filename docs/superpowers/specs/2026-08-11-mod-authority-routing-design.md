# Complete Mod Authority Routing Design

## Status and decision

This design replaces the current "guarded but feature-blocked" compatibility target with a stricter
release requirement: every player-visible gameplay function in an active Friend Edition mod must
either execute through a complete Coop authority path or be proven to be presentation/read-only.
An active stable build may not retain a clickable action that deterministically refuses, an
authority-sensitive function with no disposition, or a gameplay feature described only as a future
route.

The selected approach is per-mod authoritative routing using the existing Coop handlers, registries,
transactions, and state messages. A generic reflection-based remote invocation framework is explicitly
rejected. It would make authorization opaque, couple the protocol to private method shapes, and expand
this work into the separately deferred mod-SDK project.

## Goal

Audit every runtime-reachable function in the active mod set for shared-state relevance, then preserve
each mod's player-visible gameplay outcome using explicit Coop-native server transactions, server-only
callbacks, replicated cosmetics, or a documented existing Coop owner. Complete the missing routes and
prevent future Workshop updates from introducing unclassified authority-sensitive methods.

## Scope

The active gameplay surfaces are:

- Improved Garrisons 4.2.0.7;
- DismembermentPlus 2.0.8.7;
- Fourberie 1.4.7.5;
- Diplomacy 1.4.7;
- Unblockable Thrust 1.1.3.1;
- Player Settlement 7.5.0 plus PlayerSettlementFixes;
- Friend Edition's integrated Separatism implementation.

Harmony, ButterLib, UIExtenderEx, and MCM remain in scope for lifecycle, configuration, and headless
safety, but their ordinary presentation/framework helpers do not need a server request merely because
they are methods. RBM remains retired and is audited only to prove that none of its code or options
re-enter the active loadout.

The work does not create the generic mod SDK, revive RBM, update to new Workshop binaries, publish a
stable release, or change unrelated Coop gameplay.

## Authority-sensitive function definition

A function requires an explicit authority disposition if it can directly or transitively do any of
the following:

1. create, remove, register, move, damage, heal, equip, recruit, upgrade, capture, or otherwise mutate
   a campaign or mission object;
2. spend or award gold, influence, items, troops, prisoners, relations, policies, rewards, or skills;
3. start or resolve a war, peace, pact, rebellion, kingdom/clan transfer, siege, encounter, mission,
   menu consequence, or time transition;
4. select an actor or target from `Hero.MainHero`, `MobileParty.MainParty`, `Clan.PlayerClan`,
   `Agent.Main`, local player encounter state, or another process-global singleton;
5. use randomness, local agent indices, GUIDs, wall-clock time, or unordered process state to choose a
   result visible to another peer;
6. save, load, migrate, or write authoritative state outside the Coop campaign/config schemas;
7. register a Harmony patch, event listener, model, campaign behavior, mission behavior, or deferred
   UI callback whose downstream work satisfies any rule above;
8. present an option whose successful outcome requires any rule above.

Pure calculations still receive a disposition when their result feeds an authoritative transaction.
Constructors, accessors, compiler-generated methods, and private helpers inherit their owning function
family's disposition unless their IL independently matches an authority-sensitive rule.

## Required disposition model

The generated function ledger will gain an authority companion keyed by exact assembly SHA-256 plus
metadata token. Every authority-sensitive method must have exactly one of these dispositions:

- `ClientPresentation`: local rendering/input only, with no shared mutation;
- `PurePolicy`: deterministic calculation invoked inside a named server/mission authority owner;
- `ServerCallback`: original callback runs only on the server and publishes its changed canonical state;
- `ServerCommand`: client intent is converted into an explicit controller-authorized request;
- `ReplicatedCosmetic`: the authority selects a stable event result and every rendered peer replays it;
- `CoopOwnerReplacement`: an existing named Coop service preserves the outcome and the original patch
  or mutation path is removed;
- `FrameworkLifecycle`: pinned framework initialization/configuration with a declared role boundary;
- `Retired`: code cannot load or expose options in the active contract.

`Blocked`, `Unsupported`, `GuardedFeatureBlocked`, `NotAllowed`, unclassified, and empty placeholder
routes are invalid for an active stable gameplay surface. A temporary development branch may hide an
unfinished option through the capability snapshot, but stable promotion requires its final disposition
and tests.

The validator must also prove that every active menu/VM execution method maps to either
`ClientPresentation` or a live `ServerCommand`, every registered callback maps to an authority owner,
and every manifest method still resolves exactly once against the pinned binary.

## Audit pipeline

The existing 41,000-method ledger remains the binary inventory. A second pass will inspect method IL,
Harmony metadata, type ancestry, call targets, fields, and naming/lifecycle conventions to produce a
candidate authority list. It will flag:

- calls to known TaleWorlds/Coop mutators and action classes;
- writes to mod static/singleton/save fields;
- references to local-player/global-singleton APIs;
- event, behavior, model, patch, persistence, mission, menu, and VM execution entry points;
- filesystem/configuration access and nondeterministic result selection;
- transitive calls from a flagged public/callback entry point into mod-owned helpers.

The checked-in authority manifest is reviewed semantically rather than trusting the heuristic. CI
fails when an exact pinned assembly gains or loses a method, a flagged method has no disposition, a
disposition refers to a missing owner/route/test, or an active disposition remains blocked. Updating a
Workshop pin therefore requires a fresh audit, not merely a new file hash.

## Command and callback architecture

Each `ServerCommand` uses a mod-specific typed request and result. Requests contain:

- session/campaign epoch;
- monotonic request ID scoped to the authenticated peer;
- expected state revision;
- actor hero/controller identity derived again from the transport peer on the server;
- stable object IDs for every target;
- bounded primitive or typed payload values;
- capability/operation identifier.

The server rejects stale, unauthorized, malformed, unavailable, or duplicate work before mutation.
It validates ownership, target existence, costs, prerequisites, spatial constraints, and current
campaign/mission phase. The mutation executes through a narrow transaction with captured compensation
state. Success publishes the authoritative native deltas plus the mod's revisioned state, then caches
the result by peer/request ID. A duplicate returns the cached result without a second mutation.
Failure rolls back completely and returns an explicit reason; rollback failure aborts the session.

`ServerCallback` paths use the same canonical-state publication and revision rules but do not invent a
client request. `PurePolicy` functions run only inside their declared Coop owner. `ReplicatedCosmetic`
events carry a stable event ID, authoritative subject/target IDs, selected outcome, and seed. Late join
either replays still-relevant events or receives the resulting canonical state.

## Capability and UI contract

The host publishes a revisioned capability snapshot after the module/config handshake. A capability is
enabled only when its pinned module, declared Coop route, server handler, configuration, persistence
schema, and current campaign/mission prerequisites are all available. Clients use this snapshot to
construct or enable menus and view-model commands.

No active UI option may rely on "click and receive NotAllowed" as feature discovery. During development,
an unfinished capability is absent and its option is hidden with one operator-visible audit message.
For the final stable candidate, every original player-visible option in scope must have a live capability
or a documented `CoopOwnerReplacement` that presents the equivalent Coop flow.

## Per-mod completion slices

### Common contract first

Improved Garrisons, Fourberie, and Player Settlement will implement `IWorkshopModule` and enter the same
catalog-reconciled fingerprint, operator configuration, patch registration, sync declaration, and test
contract already used by Diplomacy and the combat adapters. RBM will be removed from the active mission
declaration path. The stale 11-module/RBM documentation and contradictory Fourberie limitation notice
will be corrected in the same slice.

### Improved Garrisons

Keep the certified server-owned ticks, recruitment, upgrades, party lifecycle, capture reconciliation,
finance reads, and persistence. Convert all 29 denied management/template/mobile-garrison operations
into typed commands. Selection payloads carry stable troop/template/town/party IDs rather than localized
names or client objects. Mobile-party creation, orders, transfers, recruiter spawning/return, culture and
template edits, copy/reset operations, and toggles each validate player-controlled settlement ownership,
cost, expected revision, and idempotency. Canonical state includes every setting/order/template/activity
record needed for restart and late join.

### Diplomacy

Keep the existing state snapshot and Donate Gold transaction. Add controller-authorized commands for
grant fief, send messenger, direct war/peace/truce actions, non-aggression pacts, keep-fief decisions,
and any player consequence currently tied to a singleton. Automated agreement, corruption/influence,
and war-exhaustion processing run as server callbacks without local inquiries. Separatism remains the
single rebellion engine; Diplomacy civil-war UI and policy are mapped to the Separatism coordinator or
an explicit equivalent flow, not a second kingdom mutation engine. Every agreement/cooldown/exhaustion
change increments and publishes the Diplomacy revision.

### Fourberie

Keep server-owned behaviors/ticks and revisioned canonical state. Replace the three singleton-based
create routines—agent enlistment, bandit recruitment, and scam bandit spawning—with explicit-context
transactions, and route mission initialization through the authenticated mission/player context.
Audit every menu, conversation, shortcut, contract, safehouse, fight-club, crime, recruitment, spawn,
party, reward, hostility, and mission consequence. Fourberie model formulas are either composed as
`PurePolicy` inputs inside the named Coop owner or mapped to an equivalent existing Coop calculation;
they may not be silently discarded solely because their original model registration conflicts.
The embedded HomesSteads and Bellum Civile cross-mod add-ons are classified `Retired` for this exact
loadout because their prerequisite modules are absent; their behaviors and options must not register.

### DismembermentPlus

Create a replicated cosmetic event at the accepted-blow boundary. The authority chooses the supported
limb/result and seed from stable hit/agent IDs after validating the killing blow. Every rendered peer
deduplicates and applies the same cosmetic event; the headless server never creates render entities or
changes slow motion. Event lifetime and late-join behavior are explicit. The original local
`OnRegisterBlow` randomness remains removed.

### Player Settlement

Replace the guarded-empty-state boundary with a server construction transaction. Preview remains local
and read-only; the request carries validated coordinates, type/template/culture/name and expected
campaign revision. The server checks placement/navmesh/distance/collision, ownership, uniqueness, cost,
and concurrency, allocates stable object IDs, registers the complete settlement/town/village/building
graph, commits payment, and publishes an ordered registration snapshot before references.

Overwrite, rebuild, port/gate placement, village binding, save/reload, map visuals, buildings, armies,
sieges, capture, and late join receive explicit dispositions and tests. Dynamic objects register before
any party, owner, siege, workshop, or village reference. A save containing these objects cannot disable
the capability without a tested migration.

### Separatism

Keep the certified server-only rebellion/union lifecycle and Diplomacy collision ownership. Restore the
omitted fallen-kingdom conversation outcome through a typed server command using the authenticated
player clan, expected kingdom revision, eligibility checks, and the existing atomic clan/kingdom
transaction. No client-side clan move is reintroduced.

### Unblockable Thrust

Retain the certified pure collision rule inside Coop's accepted-blow authority. The secondary audit must
prove there are no additional settings or entry points in the pinned binary that alter the result outside
that owner. No new protocol is added unless the audit finds a player-visible setting not represented by
the authoritative rule.

## Persistence and migration

Mod state that currently lives in sidecars or private manager fields is captured by versioned Coop-owned
schemas or a validated canonical adapter snapshot. Only the server imports legacy data, once, using a
recorded source hash and migration ID. Clients never import local mod state. Unknown newer schemas fail
closed instead of resetting. Each mod slice proves save, restart, late join, duplicate request, and
disconnect-during-transaction behavior before its capability becomes stable-enabled.

## Error handling

- Untrusted or malformed requests return a bounded rejection and produce no mutation.
- A trusted server state message that cannot validate or apply disconnects the client.
- An authoritative capture/publication failure aborts the server session rather than continuing with
  divergent peers.
- Transactions commit state and revision only after successful mutation and publication preparation.
- Compensation failure is fatal and names the affected mod/operation/request in the audit log.
- Capability mismatches fail during the join/config barrier, before a player can invoke the option.
- Runtime method-shape, patch-owner, or binary drift aborts startup and requires re-audit.

## Testing strategy

Every behavior change follows red-green-refactor. A new test must first fail because the route,
validation, rollback, state field, or capability is absent. Each command receives tests for success,
unauthorized actor, invalid/stale target, insufficient cost, duplicate/reordered request, apply failure,
rollback, disconnect, late join, save/restart, and two-client convergence where applicable.

Focused module tests are followed by the complete build, unit, integration, and sharded E2E suites.
The exact packaged candidate is then installed through the managed client dry run and tested with a
dedicated server plus rendered clients. The rendered matrix exercises every original player-visible
option, not merely campaign startup. State digests are compared after each action and after restart.

## Release gates

A stable candidate is allowed only when all of the following are true:

1. the exact active binaries and complete method ledger match their audited pins;
2. every authority-sensitive method has one valid disposition and named owner;
3. no active gameplay disposition is blocked, unsupported, unclassified, or placeholder-only;
4. every original player-visible option has a capability-backed live route or equivalent Coop flow;
5. every route has authorization, idempotency, rollback, persistence, late-join, and convergence proof;
6. common, module, integration, E2E, packaging, patch-ownership, and release-safety gates pass;
7. rendered clients exercise the complete option matrix against the paired dedicated server;
8. save/restart and late join preserve identical state digests;
9. the release candidate, server pairing, manifest, receipt, and provenance hashes agree;
10. stable promotion remains an explicit manual decision.

## Delivery decomposition

This is too large for one undifferentiated code change. It will ship as independently reviewed,
testable commits in this order:

1. authority manifest/validator and common module/capability contract;
2. Improved Garrisons commands and state completion;
3. Diplomacy commands/callbacks with Separatism ownership;
4. Fourberie explicit-context actions, models, and missions;
5. Dismemberment replicated cosmetic event;
6. Player Settlement object graph, persistence, map, siege, and AI completion;
7. Separatism conversation route and Unblockable secondary proof;
8. complete all-mod automated/rendered certification and non-stable candidate rebuild.

Each increment is committed and pushed only after its focused gates pass. The current stable feed and
live gameplay payload remain unchanged until the complete release gate is satisfied.
