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

## Phase B — ship (Phase A is green)
- Merge #14 → `development`.
- Build server DLLs (Serilog **2.x** for the .NET-Core server), re-pair `DedicatedServer.Core` SHA-pin via `DedicatedServerCompatibilityPatcher` (clears the stuck raid loop on restart).
- Deploy to grain.silo (`bannerlord-coop-seven` --user service; live Coop at `server/engine-mods/Modules/Coop`, core in the two `Win64_Shipping_Server/bin` dirs; stage via `upload-here/`, back up to `_mod_backups/`). Same `friendallmods1` save. Observe uptime.
- Dispatch client-stable + confirm launcher advertises the new build.

## Phase C — launcher sweep (CalradiaCoop, WPF .NET 8)
- **Logo (biggest gap):** no app/window `.ico`. Add Frontir icon (`C:/Users/Bryce/Desktop/frontir_logo_2_horizontal.png` — white shield crest, crop left ~126px, composite on dark Ink tile) → `<ApplicationIcon>` in csproj + `Icon=` on MainWindow; optionally swap the "F R O N T I R" text wordmark for the brand image.
- UI quick-wins: reset `UpdateText.Foreground` per render (stale color bleed), locate-game folder picker when Bannerlord not found, tooltips on trimmed ledger/update rows, open-log ghost button.
- Ship via `launcher-app-release.yml`.

## Notes / gotchas
- `dotnet` on PATH is SDK-less x86 → use `"C:\Program Files\dotnet\dotnet.exe"`.
- 127.0.0.1 loopback broken here → `dotnet test` can't run locally; CI is the gate.
- Client↔server join: no version reject at connection layer, but the in-game lobby browser gates on EXACT build version → client+server must deploy in lockstep.
- Server storm still spinning on old build with no players; Phase-B restart clears it.
