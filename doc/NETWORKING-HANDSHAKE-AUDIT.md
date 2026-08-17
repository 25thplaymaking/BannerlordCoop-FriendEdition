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
| F10 | — | Client manifest build 41.5 s against a 30 s validation deadline | Withdrawn — not a defect |
| F11 | Low | Handshake message types with a throwing member and a null-returning property | Fixed |
| F12 | Low | Dead disabled-branch for caravan hostile actions | Fixed |
| F13 | Low | Launcher token migration not persisted | Fixed |
| F14 | **Critical** | Eight client actions shipped disabled *and* never subscribed server-side | Fixed |
| F15 | High | Player self-release from captivity needs per-client state the server does not model | **Open — needs a decision** |
| F16 | Low | Dead disabled-branch for villager hostile actions | Fixed |

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

### F10 — Manifest build vs validation deadline — WITHDRAWN
I recorded this as an open risk on the strength of two numbers: the client took **41,560 ms** to build
its Workshop manifest (cold cache; warm was 1,357 ms) and `ValidateModuleState.ValidationTimeout` is
**30 s**. Those numbers are real but they are not on the same clock, and I paired them wrongly.

`ValidationTimeout` is armed inside `SendValidationRequest`, which runs *after* hashing finishes; it
bounds the server round-trip only. The build itself is covered by `ManifestPreparationTimeout`, a
separate 2-minute deadline armed at construction and present since the Workshop baseline
(`af127af18`) — long before this audit. `SteamJoinWatchdog` is the only other deadline in the join and
it is disarmed on `NetworkConnected`, before validation starts. The 41.5 s build therefore ran against
120 s, a ~3x margin, and the session it was measured in joined successfully.

No change was needed and none was made.

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

*Fixed.* Flag and unreachable branch removed. **F16** is the same defect in
`VillagerConversationsPatches.villagerHostileActionsEnabled`, found by the F14 sweep and removed with it.

### F13 — Launcher token migration is not persisted (Low) — FIXED
The migration rewrites the legacy token in memory only, and matches with `StringComparison.Ordinal`.
Any hand-edit to `launcher-config.json` stops the exact match firing, and the launch silently drops
to 16 modules and fails the handshake. Persist the migrated token.

*Fixed.* The migration now writes the upgraded token back to disk, best-effort so a read-only or
locked config can never block a launch.

---

## F14 - Eight client actions shipped disabled, and unwired on both sides (Critical, fixed)

F2 found trade disabled by an early `return` with a `#pragma warning disable CS0162` hiding the
unreachable remainder. That turned out to be a pattern, not an incident. Searching for the pattern
itself - `CS0162` in shipped source - found seven more, and every one of them was broken *twice*: the
client stub was only half of it, because in each case the server-side apply handler existed, was
correct, and **was never subscribed**. Undoing the stub alone would still have produced silence.

| Action | Client | Server |
|---|---|---|
| Settlement "take to party" | stubbed | `Handle_MenuTakeHeroToParty` never subscribed |
| Companion dismissal | stubbed | `Handle_FireCompanion` never subscribed |
| Liberate lord prisoner | stubbed | `Handle_NetworkLiberateLordPrisoner` never subscribed |
| Take lord prisoner | stubbed | `Handle_NetworkTakeLordPrisoner` never subscribed |
| Released after helping in battle | stubbed | `Handle_NetworkLordHelpedInBattle` never subscribed |
| Let a defeated lord go | stubbed | `Handle_NetworkLordDefeatToRelease` never subscribed |
| Free a lord | stubbed | `Handle_NetworkLordFreedToRelease` never subscribed |
| Release a prisoner you hold | stopped at the client | no network message existed at all |

Taking prisoners after a battle is not a fringe feature; neither is dismissing a companion.

**The reason they were disabled was sound.** Each stub's comment said some variant of "no server-issued
lease verifies this request", and that was true: the requests name their own actor and target, so a
client could forge "dismiss any companion", "add any hero to any party", or "free every prisoner in the
campaign". The pre-existing server checks confirmed the *world* still matched what the client expected
- clan unchanged, party unchanged - which is optimistic concurrency, not authorization. Nothing asked
who was calling.

So the fix is not to un-stub them. Each is restored behind a gate that reads server state only:

- **Identity, always.** The peer resolves to a registered `Player`, and the actor named in the request is
  that player's own hero or party. This alone kills acting-as-somebody-else.
