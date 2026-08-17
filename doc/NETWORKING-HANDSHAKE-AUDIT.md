# Networking / handshake audit — 2026-08-17

Audit of the reworked authority-route and Workshop-handshake layer, driven by live client and
server logs from a real session rather than by reading alone. Every finding below was reproduced
or proved from evidence; where something is unproven it says so.

Method, in short: correlate a live session log against the router state machine; enumerate every
declared route and check it is registered and can reach a terminal state; walk the game's own IL
for the native behaviour the patches depend on; verify package hashes on both peers.

---

## Summary

| # | Severity | Finding | Status |
|---|---|---|---|
| F1 | **Critical** | Live authority router never polled — every fire-and-forget client command silently never applied | Fixed `dfa726b86` |
| F2 | **Critical** | Trade disabled outright, and accepting one crashed the client (stack overflow) | Fixed `9987125b6` |
| F3 | High | Authority route held its lock across a game-thread marshal (30 s freeze per pending request) | Fixed `e98ca7cfa` |
| F4 | High | Workshop hash covered a mod's runtime log → host advertised itself unmanaged, refused all joins | Fixed `9af434a8f` |
| F5 | High | Caravan size limit replaced native calc, discarding clan + Steward contributions | Fixed `7f68bcbc5` |
| F6 | Medium | Portal `/reports` throttle keyed on a caller-supplied field | Fixed `10873da25` |
| F7 | Medium | Unit suites raced on process-wide game state; CI green by luck | Fixed `b87c538a3` |
| F8 | Medium | Launcher lost the crash bundle when any report folder was incomplete | Fixed `e98ca7cfa` |
| F9 | Medium | `ItemRoster` lifetime logging storm — 1,929 ERR lines in 3 min | Fixed |
| F10 | Medium | Client manifest build 41.5 s against a 30 s validation deadline | **Open** |
| F11 | Low | Handshake message types with a throwing member and a null-returning property | Fixed |
| F12 | Low | Dead disabled-branch for caravan hostile actions | Fixed |

---

## F1 — Live authority router never polled (Critical, fixed)

**Symptom.** Entering any settlement did nothing. No error, no timeout, no log line. Time-speed
changes, siege, kingdom and village actions were equally inert.

**Evidence.** In the 16:44–16:48 session: 21 requests sent, 18 answered, **2 completed**. The two
that completed — `bootstrap.mod-config`, `bootstrap.workshop-capabilities` — are the only routes
that call `SubmitBlocking`, which pumps `Poll()` itself. The other 19, including
`settlement.encounter.end` (RequestId 19, replied at 16:47:47), sat in `AcceptedResultReceived`
until teardown cancelled them as `route-disposed` 58 s later. They never even tripped their own
10 s `CampaignMutation` apply timeout, which is why nothing was ever logged.

**Cause.** `CoopMod` resolved `IAuthorityRequestRouter` once during module init and added *that
instance* to the update list. Joining calls `DestroyContainer()` and builds a fresh container
(`CoopartiveMultiplayerExperience.StartAsClientCore`), so the session's handlers register their
routes on a different router than the one being updated. `Poll()` is the only thing that advances
an accepted reply to `ReplicaApplied`.

**Fix.** `AuthorityRouterPump` resolves the router from the current container each frame. A cached
reference cannot survive a container rebuild, and this is the one place that lifetime boundary is
crossed. Covered by a test that rebuilds the container mid-run and asserts the stale router stops
receiving updates.

**Why tests missed it.** Every router test constructs the router directly, so no test crossed the
container-rebuild boundary that the production wiring depends on.

---

## F2 — Trade (Critical, fixed)

Two defects behind one symptom.

*Disabled.* `249932283` — titled "feat(authority): route mercenary hire through server" — dropped
`Subscribe<CompleteTrade>` and stubbed `Handle_TradeAttempted` to warn and return, in nine lines
buried in an unrelated commit. Its comment points at a `trade.open`/`trade.commit` authority flow
that **does not exist in the codebase**. Trading was impossible for co-op clients from that commit
onward, and `Handle_CompleteTrade` was dead code.

*Crash.* The stub answered by closing the inventory screen. TaleWorlds'
`InventoryScreenHelper.CloseScreen` → `CloseInventoryPresentation` → **`InventoryLogic.DoneLogic`**
(confirmed in the shipped IL). The prefix publishes `TradeAttempted`, the broker dispatches
synchronously on the caller's thread, and `GameThread.Run` runs inline on the game thread — so
Accept re-entered its own prefix. 892 nested levels in one millisecond, then
`0x800703E9` (`ERROR_STACK_OVERFLOW`), which is uncatchable: no managed exception, no dump.

Restored the client→server `CompleteTrade` path and kept a re-entrancy guard the prefix holds
across the dispatch. Verified live: `Blocked legacy` count 0, and the server replicated the trade
out (`NetworkItemRosterUpdate: 257 packets`, `TownMarketData__itemDict`).

---

## F3 — Route lock held across a game-thread marshal (High, fixed)

`HandleResult` cancelled every pending ticket from *inside* the route lock. `Monitor` is reentrant,
so the nested `CancelAll` ran the whole loop still holding it — while completing a ticket marshals
its presentation callback onto the game thread and waits, and the game thread's own `Poll` takes
that same lock first. Poller and game loop parked against each other for the full 30 s
`GameThread.BlockingTimeout`, once per pending request, whenever a reply arrived with a replaced
`SessionId`.

Reproduced deterministically: the three role-assigning test classes beside the client authority
tests failed 5 runs in 8, 16 failures each. Ticket completion is now also claimed with `Interlocked`
rather than a check-then-act on `IsCompleted`.

---

