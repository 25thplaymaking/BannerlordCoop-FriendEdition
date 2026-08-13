# Co-op "Next Update" bug sweep — status board

Goal (2026-08-13): review the fork against upstream `Bannerlord-Coop-Team/BannerlordCoop`,
patch all known + reported bugs (reference PRs/issues), land everything on ONE PR (#14),
merge + deploy to grain.silo (build server DLLs, re-pair `DedicatedServer.Core`, observe
uptime, confirm launcher push), then a full launcher frontend/feature sweep + logo.

Fork is pinned to game **1.4.7** — do NOT adopt upstream's 1.4.8 bump.

PR: https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/14
Branch: `25vid/fix-kingdom-tab-diplomacy-managers` (base: `development`)

## Phase A — fixes (all on PR #14)

| # | Bug | Root cause | Status | Verified |
|---|-----|-----------|--------|----------|
| A1 | Raid softlock + loot "numbers don't add up" + ~1 MB/s server storm | `RaidEventComponentPatches.GetAddedItems` built a throwaway `new ItemRoster()`/tick → tripped global ItemRoster ctor/AddToCounts sync (`NetworkCreateItemRoster`+`NetworkItemRosterUpdate`+"Failed to get id" spam). | **Fixed** (commit 30a2e38a) — delta now a `List<(ItemObject,int)>`; client roster wrapped in `AllowedThread`. | Build ✅ · live-raid ❌ pending |
| A2 | Kingdom→Diplomacy tab black-screen + input freeze | `WarExhaustionManager.Instance` null on client after save-load; `MapScreen.OnInitialize` pre-init hook doesn't re-fire for the loaded campaign. | **Fixed** (commit c786b1ad) — `DiplomacyUiManagerReadinessPatch` ensures managers before `KingdomDiplomacyVM.RefreshValues` + encyclopedia `OnRefresh`. | Build ✅ · live ❌ pending |

### From this session's live logs — investigate/confirm
- Raid also storms `NetworkTroopRosterElementBatch` (2.5 MB/10s), `NetworkUpdatePartyBehavior` (960 KB/10s), `TownMarketData__itemDict_Upsert` (9.8k/10s) — same scratch-object class? (code-scan agent running)
- `Failed to get id … "A.F+A"` ×902 during raid — obfuscated type; source unknown.

### Upstream cross-reference (research agent running)
- Enumerate merged bugfix PRs + open issues on `Bannerlord-Coop-Team/BannerlordCoop`, exclude 1.4.8 bump, check which our fork lacks.

## Phase B — ship (after Phase A green)
- Merge #14 → `development`.
- Build server DLLs (Serilog **2.x** for .NET-Core server: Common.csproj/Coop.csproj/LogManager), re-pair `DedicatedServer.Core` SHA-pin via `DedicatedServerCompatibilityPatcher` (clears the stuck raid loop on restart).
- Deploy to grain.silo (`bannerlord-coop-seven` --user service), observe uptime, same `friendallmods1` save.
- Dispatch client-stable + confirm launcher advertises the new build.

## Phase C — launcher sweep
- CalradiaCoop launcher: frontend + feature deployment pass, fix-ups.
- Add a logo.

## Notes / gotchas
- `dotnet` on PATH is SDK-less x86 → use `"C:\Program Files\dotnet\dotnet.exe"`.
- 127.0.0.1 loopback broken here → `dotnet test` can't run locally; CI is the gate.
- Client↔server join: no version reject at connection layer, but the in-game lobby browser
  gates on EXACT build version → client+server must deploy in lockstep.
- Server storm still spinning on old build with no players; Phase-B restart clears it.