- **Custody or a lease, whichever is actually checkable.**
  - Hero transfer: the target party must be the requester's own, and the hero must be unattached or
    already in their clan - otherwise it is pulling a hero out of someone else's party.
  - Companion dismissal: the companion's owning clan must be the requester's clan.
  - Prisoner release: the party holding the prisoner must be the requester's own. Custody *is* the
    permission - releasing a prisoner you are holding needs nothing further.
  - The five lord-conversation outcomes: an active `ConversationPartyTracker` lease, **or** custody of
    the prisoner. The lease exists now (it did not when these were disabled) and covers both the
    client-initiated and the server-detected post-battle conversation, since `HoldAndApprove` issues one
    on the `serverDetected` path too. Custody is the fallback for a party-screen conversation, which
    never opens a map conversation and so has no lease to hold. Taking a *new* prisoner has no custody
    to appeal to and therefore still requires the lease - which is exactly the case that must not be
    forgeable.

The conversation partner is deliberately not matched against the lease target: a prisoner has no party,
so there is nothing on the lease to compare it against.

Native behaviour was taken from the game's own IL rather than guessed - the TakeToParty branch of
`GameMenuOverlay.ExecuteTroopAction` is exactly `LeaveSettlementAction.ApplyForCharacterOnly` followed
by `AddHeroToPartyAction.Apply(hero, MainParty, true)`, which is what the server applies.

**Why nothing caught it.** No test asserted that a handler is subscribed, or that its body reaches the
send. Twelve tests now assert both at the IL level, which is the only level where "returns early before
sending" is visible. They were checked against a deliberately re-stubbed build to confirm they fail on
it rather than passing vacuously.

---

## F15 - Player self-release from captivity (High, open - needs a decision)

Everything above is fixed. This one is not, and I do not think it should be fixed quietly.

When *your own* hero is captive, the menu options that end it - pay ransom, escape, captor lets you go -
publish `EndPlayerCaptivityAttempted`. That was stubbed like the rest, and
`NetworkEndPlayerCaptivityAttempted` was never subscribed server-side. The difference is that this
handler, unlike the other seven, **cannot be made safe by adding a check**, because a value it needs is
supplied by the client and cannot be recomputed server-side:

- **The ransom amount.** The client sends `Campaign.Current.PlayerCaptivity.CurrentRansomAmount`. The
  server cannot recompute it. `PlayerCaptivity` is a per-campaign singleton that on a headless host
  describes the host's own captivity, not the client's, and `RansomPlayerValuePatch` deliberately forces
  `PrisonerRansomValue` to **0** for player heroes so the AI does not ransom them. There is no
  server-side number to compare against, so a client could name its own price - including zero.
- **The release position.** The client sends where its party reappears. This half *is* fixable: the
  server already has `GetReleasePosition` and can derive it from the captor. The ransom is the blocker.

`PlayerCaptivityServerHandler` keeps no per-captive state - no record of who is held, by whom, at what
price - so there is nothing to validate against today. Server-driven releases still work: if the captor
is defeated, or the AI ransoms or frees you, `PlayerCaptivityEndedByServer` runs and you get out. What
does not work is *choosing* to buy your way out.

Three ways forward, in the order I would pick them:

1. **Server-issued release offer (recommended).** When the server records a player as captive it
   computes and stores the ransom itself, and sends an offer id. The client's request quotes the id, not
   a number; the server prices it, checks the hero's gold, and applies. This is the same shape as the
   marriage-barter lease that already works, so it fits the architecture rather than bending it. It is
   also the largest change: new per-captive server state plus a small protocol.
2. **Server-priced, client-triggered.** Keep the current message, but have the server ignore
   `RansomAmount` and `PlayerPartyPosition` entirely - derive the position from the captor via
   `GetReleasePosition`, and price the ransom server-side at apply time. Much smaller, and it closes the
   exploit, but the number shown in the player's menu can disagree with what they are charged.
3. **Leave it as it is.** Captivity still ends - captor defeated, AI ransom, peace - just not by your
   choice. No exploit, no work, a worse game.

I did not pick one, because the first is a real feature decision and the third is a real answer.

---

## Scope

Covered: the authority-route layer end to end (registration, lifetime, completion, timeouts,
re-entrancy), the Workshop manifest handshake on both peers, the trade and inventory path, caravan
party-size modelling, and the launcher's module wiring.

Also covered on the second pass: every client action disabled behind a stub or an unreachable branch,
found by sweeping for the pattern itself rather than by reading - `CS0162` suppressions, "is disabled"
and "unavailable until" strings, and `IHandler` types that subscribe nothing. That sweep is now clean
apart from F15 and two deliberate gates: the unstuck command and the Diplomacy messenger, both correctly
refused until their authority is available.

Not yet covered: per-route behavioural verification of all 90 routes against their game-side effects
(only reachability and termination were proved), the mission/battle layer, and a systematic diff
against upstream `Bannerlord-Coop-Team/BannerlordCoop` for portable fixes.
