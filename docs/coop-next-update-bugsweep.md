# Co-op "Next Update" bug sweep — status board

Goal (2026-08-13): review the fork against upstream `Bannerlord-Coop-Team/BannerlordCoop`,
patch all known + reported bugs (reference PRs/issues), land everything on ONE PR (#14),
merge + deploy to grain.silo (build server DLLs, re-pair `DedicatedServer.Core`, observe
uptime, confirm launcher push), then a full launcher frontend/feature sweep + logo.

Fork is pinned to game **1.4.7** — do NOT adopt upstream's 1.4.8 bump.

PR: https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/14
Branch: `25vid/fix-kingdom-tab-diplomacy-managers` (base: `development`)

## Phase A — fixes shipping on PR #14 (all CI-green: build + unit + 8 E2E incl. Separatism)

> **2026-08-13 correction:** the live reports showed that A1/A2 did not fix their user-visible defects.
> A1's scratch-roster removal reduced one replication storm but its per-row item dictionary still overwrote
> distinct modifier stacks. A2 fabricated null internal managers, while the actual Kingdom-tab exception was
> the client-null MCM `GlobalSettings<Diplomacy.Settings>.Instance` read during VM construction. The Phase-D
> fixes below supersede those mechanisms; do not restore the A1/A2 symptom patches.

| # | Bug | Fix | Commit |
|---|-----|-----|--------|
| A1 | Raid softlock + loot "numbers don't add up" + ~1 MB/s server storm | Superseded: scratch-roster removal was retained, but the item delta required aggregation across modifier stacks (`4434f7505`). | 30a2e38a |
| A2 | Kingdom→Diplomacy tab black-screen + input freeze (+ war-vote / all diplomacy actions) | Superseded: manager fabrication was removed; a client settings fallback and snapshot-gated UI lifecycle now address the actual constructor exception (`9105c7cc8`). | c786b1ad / 013a9cb6 |
| A3 | 4 more `new ItemRoster()` sync storms (BattleRetreat.RemoveGoods, VillageHostileAction.ApplyForceSupplies, ItemRosterInterface.GetItemRosterFromData, Workshops warehouse ×2) | `ToList()` / `AllowedThread`. TroopRoster scratch rosters checked + excluded (publish is registration-gated). | 013a9cb6 |
| A5 | #2776 parties stuck in abandoned map events / stuck lords (softlock; upstream #2704/#2933) | Reinstated (revert-the-revert). Confirmed Separatism-safe: full E2E green with it. | a2a952e7 |

**Historical verification:** build-green + full E2E green, but subsequent live use disproved the raid and
Kingdom-tab completion claims. Phase D owns their replacement verification.

## Phase D — evidence-driven corrective release (2026-08-13; deployment in progress)

- **Kingdom/Diplomacy (`9105c7cc8`):** provides a client-only per-campaign settings fallback when MCM has no
  `GlobalSettings` instance, enables the exact UIExtender group only after an authoritative snapshot commits,
  and removes the broad manager-fabrication/readiness patches.
- **Encounter completion (`435385a1f`):** replaces broad bandit scanning/exception swallowing with an
  authenticated typed capture command, server-derived party validation, and an idempotent native
  `BattleResultsReady` signal before synchronized MapEvent destruction.
- **Raid accounting (`4434f7505`):** sums the before/after item counts by `ItemObject`, so modifier variants no
  longer overwrite each other and each item produces one net positive loot delta.
- **Army battles + retreat (`44f6405f5`):** initial host election now signals reserve-ownership expansion before
  full side feeds; an unresolved mission retreat now closes the requester's encounter instead of returning to a
  stale attack menu. Live Auburn evidence: 815 attackers vs 2,736 defenders, initial own reserve 81, full host
  feeds sent but never queued; after departure, two stale mission-start retries were rejected.
- **Verification:** build 0 errors; 2,491 unit/integration tests + 1,412 E2E tests passed, with 18 documented
  skips total. Explicit Fourberie 337/337, Separatism 59/59, launcher 65/65.

## Deferred — reverted upstream fixes that break Separatism (need dedicated compat work, NOT bundled)

- **#2632** (companion fiefs; closes clan-menu-black softlock #2860 + Give-Settlement #2790) — **CONFIRMED** to re-break `SeparatismCampaignFlowTests.ChaosStart…SynchronizesTheCreatedKingdom` (rebel kingdom named "Former Rebel Kingdom" vs expected "Kingdom of Rebel Clan"). Its `ClanName` sync (`ClanNameHandler`/`ClanNameChangePatch`) collides with Separatism rebel-kingdom naming. Earlier session mis-attributed this to #2867. Fixes no *user-reported* bug → dropped from this update; needs Separatism-compat rework.
- **#2857** (abdication banner decouple #2845) — cosmetic; was bundled with #2632, dropped with it. Likely innocent; re-attempt standalone later.
- **#2867** (start war on neutral simulated battle #2835) — left reverted; revisit with the #2632 Separatism-naming work.

## Deferred — perf (needs live profiling, not a correctness bug)
- **`NetworkUpdatePartyBehavior`** storm (960 KB/10s during raids): `MobilePartyBehaviorHandler` publishes on every `RecalculateShortTermBehavior` with no server-side delta check. Safe fix = gate publish on serialized snapshot being byte-identical to last sent — BUT if that channel is unreliable the redundant sends may mask packet loss; needs live profiling before shipping. Raid softlock's *primary* cause (A1) already fixed.
- `TownMarketData__itemDict_Upsert` raid flood — cause unconfirmed (AutoSync of `_itemDict` during raid recalculation); investigate later.

## Phase B — SHIPPED ✅ (merge eae6d14e2)
- Merged #14 → `development`.
- Server DLLs rebuilt Serilog-2.x (flip Common.csproj→2.12.0 + drop Sinks.Seq + LogManager Seq line; GameInterface/Coop.Core are netstandard2.0, ref Serilog 2.0.0.0 verified). Only GameInterface.dll + Coop.Core.dll changed (Common/Coop.Steam pulled live + reused).
- Re-paired `DedicatedServer.Core` → paired sha `189d7c9e1bf2b37d…`; pinned GameInterface `aee3ab47…`, Coop.Core `8e1b6d40…`, Common `b76a527b…`, Coop.Steam `a27674a3…`. Loader-input core `8b67ff34…` (unchanged).
- Deployed to grain.silo (`engine-mods/Modules/Coop/bin/Win64_Shipping_Server` + both core dirs). **NRestarts=0, no exit-4**, `CAMPAIGN LOADED` → `SERVING`, UDP 4200 up, `friendallmods1`. Backup: `_mod_backups/pre-eae6d14e2-20260813T074627Z`.
- Client-stable published `2026.08.13.0740` (sha d056c366…). Friends direct-connect via launcher `/coopjoin` (no build-version gate) → join fine despite server keeping its prior Common version stamp.
- **Historical live claim superseded by Phase D:** raid and Kingdom/Diplomacy remained broken after this deploy.

## Phase C — launcher sweep — SHIPPED ✅ (stable `launcher-app` 2026.8.13.21, source 830f9ec2e)
- **Logo:** added `Frontir.ico` (white shield crest cropped from the brand lockup, on the dark Ink tile + gold ring; multi-size 16→256) wired via `<ApplicationIcon>` + embedded `<Resource>` + Window `Icon=`. Verified offscreen render — frontend intact, server showed ONLINE.
- Fixed `UpdateText.Foreground` colour-bleed (reset to Steel in RenderSnapshot/RenderProgress); added hover tooltip revealing the trimmed dispatch line (suppressed when empty).
- Fixed a **pre-existing flaky launcher test** (`ExactInstall_RetriesWhileScannerTemporarilyLocksStagedFile`) that blocked the stable publish: scanner now polls on a dedicated LongRunning thread instead of the saturated thread pool.
- Not done (optional follow-ups): locate-game folder picker, open-log button, `shootMode` fake-data guard.

## LIVE verification still owed after Phase-D deployment
The local gates are green; rendered verification must use the Phase-D client/server pair:
1. Raid a village/town → no softlock, loot totals sane, no server storm.
2. Open Kingdom→Diplomacy, declare war / make peace → no black screen.
3. Enter a large allied-army field battle → the host fields the full proportional army reserve, not only its
   own entry-time party allocation.
4. Retreat from an unresolved battle mission → the encounter closes and does not offer a stale attack retry.

## Notes / gotchas
- `dotnet` on PATH is SDK-less x86 → use `"C:\Program Files\dotnet\dotnet.exe"`.
- 127.0.0.1 loopback broken here → `dotnet test` can't run locally; CI is the gate.
- Client↔server join: no version reject at connection layer, but the in-game lobby browser gates on EXACT build version → client+server must deploy in lockstep.
- Server storm still spinning on old build with no players; Phase-B restart clears it.