## F4 — Workshop hash covered a mod's runtime log (High, fixed)

Improved Garrisons appends to `ModuleData/ErrorLog.xml` inside its own module root whenever it
throws, which it does on every campaign tick of the headless host (`IGSaveFilePath.get_SaveFilesPath`
has no player profile there). The hasher classified that `.xml` as *configuration* and folded it into
the package digest, so the host drifted from its receipt seconds after loading a save and refused
every client with "Server loads an unmanaged copy of 'ImprovedGarrisons'".

Both hash implementations — the runtime hasher and the receipt packager — now exclude it. No shipped
package contains that name, so every existing pin stays valid. Proved after deployment: with
`ErrorLog.xml` present again on disk, the pre-fix rules fail the configuration digest and the
post-fix rules pass, and all 14 modules match the receipt on host *and* client.

---

## F5 — Caravan party size limit (High, fixed)

Native adds base 20 **+ the clan's `CalculateBaseMemberSize` + the leader's Steward bonus** + the
caravan bonus, but gates that last term on `Party.Owner == Hero.MainHero` — never true on a headless
host, which caused real desertions. The patch fixed that by replacing the entire model with a flat
`20 + (elite ? 30 : 10)`, discarding the clan and Steward terms: an upgraded caravan's cap was pinned
at 50 and never grew.

Now runs native and adds only what native skipped, reading the owner from
`CaravanPartyComponent.Owner` rather than `PartyBase.Owner` (the latter consults `_customOwner`,
which is deliberately not replicated and can resolve differently per peer). An unresolvable owner is
logged instead of silently yielding the bare base.

---

## Verified sound

These were checked and found correct — recorded so they are not re-audited blindly.

- **Route registration: 90 declared, 90 registered.** No declared route lacks a `Define`.
- **Every client route can terminate.** All client-side route definitions contain an
  `AuthorityCommitProbeResult.Applied` path. The seven handlers with no `Applied` path are all
  server-side, where `Poll` returns early on `ModInformation.IsServer` by design.
- **Suite integrity, both peers.** All 14 Workshop modules hash-match the receipt on the host and on
  the installed client; every launch-order module present; 25 `SubModule.xml` files parse.
- **Launch order.** The launcher config still holds the legacy 16-module token but matches
  `LegacyModuleTokenBeforeGearAndDemographics` byte-for-byte, so it migrates in memory to the full
  20-module order. Fragile — see F13 below.
- **Re-entrancy cluster around `PlayerEncounter`.** Ten publisher/handler cycles were identified from
  a method-granular publisher map; the encounter cluster is guarded by `AllowedThread` /
  `CallOriginalPolicy` and by an explicit `approvedRestartDepth` counter. The trade path was the only
  unguarded instance.

---

## Open items

### F9 — `ItemRoster` logging storm (Medium) — FIXED
`ItemRosterLifetimePatches` logs *Client created managed ItemRoster* at **ERR** severity for a benign,
constant condition: 1,929 lines in three minutes this session, and ~200/second in an earlier one
(48,712 lines in ten minutes, a 7.7 MB log). It drowns real errors — the trade crash's 892 lines were
buried under it — and is the same class as the SideDiag flood fixed in `0acafc0aa`. Should be
demoted to debug or rate-limited.

*Fixed.* All 58 call sites now route through `Common.Logging.ClientMutationLog`, which keeps the
first three reports per `(action, subject)`, says so once when a subject starts repeating, then stays
quiet — at Warning, not Error. Nothing here is a failure, and reserving Error for real faults is what
makes the log searchable.

### F10 — Manifest build vs validation deadline (Medium)
The client took **41,560 ms** to build its Workshop manifest this session (cold cache; a warm run was
1,357 ms). `ValidateModuleState.ValidationTimeout` is **30 s**. The build did not trip it, but the
margin is inverted on a cold cache and a slower disk could produce a spurious "Timed out waiting for
the server to validate the connection". Needs either a deadline that accounts for hashing, or a
warm-up that hashes before the join begins.

### F11 — Handshake message types (Low) — FIXED
`ValidateModules.TransactionID` is `=> throw new NotImplementedException()`, and
`ModulesProcessed.Modules` is a get-only auto-property with no constructor, so it can only ever be
null. Both types *are* live — `ValidateModules` is subscribed by `NewHeroHandler` and `ModulesProcessed`
is published by `ModuleInterface`; an initial grep suggested otherwise and the compiler corrected it.
Fixed in place: `TransactionID` returns `Guid.Empty` instead of throwing, and `Modules` can no longer
return null.

### F12 — Dead disabled branch (Low) — FIXED
`CaravansConversationsPatches.caravanHostileActionsEnabled` is `true`, so the "Hostile actions
against caravans are temporarily disabled" branch is unreachable. Remove the flag or document why it
is retained.

*Fixed.* Flag and unreachable branch removed.

### F13 — Launcher token migration is not persisted (Low) — FIXED
The migration rewrites the legacy token in memory only, and matches with `StringComparison.Ordinal`.
Any hand-edit to `launcher-config.json` stops the exact match firing, and the launch silently drops
to 16 modules and fails the handshake. Persist the migrated token.

*Fixed.* The migration now writes the upgraded token back to disk, best-effort so a read-only or
locked config can never block a launch.

---

## Scope

Covered: the authority-route layer end to end (registration, lifetime, completion, timeouts,
re-entrancy), the Workshop manifest handshake on both peers, the trade and inventory path, caravan
party-size modelling, and the launcher's module wiring.

Not yet covered: per-route behavioural verification of all 90 routes against their game-side effects
(only reachability and termination were proved), the mission/battle layer, and a systematic diff
against upstream `Bannerlord-Coop-Team/BannerlordCoop` for portable fixes.
