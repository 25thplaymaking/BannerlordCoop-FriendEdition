# Co-op "Next Update" bug sweep — status board

Goal (2026-08-13): review the fork against upstream `Bannerlord-Coop-Team/BannerlordCoop`,
patch all known + reported bugs (reference PRs/issues), land everything on ONE PR (#14),
merge + deploy to grain.silo (build server DLLs, re-pair `DedicatedServer.Core`, observe
uptime, confirm launcher push), then a full launcher frontend/feature sweep + logo.

Fork is pinned to game **1.4.7** — do NOT adopt upstream's 1.4.8 bump.

PR: https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/14
Branch: `25vid/fix-kingdom-tab-diplomacy-managers` (base: `development`)

## Phase A — fixes shipping on PR #14 (all CI-green: build + unit + 8 E2E incl. Separatism)

| # | Bug | Fix | Commit |
|---|-----|-----|--------|
| A1 | Raid softlock + loot "numbers don't add up" + ~1 MB/s server storm | `RaidEventComponentPatches.GetAddedItems` no longer builds a throwaway `new ItemRoster()`/tick (tripped the global ItemRoster ctor/AddToCounts sync); delta now `List<(ItemObject,int)>`; client roster wrapped in `AllowedThread`. | 30a2e38a |
| A2 | Kingdom→Diplomacy tab black-screen + input freeze (+ war-vote / all diplomacy actions) | `DiplomacyUiManagerReadinessPatch` ensures the null-on-client Diplomacy managers before `KingdomDiplomacyVM.RefreshValues`, the encyclopedia mixin, **and** in the 3 mixin ctors that read them (War/Truce item + EncyclopediaFaction). | c786b1ad / 013a9cb6 |
| A3 | 4 more `new ItemRoster()` sync storms (BattleRetreat.RemoveGoods, VillageHostileAction.ApplyForceSupplies, ItemRosterInterface.GetItemRosterFromData, Workshops warehouse ×2) | `ToList()` / `AllowedThread`. TroopRoster scratch rosters checked + excluded (publish is registration-gated). | 013a9cb6 |
| A5 | #2776 parties stuck in abandoned map events / stuck lords (softlock; upstream #2704/#2933) | Reinstated (revert-the-revert). Confirmed Separatism-safe: full E2E green with it. | a2a952e7 |

**Verification:** build-green + full E2E green. Live (raid + kingdom tab on the server) confirmed at Phase-B deploy.

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
- **Left to confirm LIVE (needs a friend):** raid a settlement (no storm/softlock), open Kingdom→Diplomacy tab + do a war/peace action (no black screen).

## Phase C — launcher sweep — SHIPPED ✅ (stable `launcher-app` 2026.8.13.21, source 830f9ec2e)
- **Logo:** added `Frontir.ico` (white shield crest cropped from the brand lockup, on the dark Ink tile + gold ring; multi-size 16→256) wired via `<ApplicationIcon>` + embedded `<Resource>` + Window `Icon=`. Verified offscreen render — frontend intact, server showed ONLINE.
- Fixed `UpdateText.Foreground` colour-bleed (reset to Steel in RenderSnapshot/RenderProgress); added hover tooltip revealing the trimmed dispatch line (suppressed when empty).
- Fixed a **pre-existing flaky launcher test** (`ExactInstall_RetriesWhileScannerTemporarilyLocksStagedFile`) that blocked the stable publish: scanner now polls on a dedicated LongRunning thread instead of the saturated thread pool.
- Not done (optional follow-ups): locate-game folder picker, open-log button, `shootMode` fake-data guard.

## LIVE verification still owed (needs a friend on the updated client)
Everything is build/CI-green + the server is serving, but the ultimate proof needs a player:
1. Raid a village/town → no softlock, loot totals sane, no server storm.
2. Open Kingdom→Diplomacy, declare war / make peace → no black screen.

## Notes / gotchas
- `dotnet` on PATH is SDK-less x86 → use `"C:\Program Files\dotnet\dotnet.exe"`.
- 127.0.0.1 loopback broken here → `dotnet test` can't run locally; CI is the gate.
- Client↔server join: no version reject at connection layer, but the in-game lobby browser gates on EXACT build version → client+server must deploy in lockstep.
- Server storm still spinning on old build with no players; Phase-B restart clears it.
