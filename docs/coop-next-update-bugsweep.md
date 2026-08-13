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
> distinct modifier stacks. A2 fabricated null internal managers. Phase D fixed the then-observed client-null
> MCM `GlobalSettings<Diplomacy.Settings>.Instance`, but the next live run exposed the remaining transport race:
> the settings read succeeded and `WarExhaustionManager.Instance` was null because the server discarded the
> client's only snapshot request before its player mapping existed. Phase E supersedes both symptom mechanisms.

| # | Bug | Fix | Commit |
|---|-----|-----|--------|
| A1 | Raid softlock + loot "numbers don't add up" + ~1 MB/s server storm | Superseded: scratch-roster removal was retained, but the item delta required aggregation across modifier stacks (`4434f7505`). | 30a2e38a |
| A2 | Kingdom→Diplomacy tab black-screen + input freeze (+ war-vote / all diplomacy actions) | Superseded twice: Phase D fixed the missing settings instance; Phase E fixes the dropped authoritative snapshot and guards native row construction (`bd425fa09`). | c786b1ad / 013a9cb6 |
| A3 | 4 more `new ItemRoster()` sync storms (BattleRetreat.RemoveGoods, VillageHostileAction.ApplyForceSupplies, ItemRosterInterface.GetItemRosterFromData, Workshops warehouse ×2) | `ToList()` / `AllowedThread`. TroopRoster scratch rosters checked + excluded (publish is registration-gated). | 013a9cb6 |
| A5 | #2776 parties stuck in abandoned map events / stuck lords (softlock; upstream #2704/#2933) | Reinstated (revert-the-revert). Confirmed Separatism-safe: full E2E green with it. | a2a952e7 |

**Historical verification:** build-green + full E2E green, but subsequent live use disproved the raid and
Kingdom-tab completion claims. Phase D fixed the raid path; Phase E owns the remaining Kingdom correction.

## Phase D — evidence-driven corrective release — SHIPPED ✅ (2026-08-13; source `315be775e`)

- **Kingdom/Diplomacy (`9105c7cc8`):** provides a client-only per-campaign settings fallback when MCM has no
  `GlobalSettings` instance, enables the exact UIExtender group only after an authoritative snapshot commits,
  and removes the broad manager-fabrication/readiness patches. **Superseded for completion by Phase E:** the
  snapshot request itself was still dropped by an impossible early player-mapping prerequisite.
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
- **Client release:** stable workflow `31708916585` passed on exact source `315be775e`; launcher manifest
  `2026.08.13.1416` serves `Coop-client.zip` SHA-256
  `1bc2bed1ebaa1370e9784de256f50357cdac1435ed5a03ceddb727a406c0365f`.
