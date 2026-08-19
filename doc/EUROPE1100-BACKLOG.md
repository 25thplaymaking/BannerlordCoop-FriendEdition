# Europe 1100 — findings backlog

Collected from the first live Europe 1100 sessions on `bannerlord-coop-seven.service`
(2026-08-18). Everything here is evidence-backed and reproducible; nothing is speculative unless
it says so. Ordered by player impact.

Two items from these sessions are already fixed and shipped, and are listed only so the backlog
reads honestly: the settlement distance cache (`COOP-OPS-WORKFLOW.md` item 21) and the
weather-node bounds guard (`MapWeatherNodeBoundsGuardPatch`, PR #30).

---

## 1. Clothing snaps/tears as units come into view — incomplete EoE shader sack

**Symptom.** Garments visibly snap or tear on all units once they get within a certain distance,
accompanied by a hitch.

**Cause.** `Modules/Europe1100/Shaders/D3D11/compressed_shader_cache.sack` (979 MB, 72,024
variants) is an *incomplete* cache for the render path this client uses. Its own
`shader_compile_report.log` contains:

| shader | references in EoE's sack |
|---|---|
| `pbr_metallic` | 411 |
| `pbr_metallic_gbuffer` | **0** |
| `pbr_terrain` | **0** |
| `pbr_cloth` | **0** |

So the sack carries the base pass for clothing materials but none of the deferred/gbuffer or
shadowmap variants. The engine misses the sack and compiles the variant **at runtime, on demand**:

```
[20:24:35.078] Render Requested: black_cape
[20:24:35.094] Render Requested: merchants_hat
[20:24:35.102] Missing shader from sack: pbr_metallic_gbuffer
[20:24:35.102] compile_shader: $BASE/Shaders/Sources/pbr_metallic_gbuffer.rs, main_vs, vs_5_0, ...
[20:24:35.102] Missing shader from sack: pbr_metallic_shadowmap
```

One session logged **528** `pbr_terrain`, **201** `pbr_terrain_gbuffer`, **26**
`pbr_metallic_gbuffer`, **8** `pbr_terrain_shadowmap`, **4** `pbr_terrain_pointlight`, **1**
`pbr_metallic_shadowmap` misses and **56** `compile_shader:` calls. The misses cluster on
`Render Requested:` lines for clothing and equipment — `black_cape`, `merchants_hat`,
`half_apron`, `merchants_fur_coat`, `guarded_armwraps`, `empire_cape_a`, `wrapped_shoes`.

The engine does fall back correctly — it compiles from `$BASE/Shaders/Sources/`, which is complete.
The sack is not *wrong*, it is *partial*, and the cost is paid at the worst possible moment.

**Proposed change.** Test removing EoE's shader cache so the base game's complete pipeline is used:
rename `Modules/Europe1100/Shaders/` aside and relaunch. Expect a longer first load while shaders
compile, then no misses. If that resolves it, decide between shipping the module without its sack
or regenerating the sack for this engine build.

**Join-safety.** Safe to change client-side without touching the handshake: the EoE modules are
deliberately **uncatalogued**, so `WorkshopModuleFileHasher` never sees them and no content or
configuration hash covers `Shaders/`. This is one of the payoffs of keeping the conversion out of
the catalog.

## 2. Asset streaming stalls on the same approach

185 occurrences of:

```
Trying to make partial read on compressed asset data. This does not improve performance since
partial decompression is not supported
```

interleaved with the same `Render Requested:` bursts. EoE's asset packages are compressed in a way
that forces full decompression for a partial read, so streaming a newly visible model decompresses
more than it needs. Compounds §1 — both fire exactly when new models enter view.

**Proposed change.** Investigate repacking EoE's asset packages uncompressed, or accept and
document it. Measure before/after with the same log counters.

## 3. Joining clients softlock on a 219 MB save transfer

**Symptom.** A joining player appears frozen after connecting, with no progress indication.

**Evidence.** The packaged client log of a softlocked player ends here, with nothing after it:

```
03:46:13 Receiving host save transfer 5: 351 chunks, 22,950,818 compressed bytes, 219,002,742 save bytes
03:47:01 Received host save transfer 5: 22,950,818 compressed bytes decompressed to 219,002,742 bytes
```

48 seconds to stream the save, then the client goes quiet while it loads **219 MB** of campaign data.
For scale the host itself takes ~100 s and 556 ticks to load the same world, and the old Calradia
save was roughly 19 MB. Nothing is logged and nothing is shown during that window, so the only
signal available to a player is an unresponsive game.

**Proposed change.** Surface it rather than hide it: the transfer already reports chunk counts, so
the load that follows should drive the loading screen too (it is the same `ILoadingInterface` used
for "Validating modules..."). Then measure the real load time before deciding whether the payload
itself needs work — 219 MB per join, per player, is also worth weighing against §4's replication
volume.

## 4. Player death in a hideout resolves as a win

Dying in a hideout fight cleared the hideout and reported a victory. Native runs a distinct defeat
path here — the player is knocked out and companions pull them clear — and that flow is not
happening. Note `E2E.Tests.Services.MapEvents.HideoutMapEventTests` is in the pre-existing failing
set (§12), so this area has no working regression coverage to have caught it.

## 5. AI parties never initiate an encounter on a player (bandits ignore you)

**Symptom.** Bandits and other hostile AI never attack; they only fight if the player initiates the
encounter.

**Evidence.** Over a 90-minute live session with a player on the map, the host published
**`ConversationRequested` exactly 0 times**, and the only authority routes exercised were
`settlement.encounter.start` (137) and `settlement.encounter.end` (100) — i.e. every encounter that
session was the player walking into a settlement. No party-vs-party encounter occurred at all.

**Why that is the whole story.** On a dedicated host there is no `MainParty`, so an AI-initiated
encounter cannot use the native player-encounter flow. `EncounterManagerPatches
.TryRequestServerPlayerConversation` is the *only* bridge: when exactly one side of a
`StartPartyEncounter` is a player party it publishes `ConversationRequested` and returns, with the
comment "The dedicated server has no MainParty, so send fresh AI/player encounters to the player's
conversation flow." Zero publishes means AI parties are never reaching that bridge.

Ruled out along the way:
- `MobilePartyAIDisablePatches.TickPrefix` is **not** the cause — on the server
  `IsControlledByThisInstance()` is true for every non-player party, so AI parties do tick.
- `RaidAiInterventionSuppression` is **not** the cause — it only suppresses parties whose target is
  a suppressed *raid* target (village raids), not general hostility.
- Coop's attack-protection (`DefaultMobilePartyAIModelPatches.PreventFactionAttacksUntil`) is **not**
  a blanket block — it is applied only on safe-passage barter and captivity release.

**Confirmed still true on 2026-08-19, and narrowed.** Over 26 hours the host published
`ConversationRequested` **zero** times while **625** `MapEventInitialized` and 341 map-event
conversations occurred between AI parties. So AI parties fight each other perfectly well; what never
happens is an AI party choosing a *player* party. Players describe exactly that shape: bandits will
join a battle already running, but walk past a lone player on the map.

**The decision path, traced.** For an AI party to attack a player on a dedicated host:

1. `DefaultMobilePartyAIModel.CalculateInitiativeScoresForEnemy` scores each nearby enemy, gated by
   `ShouldConsiderAttacking(party, target)`.
2. A positive score sets `ShortTermBehavior = EngageParty` with the player's `PartyBase`.
3. `EncounterManager.HandleEncounterForMobileParty` sees `IsCurrentlyEngagingParty` and calls
   `PartyBase.CanPartyInteract`, then `OnPartyInteraction` → `StartPartyEncounter`.
4. `EncounterManagerPatches.TryRequestServerPlayerConversation` publishes `ConversationRequested` —
   the only bridge, since the host has no `MainParty`.

**Prime suspect: our own postfix on `ShouldConsiderAttacking`.** Native consults `ShouldBeIgnored`
only for `MobileParty.MainParty`:

```csharp
bool num = targetParty != MobileParty.MainParty || !MobileParty.MainParty.ShouldBeIgnored;
```

`DefaultMobilePartyAIModelPatches.ShouldConsiderAttacking_Postfix` applies it to **every** target,
and carried a literal `// TODO test with player parties`. On a dedicated host a player's party is an
ordinary party, not the main one, so this is the one rule in the chain that treats players
differently from how native would. `ShouldBeIgnored` is `_ignoredUntilTime.IsFuture || IsInRaftState`,
and `MobileParty._ignoredUntilTime` **is an AutoSync'd field** (`MobilePartySync`), so a window set
by a client's local native flow replicates to the host and suppresses AI there. The same flag gates
step 3 independently: `PartyBase.CanPartyInteract` requires `mobileParty.IsMainParty || !target.ShouldBeIgnored`,
and on the host no engaging party is ever the main party.

**Ruled out this pass:**
- **War status.** `IsEnemy` is `FactionManager.IsAtWarAgainstFaction`, and
  `DefaultDiplomacyModel.GetShallowDiplomaticStance` returns `War` whenever
  `faction1.IsBanditFaction != faction2.IsBanditFaction`. Bandits are at constant war with every
  non-bandit faction including a freshly created coop clan, so this passes.
- **`AiEngagePartyBehavior`.** Not the bandit path at all — it returns early unless the thinking
  party is in a kingdom faction and has a `LeaderHero`.
- **`AiPatrollingBehavior`.** Explicitly excludes `IsBandit`.
- **The locator dropping distant parties.** `LocatorGrid` is a fixed 32×32 grid of 5-unit nodes whose
  `MapCoordinates` wraps modulo, so positions beyond the map scene's terrain size are folded back
  rather than lost. Unlike the weather grid in §8, proximity search is not a terrain-size victim.

**Shipped this pass: measurement, not a behaviour change.** `PlayerAggressionDiagnostics` counts, per
reason, why AI parties do or do not attack a player, in two stages — was the AI *allowed* to consider
the player (`native-declined` / `target-should-be-ignored` / `attack-protection` /
`player-in-conversation` / `allowed`), and did it then actually commit (`engage-party-set`). Enable
on the host with:

```
coop.debug.mobileparty.player_aggression true
```

then walk a player past hostile parties; a summary logs every 30 s. A dominant
`target-should-be-ignored` confirms the suspect above. `allowed` with no `engage-party-set` moves the
search to scoring or `CanPartyInteract`. Deliberately not fixed blind: guessing wrong here either
leaves the world passive or has every bandit on the map converge on one player, and one live sample
settles it.

## 6. Replication traffic peaks at ~6 MB/s to a single client

`[Coop] Packet profile over 10 seconds` peaked at **5,970,151 bytes/sec** with one player connected,
with sustained samples at 3.3 MB/s and 1.1 MB/s. The dominant senders in a single 10-second window:

| message | packets | bytes |
|---|---|---|
| `NetworkTroopRosterElementBatch` | 34,548 | 2,356,248 |
| `NetworkItemRosterUpdate` | 30,706 | 1,189,748 |
| `TownMarketData__itemDict_Upsert` | 20,836 | 1,114,238 |
| `NetworkUpdateTradeActionLogsForParty` | 842 | 999,694 |
| `NetworkUpdatePartyBehavior` | 6,818 | 797,962 |

EoE's parties and its far larger settlement/market set multiply every per-party and per-settlement
replication route. This is independent of the autosave stall in §7 and scales with player count.

**CORRECTION (2026-08-19): this is message volume, not packet count.** The earlier reading of this
table — "~6,500 tiny datagrams a second" — was wrong, and the fix it proposed was already shipped.

`PacketProfiler` records at the **logical** send, not at the wire. `CoopNetworkBase.Send` profiles
each `MessagePacket` and then calls `EnqueueMessage`, which buffers per peer and emits an
`AggregateMessagePacket` once the batch reaches `AggregationBudgetBytes` (1200 B, sized against
LiteNetLib's MTU and its 64-*packet* reliable window). So the 34,548 troop-roster entries above are
34,548 **messages**, which left as roughly `2,356,248 / 1200` ≈ 2,000 datagrams. Cross-key
aggregation is not missing — it is general, it is already applied to every message route, and
`AggregateMessagePacket` appearing in the profile with only framing bytes is that batching working
rather than an unused transport.

**What is actually expensive** is producing ~17,800 replication messages a second in the first
place. Every one is deserialized and then applied as a separate action on the client's game thread,
and the pump used to run the entire arrival backlog inside a single frame — which is the "constant
stutter", and the same mechanism as the freeze in §7. The drain budget in `GameThread.Update` stops
a backlog landing in one frame; it does not reduce the work, so the message rate is still worth
attacking at the source.

**Where to look next.** Why one connected player generates tens of thousands of roster and market
messages in ten seconds. Suspect full-state rather than on-change replication: `NetworkItemRosterUpdate`
averages 38 bytes and `TownMarketData__itemDict_Upsert` 53, which is the shape of "send every entry"
rather than "send what changed". The peak sample here (5.97 MB/s) was taken during a join baseline,
not steady state — an idle campaign now profiles at 108 bytes/sec — so measure during real play
before sizing the problem.

**Do not chase per-packet framing.** It is already handled, and the 1200-byte budget has a written
rationale tied to MTU discovery. The remaining win is fewer messages, not better packing.

## 7. Host: ~4.5 s game-thread stall on every autosave

Not a bug — a big world meeting a save that cannot be made asynchronous. Measured breakdown of one
real host save (`SaveContext` emits these blocks itself, they land in the journal):

| block | time |
|---|---|
| `SaveContext::CollectObjects` (serial graph walk) | 0.285 s |
| `SaveContext::CollectSaveDataForObject::Objects` | 1.474 s |
| `SaveContext::CollectSaveDataForObject::Containers` | 2.361 s |
| `SaveContext::Saving Objects` + `Saving Containers` | 0.379 s |
| **`SaveContext::Save`** | **4.618 s** |
| `Save Process` (adds compression and the file write) | 6.043 s |

Payload: header 14.0 MB, strings 1.0 MB, objects 111.3 MB, containers 91.0 MB. It **plateaus** as
the world fills in rather than growing without bound.

**Why it cannot simply be made faster.** Both dominant blocks are *already* parallel — TaleWorlds
runs them through `TWParallel.ForWithoutRenderThread`, and the host confirms the width it gets:
`Max Dexree of Parallelism is set to: 30` on its 32-thread Ryzen. The file write is already async
(`AsyncFileSaveDriver`). What is left is reading a live 205 MB object graph, which has to happen on
the game thread because the graph is the campaign. Lock contention was considered and rejected:
`SaveContext.AddOrGetStringId` does take a single global lock, but `Saving Objects` takes the same
lock through `GetStringId` and costs only 0.201 s, so the lock is not where the seconds are.

**What was done instead — move it, and stop hiding it.**
- `DeferAutosaveWhileCampaignRunningPatch` holds the host's autosave tick until
  `TimeControlMode.Stop`, capped at 10 minutes. The campaign is paused roughly half the wall clock,
  and a stall taken while paused costs nothing.
- `autosaveMinutes` raised to 20 in `CoopData/DedicatedServer/server-config.json` (read at boot; a
  change needs a restart).
- `SavePatches` now raises `GameSaveStateChanged` around `Game.Save`, so clients show the native
  saving indicator for the whole stall. **This was the "no UI on screen" complaint, and the cause is
  worth recording:** the notification pipeline was complete end to end, and simply never fired,
  because the dedicated host does not autosave through `SaveHandler`. It builds the metadata itself
  and calls `Game.Current.Save(metaData, name, new AsyncFileSaveDriver(), callback)` directly, so
  the existing patches on `SaveHandler.OnSaveStarted` / `OnSaveEnded` were never reached.
- `GameThread.Update` no longer applies a whole arrival backlog in one frame (see §6), so the burst
  that follows the stall no longer freezes the client for about as long as the host froze.

**Still open.** The stall itself. The only real reductions left are saving less (fewer parties, or
excluding data from the graph, which changes the save format) or moving the collect off the game
thread (which the engine's design forbids). Both are large. Scheduling it is the right trade for now.

## 8. Host: the headless map scene reports the wrong terrain size

`MapWeatherNodeBoundsGuardPatch` (PR #30) stops the bleeding, and its own comment says it treats a
symptom. The cause is that `DedicatedServerMapScene` loads the "proven native set" and reports
**Native's** terrain size while an EoE campaign's parties sit far outside it. Native code derives
grid indices from `MapSceneWrapper.GetTerrainSize()` without bounds-checking, so:

- `DefaultMapWeatherModel.GetWeatherEventInPosition` threw ~9700 times in five minutes until guarded
- `DefaultMilitaryPowerModel.GetContextForPosition` appeared in the same stacks

**Anything else deriving a grid index from `GetTerrainSize()` is wrong the same way, silently.**
The fix belongs in the dedicated-server map scene, whose source is not in this repo — same blocker
as §3. Worth auditing every `GetTerrainSize()` consumer once that source is available.

## 9. EoE data: malformed settlement entries

Both peers log, in the hundreds:

```
Error: The required attribute 'name' is missing.     Node: Settlement  Line: 96
Error: The required attribute 'posX' is missing.
Error: The required attribute 'posY' is missing.
```

plus `'culture'` (89) and `'tier'` (69). These are EoE's own `settlements.xml` entries. Harmless
enough that the campaign loads, but they should be characterised — a settlement with no position is
worth knowing about before someone reports a missing town.

## 10. Missing particle systems

`waterfall_splash` (18), `psys_blaze_small_`, `outdoor_fire_sparks_small`, `psys_torch_fire`,
`prt_small_smoke`, `prt_small_outdoor_fire`, `prt_small_fire_sparks`. EoE scenes referencing native
particle systems that are not present. Cosmetic; lowest priority.

## 11. Ruled out — do not chase

**`Unable to find item to add dependency`** (86 on the client, 7 on the host). Looks like armour
breakage — `empire_helmet_a`, `plated_leather_armor_b_converted_slim`, `burlap_sack_dress_slim` —
but all of it fires at startup during `Registering items...`, immediately after
`Loading packages $BASE/Modules/Native/AssetPackages`, i.e. during **native** item registration
before EoE content is involved. The list also contains conversation poses
(`conversation_negative_confident`), which are not armour at all, and the headless host logs the
same class. Pre-existing and unrelated to the 1100 loadout.

## 12. Pre-existing: the E2E suite

179 unique failing E2E tests across Armies, Companions, LordBarter, RomanceMarriage, Kingdoms,
MapEvents and SiegeEvents. These were invisible while the solution did not compile (see PR #29) and
are unchanged by PRs #29 and #30 — the failing set is byte-identical before and after both. They
predate all Europe 1100 work and want their own pass.
