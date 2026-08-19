# Europe 1100 — findings backlog

Collected from the first live Europe 1100 sessions on `bannerlord-coop-seven.service`
(2026-08-18). Everything here is evidence-backed and reproducible; nothing is speculative unless
it says so. Ordered by player impact.

Two items from these sessions are already fixed and shipped, and are listed only so the backlog
reads honestly: the settlement distance cache (`COOP-OPS-WORKFLOW.md` item 21) and the
weather-node bounds guard (`MapWeatherNodeBoundsGuardPatch`, PR #30).

---

## 1. Clothing snaps/tears as units come into view — CAUSE STILL UNKNOWN

**Correction, 2026-08-19. The shader-sack explanation below is wrong for the TEARING.** It explains a
hitch when a garment first comes into view — the engine compiling a missing variant mid-frame — and
that is a real effect. It does not explain geometry stretching down through the terrain, which is a
skinning or mesh-deformation artifact, not a shading one. Two symptoms were conflated.

What the re-read of the client logs actually establishes:

- **The neutraliser did not cause it.** `Missing shader from sack` entries appear at 19:15, 21:25,
  21:52, 22:39 and 22:49 on 08-18; the cache was not renamed aside until **22:56**. The artifact
  predates the change by hours.
- **The bulk misses are harmless.** `pbr_terrain` misses 2,112 times and compiles **zero** times,
  because the base game ships its own `compressed_shader_cache.sack` that satisfies it (and
  `pbr_terrain.rs` exists in `$BASE/Shaders/Sources`). Only `pbr_metallic_*` both misses and compiles
  — 60/40/21/10 misses against 60/40/21/10 compiles, an exact match. Those are the hitch.
- **The item-dependency errors are not it.** `plated_leather_armor_*`, `burlap_sack_dress*`,
  `sturgian_lamellar_*` and their `_slim` / `_converted` variants fail during **Native** asset-package
  registration, name no module's ModuleData (they live in binary asset packages), and appear on the
  headless host too. Pre-existing native noise, as originally judged.
- **`Render Requested: black_cape` is the item-thumbnail renderer**, not in-world cloth, so the
  partial-read warnings that cluster around it are not a cape-simulation fault.

**The logs cannot diagnose this.** `rgl_log` records no skinning, skeleton or deformation faults, and
EoE ships neither `skeleton_scales.xml` nor `bone_body_types.xml`, so both come from Native. Diagnosis
needs the artifact itself — a screenshot, or the item and unit it happens on — not another pass over
these logs.

**Consequence: the neutraliser now defaults OFF** (`NeutralizeConversionShaderCache`), and
`ConversionBootstrap.RestoreShaderCache` puts back a cache an earlier launcher renamed aside. It
modified a 979 MB game file to fix something it demonstrably does not fix, and it trades 72,024
precompiled conversion variants for runtime compilation — which causes hitching rather than curing it.
The flag remains for A/B from the published config.

## 1a. Shader sack detail (accurate for the HITCH, not the tearing)

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

**It is world growth, not a leak.** Measured across the 00:00-03:40 session on 2026-08-19 (2-5
players): parties grew **2,711 to 4,291 (+58%)** as the campaign filled toward its own equilibrium,
outbound went **36 KB/s to ~500 KB/s sustained** with a 5.97 MB/s peak, and one 10 s window carried
**1,031,168 messages**. Normalising by parties x players gives 5.8 -> 3.3 messages per party per
player per 10 s over the session: flat to declining. Nothing leaks. The rate is
`world size x rate of change x player count`, because **there is no relevance filtering anywhere** —
511 `SendAll` call sites broadcast every world change to every client whether or not that client can
observe it.

**What that does to the link.** LiteNetLib's reliable channel keeps a fixed **64 packets** in flight,
so per-peer throughput is bounded by window / RTT, not bandwidth — roughly 4.5 MB/s at 17 ms and
**1.2 MB/s at 66 ms**. Peaks exceed both. Per-peer queue depths in that session reached **69,037 /
47,328 / 38,281**, and the 66 ms peer carried 4x the average backlog of the 17 ms peers on identical
traffic. `OverloadedPeerManager` then correctly forced campaign time to Pause **20 times** (it resumes
only once *every* peer is under 5,000), so the campaign was **fully stopped 42% of the session**,
worst hour 53%. That is the "we could barely play" report: the world refusing to advance.

**First reduction, shipped.** The four `Settlement.Nearby*Intensity` properties are no longer
replicated (`SettlementSync`). They measured ~8,900 messages per 10 s — about **12% of all traffic** —
and every reader is AI scoring or save serialisation; the names appear in no UI assembly. See that
file for the full argument. This is the template for the next pass: find AutoSync'd members that only
the authority reads.

**Relevance filtering, first cut, shipped.** `ReplicationRelevanceGate` holds coalesced roster
updates for parties and settlements no player is within 100 map units of. It reaches the traffic
without touching 511 broadcast sites because the two largest routes —
`NetworkTroopRosterElementBatch` and `NetworkItemRosterUpdate`, together about **55% of all
messages** — both already flow through `ISendCoalescer`, so gating that single flush covers them.

It **holds, it does not drop**. Coalesced payloads merge (counts sum, sets take the latest), so an
update held across many changes still carries the correct end state; a distant party whose roster
churns costs one message instead of many. `MaximumHold` (5 s) bounds staleness, `FlushInstance`
(the destroy path) bypasses the gate entirely, and every failure path sends — a wrongly held update
is a desync, a wrongly sent one is a packet.

The radius is generous on purpose: Bannerlord's party spotting range is single-digit map units, so
100 is over ten times what a client could see, while Europe 1100 spans roughly 1,405 x 894 units, so
one player's circle is ~2.5% of the map area. Effect is logged every 60 s as
`[Relevance] held X of Y (Z%)`, and `ReplicationRelevanceGate.Enabled` turns it off for A/B against
the packet profile.

**Second cut: the whole send path.** `ReplicationRelevanceFilter` sits on
`CoopNetworkBase.SendAll(IMessage)` — the single point every broadcast passes through — so it covers
the entire replication surface, not just what happens to use the coalescer. It holds any message about
an object no player is within 100 map units of, keeping only the latest per object per message type,
and releases on relevance or after 10 s.

Held only where holding is lossless:
- every generated `*_SetNetworkMessage` (whole-member state set), which is what
  `IInstanceScopedNetworkEvent` on `GenericNetworkEvent` exists to expose
- `NetworkUpdatePartyBehavior` — the largest route by BYTES (14,157 messages / 1.58 MB per 10 s); it
  carries its own position, so it needs no lookup, and it is a full behaviour snapshot
- `NetworkUpdateTradeActionLogsForParty` — 842 messages but ~1 MB per 10 s, and it resends the whole
  log list every time

**Never** held: adds, removes, index changes, clears and dictionary upserts. Those describe a step
rather than a state, and collapsing them would lose the steps in between — an add followed by a remove
would arrive as only the remove. `ReplicationRelevanceFilterTests` pins that rule so a future message
cannot quietly opt into being dropped.

Positions resolve for `MobileParty`, `Settlement`, `PartyBase`, `MobilePartyAi`, `TroopRoster`,
`Hero`, `Town` and `Village`. Anything unresolvable, unclassifiable, or that throws is sent
immediately.

**Dictionary upserts are covered too, keyed.** A generated upsert carries its own `Key`, so it is
held in a slot of `messageType|Key` — latest-wins *per key*, which is precisely what an upsert means
and is therefore lossless. That collapses `TownMarketData__itemDict_Upsert` (9,747 per 10 s) from one
message per reprice to one per item, and only for markets nobody is standing in.

Keying the hold that way exposed an ordering hazard, which is closed: a held upsert must never land
after a later remove or clear for the same object, or it resurrects the entry. So **any message that
is not holdable releases everything pending for its object first**, in order. That is checked before
relevance, before position, before anything — an unheld message can never overtake held state.
Releases are queued and drained on the next poll rather than sent inline, because the caller is
already inside `SendAll`.

**Position resolution** covers `MobileParty`, `Settlement`, `PartyBase`, `MobilePartyAi`,
`TroopRoster`, `Hero`, `HeroDeveloper` (via its hero — XP and skill churn was ~7,000 per 10 s),
`MapEventSide` (via its map event), `Town`, `TownMarketData` (via its town) and `Village`. Anything
outside that list is sent immediately.

Between the coalescer gate and the send-path filter, every route in the measured top ten is now
either relevance-filtered or deleted.

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

**Deferring the host's autosave tick does NOT work. It crash-loops the host.**
`DeferAutosaveWhileCampaignRunningPatch` (shipped 2026-08-19 04:11, removed 2026-08-19 15:30) held
`DedicatedServer.CoopServerHost`'s autosave tick until `TimeControlMode.Stop`. Skipping and later
releasing that tick made its own re-arm throw:

```
System.ArgumentOutOfRangeException: The added or subtracted value results in an un-representable DateTime. (Parameter 't')
   at System.DateTime.op_Addition(DateTime d, TimeSpan t)
   at DedicatedServer.CoopServerHost.c_Patch1()
   at DedicatedServer.CoopServerHost.A(Single )
   at DedicatedServer.CoopServerHost.Tick(Single dt)
```

`CoopServerHost.Tick` treats any exception as fatal and calls `Environment.Exit(3)`, so the host died
and systemd restarted it — **29 times in 11 hours, on a near-exact 22 min 39 s cycle**. Each process
loaded the same save, ran until the autosave fired, and died, so **the campaign never advanced past
Spring 12-13 1101 and nothing players did was ever saved.** The tick holds two obfuscated statics
that both decompile to `m_A` (one `DateTime` next-due, one `TimeSpan` interval); the release path
recomputes the due time from them and overflows. Do not patch this method. Treat the dedicated
host's autosave scheduler as off-limits and change `autosaveMinutes` instead.

**What is done instead.**
- `autosaveMinutes` is 20 in `CoopData/DedicatedServer/server-config.json` (read at boot; a change
  needs a restart). That is the only supported lever on when the host saves.
- `SavePatches` now raises `GameSaveStateChanged` around `Game.Save`, so clients show the native
  saving indicator for the whole stall. **This was the "no UI on screen" complaint, and the cause is
  worth recording:** the notification pipeline was complete end to end, and simply never fired,
  because the dedicated host does not autosave through `SaveHandler`. It builds the metadata itself
  and calls `Game.Current.Save(metaData, name, new AsyncFileSaveDriver(), callback)` directly, so
  the existing patches on `SaveHandler.OnSaveStarted` / `OnSaveEnded` were never reached.
- `GameThread.Update` no longer applies a whole arrival backlog in one frame (see §6), so the burst
  that follows the stall no longer freezes the client for about as long as the host froze. This is
  now the whole of the client-side mitigation, since the stall can no longer be rescheduled.

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

## 9. EoE data: malformed settlement entries — RESOLVED, benign

**Characterised 2026-08-19: nothing is missing.** The errors come from `Europe1100Expanded`, not
`Europe1100`, and they are partial override nodes rather than broken definitions:

| file | `<Settlement>` nodes | with `posX` |
|---|---|---|
| `Europe1100/ModuleData/settlements.xml` | 1,628 | **1,628** |
| `Europe1100Expanded/ModuleData/settlements.xml` | 95 | 0 |
| `Europe1100Expanded/ModuleData/settlements_to_eoe_1100/settlements.xml` | 73 | 0 |

95 + 73 accounts for the ~166 `posX`/`posY` errors. Those nodes look like:

```xml
<Settlement id="town_bordeaux" owner="Faction.clan_french_4" ></Settlement>
```

They patch only `owner` on settlements Europe1100 already defines, merged by `id` — which is exactly
what "Expanded" does, reassigning fiefs to its own clans. The engine's XSD marks `name`, `posX`,
`posY`, `culture` and `tier` required, so validating a partial override reports them missing. Every
settlement keeps the position Europe1100 gave it. No content is lost and no action is needed.

## 9a. Superseded settlement entries (original note)

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

## 13. Launcher: retired modules were never removed, and a crash left the launcher looking busy

**Duplicate storage and an unreadable module list.** `ModUpdater.InstallExact` replaced each module
the feed ships, exactly — but it recorded only a version string, never *which* modules it had
installed. A module dropped from a later suite therefore stayed on disk permanently. Across revisions
that accumulated whole conversions worth of gigabytes, and left the stock Bannerlord launcher listing
modules nobody could account for.

Each tier now writes a receipt beside its version stamp (`coop-suite-modules.txt`,
`Coop/installed-modules.txt`) naming what it installed, and on the next install removes what the
previous receipt claims and the new payload no longer ships. Pruning can only ever touch names a
previous receipt claims, so a player's own mods cannot be removed; base-game modules are refused
outright and receipt entries that are not plain child directory names are ignored. The first install
after this change has no receipt, so it prunes nothing and records the current set — cleanup begins
with the update after it.

**A crash left the launcher believing a session was still running.** After handing off, the launcher
watched nothing. A crash was surfaced only when the co-op collector eventually relaunched the
launcher through `COOP_LAUNCHER_PATH`, long after the game had gone, so players sat looking at
"Bannerlord has taken the field" and started another launcher to get back in — which the
single-instance mutex refused with an error box.

Both halves are fixed. The launcher now watches the game process and reacts the moment it exits,
polling briefly for the collector's bundle so the crash prompt appears on its own rather than at some
later start. And a second launcher start no longer complains: it signals the running instance to
raise its window and exits silently, which is the right behaviour for the two cases that actually
cause it — the collector's relaunch, and a launcher left invisible behind a full-screen game.

### 13a. Reclaiming what has already accumulated

Receipt-based pruning only helps installs made after it shipped: an existing install has no receipt,
so the first update records the current set and removes nothing. Everything already orphaned stays.

`ModuleReclaim` plus **Options -> RECLAIM DISK SPACE** recovers those. It finds unused module folders
by elimination — not a base-game module, not named in a feed receipt, not in the launch token — and
reports them with sizes, largest first.

It reports rather than deletes, because elimination is honest but not proof of provenance: the same
scan also catches mods a player installed themselves for single-player, which the launcher never put
there. The prompt names every folder, marks the ones a receipt proves are ours, and removes nothing
until the player agrees.

One safety rule is worth stating because a test caught it being wrong: a receipt that exists but
cannot be read aborts the scan entirely. Treating an unreadable receipt as "nothing installed" would
have offered the whole co-op loadout for deletion.

## 14. Wiring sweep, 2026-08-19

A pass over what the co-op work had NOT touched, looking for anything of Europe 1100's that our code
drops.

**Module and handshake wiring is correct.** Client and server launch tokens are identical apart from
the server-only `DedicatedServer.Windows`, activation order satisfies ops rule 5 (frameworks, base
game, `Coop`, then gameplay), and the catalog's expectations match the live loadout — the four
frameworks expected active on both roles and every catalogued gameplay mod expected inactive on both,
which is what the running loadout is. Since `FeatureActiveExpected*` is an equality policy, a
mismatch here would refuse every join; there is none.

**Handler registration cannot silently rot.** Handlers are discovered by namespace scan
(`InterfaceCollector`), not hand-registered, so an added handler is wired by construction.
`Europe1100CampaignAuthorityGate` is `AutoActivate`d, so it installs when the container is built
rather than on first resolve.

**`Europe1100Expanded` is data-only** — no `bin/`, so it declares no behaviours and correctly does not
appear in the authority gate.

**The authority gate was re-audited against the binaries rather than its own comments**, which found
two things (see §14a).

**854 "subscribed but never published" message types is a measurement artifact**, not dead wiring: the
transport publishes network messages reflectively on receipt, and the codebase uses target-typed
`new()` widely. Correcting for both leaves ~41, and the ones checked (`CreateKingdom`,
`HeroLevelChanged`, hero appearance fields) are legacy paths superseded by AutoSync or by
`AuthorityRoute` — e.g. kingdom creation now runs through
`AuthorityRoute<CreateKingdomIntent, NetworkRequestCreateKingdom, NetworkCreateKingdomResult>`, and
hero level replicates as `Hero_Level_SetNetworkMessage`. Dead code to remove, not behaviour to fix.

### 14a. Two corrections to the Europe 1100 authority gate

Both came from checking the shipped `SubModule.xml` and DLLs instead of trusting the comments:

- `WhileThyCome` **is** a declared submodule. The gate's note claimed it was not and that its six
  behaviours were listed only defensively against a reflective load. It loads; listing them was not
  optional, and had they been omitted every client would have been running unsynchronised party
  spawning.
- `EOE.CustomBattlePatch` is **now gated**. It had been excluded because its submodule was
  "tagged `DedicatedServerType="none"` and not on the dedicated allowlist" — but no submodule in that
  file carries a `DedicatedServerType` attribute and no such allowlist exists. Its
  `EoeCustomBattleCampaignBehavior` derives from `CampaignBehaviorBase` like the others, so it is
  confined to the host like the others.

Coverage is now provably complete: exactly four of the eleven Europe1100 assemblies plus
SnowballingKingdoms reference `CampaignBehaviorBase`, and every assembly referencing `CampaignEvents`
also declares one — so no EoE code subscribes to campaign events outside the gated set.