- **Server release:** the matching Serilog-2.x assemblies and both paired core copies passed their release pins
  and deployment ledger. Paired core SHA-256 is
  `058c2646b5d5685305a7965cd3de984a7c2ae4918ae8281eaa4a55c7cf36c404`. The service loaded the existing
  `friendallmods1` world, reached `SERVING` on UDP 4200, emitted repeated pulses, and remained at zero restarts
  with no pin-verification or unhandled-fatal marker. Byte-verified rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-315be775e-20260813T141932Z`.

## Phase E — Kingdom handshake + siege army convergence — SHIPPED ✅ (2026-08-13; source `4c711e778`)

- **Latest live Kingdom evidence:** client run 21140 reached `KingdomState`, successfully read the Diplomacy
  settings, then failed in `DetermineInfluenceCostForMakingPeace` because
  `WarExhaustionManager.Instance` was null. Native frame tick then repeated its null reference 2,197 times.
  The server journal contains no `NetworkDiplomacySnapshot` delivery for that join: `CampaignReady` sent the
  request before `NetworkPlayerCampaignEntered`, and the old handler rejected peers without a player mapping.
- **Handshake repair (`bd425fa09`):** the request carries the exact already accepted mod-config protocol,
  session, revision, and SHA-256. The server validates that identity and can reply before player registration;
  malformed or mismatched requests fail closed. The protobuf wire shape and the pre-mapping delivery path have
  direct regressions.
- **Kingdom boundary (`bd425fa09`):** the client retains only a validated authoritative snapshot. Before native
  `KingdomDiplomacyVM.RefreshValues` calls `RefreshDiplomacyList` and constructs Diplomacy UIExtender rows, Coop
  verifies `Settings.Instance`, the complete host MCM fingerprint, and all four manager singleton/dictionary
  shapes. A lost singleton is rebuilt only by reapplying the retained trusted snapshot; otherwise row
  construction is blocked instead of allowing the black-screen exception loop.
- **MCM contract:** the server's complete Diplomacy settings set is authoritative. Clients apply it, compare an
  exact fingerprint immediately, and compare it again at the Kingdom UI boundary; local client MCM drift cannot
  silently own campaign calculations.
- **Siege army leave (`7fb31d007`):** the follower **Leave Army** siege menu no longer assigns
  `MobileParty.MainParty.Army = null` client-only. It publishes the standard authoritative removal and mirrors it
  locally; the E2E regression proves the server and every client remove the same party from the same army.
- **Incremental verification:** affected projects build Release with zero errors. Diplomacy compatibility is
  111/111; 81 E2E cases pass across Diplomacy patch/command authority, siege leave, army lifecycle/waiting,
  mission-ready election, full reserve construction/reconnect, reinforcement spawning/quotas, retreat teardown,
  and unstuck recovery. The prior `44f6405f5` reserve-expansion and retreat fixes remain included.
- **Release gate:** `4c711e778` fixes the test-only game-thread ownership race caught by the first workflow.
  Stable workflow `31714742241` then passed 2,481 unit/integration tests with 14 intentional skips and built the
  exact-source Serilog-4.x runtime with zero errors. Pre-install ZIP reconciliation caught an independent packaging
  omission: `ModuleData` and `workshop-mods.json` were absent even though `SubModule.xml` requires the tournament
  item XML. That incomplete package was not installed on the production client. `d11ab502e` adds a release-safety
  regression for those files and every declared ModuleData XML reference. Stable workflow `31716900189` passed the
  full pipeline and published launcher manifest `2026.08.13.1547` with ZIP SHA-256
  `9ffd0cf7844c87edf0794acd1c42104376a6b15deec668f19383772d89b519ea`. Independent extraction and post-launcher
  install comparison confirm all 66 payload files match byte-for-byte, the four Coop assemblies identify package
  source `d11ab502e`, and the client references Serilog 4.2.
- **Server release:** the four exact-source Serilog-2.x assemblies are pinned to paired core SHA-256
  `296dfc03969512e9df9a92e2b9ac07360302717158a2cc0bb8e2fd2498339a19`; both physical core copies, the pairing
  receipt, and every deployment-ledger entry verify. The stopped `friendallmods1` save set was inventoried and
  copied byte-for-byte to `/home/bishop/bannerlord-coop/server/_mod_backups/pre-4c711e778-20260813T152718Z`.
  The initial live save and JSON matched that backup before configured autosaves resumed. The same Summer 15, 1093
  world loaded, reached `SERVING` on
  UDP 4200, emitted repeated pulses, and remains active at `NRestarts=0` without a pin-verification,
  null-reference, or unhandled-fatal marker.
- **Still requires a player action:** rendered Kingdom/large-army/retreat verification. This is not inferred
  from headless deployment health.

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

## LIVE rendered verification still owed on the current Phase-E pair
The local and deployment gates are green; rendered verification must use launcher client `2026.08.13.1547`:
1. Raid a village/town → no softlock, loot totals sane, no server storm.
2. Open Kingdom→Diplomacy, declare war / make peace → no black screen.
3. Enter a large allied-army field battle → the host fields the full proportional army reserve, not only its
   own entry-time party allocation.
4. Retreat from an unresolved battle mission → the encounter closes and does not offer a stale attack retry.

## Notes / gotchas
- `dotnet` on PATH is SDK-less x86 → use `"C:\Program Files\dotnet\dotnet.exe"`.
- 127.0.0.1 loopback broken here → `dotnet test` can't run locally; CI is the gate.
- Client↔server join: no version reject at connection layer, but the in-game lobby browser gates on EXACT build version → client+server must deploy in lockstep.
