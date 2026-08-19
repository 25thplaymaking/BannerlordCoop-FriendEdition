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

**Where to look next.** Whether AI parties ever select a client-controlled party as a target at all
(native targeting leans on `MainParty`/`IsPlayerParty` in several models), and whether
`HandleEncounterForMobileParty` is reaching the collision test for them. Note the design intent:
any suppression of hostility should apply **only during battle**, never on the campaign map.

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

EoE's ~2711 parties and its far larger settlement/market set multiply every per-party and
per-settlement replication route. This is a strong candidate for client-side stutter that is
*independent* of the autosave stall in §5, and it scales with player count — worth profiling before
more friends join.

## 7. Host: ~4.5 s game-thread stall every 5 minutes (autosave)

Not a bug — a big world meeting a synchronous save.

| autosave (UTC) | stall |
|---|---|
| 23:59:16 | 3826 ms |
| 00:09:16 | 3995 ms |
| 00:24:16 | 4096 ms |
| 00:34:17 | 4564 ms |
| 00:39:17 | 4544 ms |

Save payload is **204.9 MB** uncompressed (ObjectData 112 MB + ContainerData 87 MB, ~2711
parties), against roughly 19 MB for the old Calradia world. It **plateaus** rather than growing
without bound — object data went flat at 112.4 MB and container data settled at 87.3 MB as the
world filled in — so the stall stabilises around 4.5 s rather than climbing all session.

**Levers.**
- `autosaveMinutes` in `CoopData/DedicatedServer/server-config.json` (currently `5`). Read at boot,
  no config watcher, so a change needs a restart. Trades spike frequency against progress lost on a
  host death.
- Proper fix: the save should not block the game thread. `DedicatedServer.Core` already owns
  autosave — it has `NativeAutoSaveSuppressPatch` and an `AutosaveMinutes` setter — so the hook to
  make it asynchronous, or to defer it to a natural pause, exists. **That module's source is not in
  this repo**, which is the blocker.

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
