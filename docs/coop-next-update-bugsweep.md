# Co-op "Next Update" bug sweep — status board

Goal (2026-08-13): review the fork against upstream `Bannerlord-Coop-Team/BannerlordCoop`,
patch all known + reported bugs (reference PRs/issues), land everything on ONE PR (#14),
merge + deploy to grain.silo (build server DLLs, re-pair `DedicatedServer.Core`, observe
uptime, confirm launcher push), then a full launcher frontend/feature sweep + logo.

Historical execution constraint: this completed update stayed on game **1.4.7**
and did not adopt upstream's 1.4.8 bump. Future work is governed by the
[Bannerlord v1.4.8 migration guide](bannerlord-1.4.8-migration.md).

PR: https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/14
Branch: `25vid/fix-kingdom-tab-diplomacy-managers` (base: `development`)

## Phase O — battle upkeep + siege result/village defense correction — SHIPPED (2026-08-14)

- `Disabled` battle economy is party-scoped: a party currently in a battle pays no party wages and
  consumes no food. Other clan parties, income, and unrelated expenses continue normally. The packaged
  default is now `Disabled`; the live shared configuration remains unchanged.
- The 18:24 UTC live siege log showed `AttackerVictory` reconciled after its map event had already become
  inactive. The server now blocks campaign `MapEvent.Update` while an accepted live mission is occupied,
  so campaign simulation cannot defeat/capture the winner before the authoritative mission result arrives.
- Active slow village raids once blanket-disabled the defend option and rejected defender joins on the server.
  **Help defenders** now routes through the normal authoritative join, replicates the player's party onto the
  village side, and transitions the raid into its resistance battle. The server requires vanilla faction-side
  compatibility, safe-passage eligibility, and the village encounter radius (gate or coastal port) before accepting
  the defender join.
- Focused verification: GameInterface battle-upkeep/config tests 26/26; map-event authority E2Es 4/4;
  village-defense regressions 5/5. Both test projects build with zero errors. PR #17 merged as `1974e2994`;
  required workflow `31833738722` passed build, unit tests, and all eight E2E shards. Workflow
  `31834158937` published stable client `2026.08.14.1942` (ZIP SHA-256
  `aa1a144c08db8be8fbfea48d72871aca682a200eec610b29916ca88d043e1dff`). The server pair uses
  core SHA-256 `b63120bb035bf9305fca7c5195e86632fbfd86a49717ca20008a6e3798e1af95` and receipt SHA-256
  `64fa384166dde423c4382f4d443381655ddc2c72bdaa4de0b358f0b9ef51325d`; it loaded preserved save
  `friendallmods1`, reached `SERVING` on UDP 4200, and remained at `NRestarts=0`. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-1974e2994-20260814T194210Z`.

## Phase N — messenger + bandit surrender + kingdom vote correction — SHIPPED (2026-08-14)

- **Send Messenger:** the authenticated executor now treats messenger dispatch as a personal action, so a
  joined player embedded in the clan leader's party can send one. Party leadership is still required for the
  clan, kingdom, fief, and gold operations.
- **Bandit surrender:** the accepting client records the pending surrender before forwarding it. When the
  authoritative result arrives, all surrendered participants are staged in the player's prisoner loot roster
  and the encounter advances through the native prisoner screen and inventory-loot phase. The server accepts
  the surrender only from a registered participant and only for that player's opposing bandit side.
- **Kingdom decisions:** the authoritative vote manager counts only connected registered player clans. Dormant
  personal clans retained in an existing save no longer become invisible required voters that deadlock the
  decision popup. Disconnects also remove that clan's pending/final support and immediately resolve when all
  remaining connected clans have finalized. The regressions cover an offline saved clan, disconnect-after-final,
  and preview-then-disconnect; all 45 kingdom E2Es and 49 GameInterface kingdom tests pass through direct xUnit.
- **Release:** PR #16 merged as `bff4266fe`. Required workflow `31825410274` passed build, unit tests,
  and all eight E2E shards. Workflow `31825845733` published stable and nightly client
  `2026.08.14.1754` (ZIP SHA-256 `ac01a27109dade8fc1619c4a8c3b39205d10ba1f4100ad2c963ae99200404a8e`).
  The paired server core SHA-256 is `d204ccedc76ff5ae2d5879d7c83dd7e8d176601c16ba25c8ba2dd2c8f2ee3d53`;
  it restarted on preserved save `friendallmods1`, reached `SERVING` on UDP 4200, and remained at
  `NRestarts=0`. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-bff4266fe-20260814T175833Z`.

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

## Phase F — exact runtime wiring and battle teardown — SHIPPED (2026-08-13)

- **Kingdom failure, corrected diagnosis:** the latest client log first throws
  `TypeLoadException: Diplomacy.ViewModelMixin.DiplomacyPanelPrefabExtension` from
  `DiplomacyClientUiLifecycle.ResetForCampaign`, then continues with `campaignReady=true`, and finally throws
  from `DiplomacyCostCalculator.DetermineInfluenceCostForMakingPeace`. Decompilation of the exact shipped
  Diplomacy DLL shows that line dereferences `GlobalSettings<Diplomacy.Settings>.Instance`; it is not a layout
  or missing-prefab failure. The old candidate scan accepted both the unversioned loader assembly and the
  versioned implementation, so Coop could bind the wrong type universe and skip the settings patch.
- **Exact Diplomacy lifecycle:** one fingerprinted implementation assembly must supply every one of the sixteen
  supported UIExtender types. The whole group is disabled before resolution, the exact `Type` objects are cached,
  ten snapshot-backed extensions are enabled only after settings readiness, six retired extensions stay disabled,
  and any resolution/activation failure rolls the whole group back. Campaign readiness is published only after
  both runtime binding and UI reset succeed.
- **MCM/server settings contract:** MCM and UIExtenderEx remain client presentation modules and are rejected from
  server initialization. The dedicated server creates Diplomacy's canonical 66 scalar settings directly, forces
  the unsafe war-exhaustion debug option off, fingerprints the complete set, and sends it as campaign authority;
  clients apply exactly that set. The startup hook logs the selected Diplomacy assembly name/version/location so
  any future loader/implementation split is visible rather than swallowed.
- **Workshop runtime wiring:** retained launch history proved that the production token advertised eight Workshop
  modules while their server bins were absent. An explicit role manifest now requires server bins for
  PlayerSettlement, ImprovedGarrisons, DismembermentPlus, Fourberie, Diplomacy, and UnblockableThrust; it treats
  UIExtenderEx and MCM as presentation-only. The sync and verification tools compare exact file sets and hashes,
  reject presentation submodules on the server, verify the UI support-assembly closure, and fail startup on drift.
  Loading UIExtenderEx/MCM as server submodules was proven to cause the delayed abstract-`NewExpression`/native
  crash. With those presentation bins held, a 35-second post-serving same-save run kept all six gameplay runtime
  modules active and reached the complete registry audit.
- **Player Settlement early save lifecycle:** the installed mod implements `GetStore` as the static extension
  `CampaignExtensions.GetStore(Campaign, CampaignBehaviorBase)`; the previous reflection against a nonexistent
  Campaign instance method could never work. The exact extension is now resolved and invoked. Because the
  dedicated lifecycle can expose no behavior store during early object registration, the adapter admits that
  state only after current metadata, in-process legacy state, and the campaign-specific legacy external directory
  are all proven empty. That is the verified state of unchanged `friendallmods1`. Generated settlement graphs and
  new construction still fail closed: the pinned mod registers XML objects locally and save/reloads, which cannot
  be replayed safely on one peer.
- **Diplomacy post-serving capture:** fresh hosts had no initialized manager dictionaries when the first canonical
  snapshot ran. Capture now initializes Expansionism, Cooldown, DiplomaticAgreement, and WarExhaustion managers
  before shape validation. The exact agreement dictionary key is `Diplomacy.FactionPair`; the prior reflected
  namespace was wrong. The sustained same-save run broadcast the authoritative snapshot after both fixes.
- **Large-army reserves:** the prior one-way expansion signal was insufficient. Event 70760 delivered the full
  attacker feed while the defender still held the stale 81-troop entry allocation, so host election could latch
  a mixed generation. Each ownership expansion now starts an authoritative refresh generation and commits only
  after both attacker and defender reserve feeds arrive; packets arriving before supplier registration are kept.
- **Auburn retreat:** a native retreat can report `BattleResolved=true` while having no accepted attacker/defender
  winner. That excluded the early retreat request, and mission teardown removed membership without detaching the
  campaign party. `NetworkMissionLeft` now carries an authenticated unresolved-battle flag; the server derives the
  peer's party and MapEvent and performs idempotent campaign detachment in the same departure transaction.
- **Army registry from the beginning:** the base registry keyed every kingdom army by `Kingdom.StringId`, so only
  the first army in each kingdom could register. The repeated live `Failed to get id for object type Army` markers
  are the direct consequence. Every loaded army is now keyed by its stable leader-party ID, with a second party
  traversal for kingdom-free/modded armies. Before `AllGameObjectsRegistered`, a campaign-graph audit now proves
  registration of critical parties, armies, MapEvents, sides, components, and rosters or aborts startup.
- **Verification/deployment:** focused Diplomacy (120/120) and Player Settlement (24/24) regressions are green,
  as are the registry, reserve, mission-lifecycle, retreat, component, launcher, server-kit, Workshop authority,
  role-policy, XAML, and release-safety gates. The sustained host proof reached `CAMPAIGN LOADED`, authoritative
  snapshots, and `[RegistryAudit] PASS` with the same save. The full correct-location E2E result is 1,419 total,
  1,415 passed, four known skips, and zero failures. Source `4b731ead5bdc551086436cf5c353e6038b7c5ffc`
  published stable client `2026.08.13.2231` through workflow `31750036220`; its ZIP SHA-256 is
  `daa22eb6a031e3a198974493f45b0a41b2d1d6c7cf42d77ad063c242129d8aae`. The server-paired core is
  `7ab11dddbdc9a4b883eca944f6653a1a085d366fc395ab3cf4fde56e06fd9171`. Production stayed active with zero
  restarts through the post-serving window, emitted three pulses, bound UDP 4200, and left the unchanged
  `friendallmods1` save and sidecar at their original hashes. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-4b731ead5-20260813T224028Z`.

## Phase G — client join freeze: unpatchable generic Harmony targets (2026-08-13; source `6ce74c217`)

- **Symptom:** after the `4b731ead5` client shipped, every joining client soft-froze on the
  "Applying patches..." loading screen; the server parked all three peers in `state:"handshake"`
  indefinitely. Client log: `HarmonyException` patching
  `AbstractDiplomaticAction<FormNonAggressionPactAction>::TryApply` →
  `ArgumentException: The given generic instantiation was invalid`, thrown from
  `GameInterface.PatchAll()` inside `MainMenuState.Handle_NetworkConnected`, swallowed by the
  message broker (`Failed to run <null>`).
- **Root cause:** the .NET Framework client CLR refuses Harmony rewrites of methods **declared on a
  constructed generic type**; the server's .NET Core runtime accepts them, which is why the server
  kept booting clean. `Harmony.PatchCategory` aborts the whole category on its first failed job, so
  the join handshake died with it. Two live targets:
  1. `DiplomacyPlayerKingdomActionGuardPatch` → `TryApply` (declared on the generic action base;
     latent since `9899ac47e` on 2026-08-09, armed when `c9644be9c`'s wiring fixes made Diplomacy
     category registration actually succeed on clients).
  2. `DiplomacyClientSettingsBridge.GlobalSettingsInstancePatch` → `get_Instance` (lands on MCM's
     `GlobalSettings<Diplomacy.Settings>`; added 2026-08-13 in `c9644be9c`). Would have been the
     next abort once #1 was fixed.
- **Fix (`a0d1567f0` + `6ce74c217`):** new `HarmonyGenericTargetPolicy` — every Workshop
  `TargetMethods()` routes candidates through `CanPatch`; generic-declared targets are yielded only
  on runtimes that accept them (server keeps both), open generics never. Framework clients stay
  guarded via the concrete overrides (`ApplyInternal` + newly added `AssessCosts`); the MCM getter
  patch is skipped on clients, whose full MCM registers the real settings object (snapshot-apply
  boundaries remain the loud failure surface). `MainMenuState.Handle_NetworkConnected` now catches
  PatchAll failures: logs the real exception and tears down via `ICoopFinalizer` with a visible
  message instead of freezing.
- **Adversarial audit:** every Harmony target-resolution across all seven Workshop mods
  cross-checked against the pinned decompiles — no further generic-declared targets. Hardening
  note: `FourberieMethodSpec.Resolve`, `ImprovedGarrisonsMethodSpec.Resolve`, and
  `DiplomacyUnsupportedImplementationGate.TargetMethods` resolve without `DeclaredOnly`; a future
  mod build that stops overriding a member would silently retarget to a base. Not currently
  hazardous; tighten when next touched.
- Unit tests: `HarmonyGenericTargetPolicyTests` mirrors both real shapes (CRTP action base, MCM
  settings getter via declared-only base walk).

## Phase H — black Kingdom tab + full Diplomacy static-read audit (2026-08-13/14)

- **Black Kingdom tab, third and final null (`e91a46c90`):** with joins fixed, the tab still blacked
  out. `KingdomWarItemVMMixin` ctor → `DiplomacyCostCalculator.DetermineReparationsForMakingPeace` →
  `KingdomExtensions.IsRebelKingdomOf` → `RebelFactionManager.AllRebelFactions` (`=> Instance.
  RebelFactions`) with a null `Instance`. Friend Edition retires Diplomacy's civil war (Separatism
  owns rebellions), so nothing ever constructs the singleton on either role; the pinned cost
  calculator still consults its ledger for every war row. Fix: `RebelFactionManager` is the fifth
  ensured manager — existence only, canonically empty, never synced — in the snapshot-apply ensure
  block, ctor preflight, capture barrier, `RequiredManagerDictionaries`, and the assembly-audit
  `RequiredTypes`.
- **Why this class of bug recurs (mechanism):** Gauntlet rebuilds the whole Kingdom VM tree on every
  open; Diplomacy's UIExtenderEx mixins eagerly compute costs in VM constructors; co-op retires
  Diplomacy's CampaignBehaviors (they would desync peers), so every manager singleton those
  behaviors would construct exists only if Coop's ensure-lists create it. Any read the lists missed
  = construction-time NRE = black panel.
- **Full static-read audit (decompile × adapter cross-reference)** enumerated every `*.Instance`/
  static-singleton read in Diplomacy 1.4.7 and classified reachability. Result: all mixin-reachable
  reads of the five managers, MCM settings, and `DiplomacyEvents` are provided. Gaps found and fixed
  in this phase:
  - **GAP-1/2 — campaign-map war-exhaustion widget (found before it fired live):** Diplomacy's
    `UIBehavior.AddUIElements` runs on the first campaign tick (Coop allows it on clients; it is a
    MapView, so no UiLifecycle/readiness gate can reach it) and installs
    `WarExhaustionMapIndicatorVM`, whose item VMs call `WarExhaustionManager.Instance.
    GetWarExhaustion(...)` during construction — on a late join that precedes the snapshot apply.
    Fix: `DiplomacyClientInitializationPatch` now pre-creates **all five** managers at
    `MapScreen.OnInitialize` (sourced from `DiplomacyManagerCaptureBarrier.
    RequiredManagerTypeNames`), closing the map-build → snapshot window for every current and
    future ungated reader. (This also makes the `EnsureManager` doc contract true again — GAP-6.)
  - **GAP-5 — `diplomacy.*` console cheats bypassed authority:** `CampaignCheatsExtension` mutates
    ensured singletons and unreplicated state directly. Its six mutating cheats now route through
    the server-only `SharedMutationMethods` funnel; the two UI-debug cheats stay role-local.
  - **GAP-4 — verified closed by construction:** the only `DiplomacyAutomatedOperationScope.Enter()`
    site is `DiplomacyAutomatedKingdomActionContext`'s ctor, which creates the `BarterPlayerContext`
    in the same breath; the explicit-scope path enters one in `DiplomacyOperationHandler`.
- **GAP-3 — documented, not coded:** `WarExhaustionBehavior` server callbacks reach
  `PlayerHelper.GetOpposingKingdomIfPlayerKingdomProvided` → `Hero.MainHero.MapFaction`, which NREs
  on a dedicated host **only when `StoryModeManager.Current != null`** (`WarExhaustionManager.cs:613`
  returns early for sandbox). `friendallmods1` is a sandbox campaign, so this is unreachable today.
  If a story-mode campaign is ever hosted, wrap those callbacks in a `BarterPlayerContext` first.

## Phase I — village-raid loot-completion softlock ("End raid" dead button) (2026-08-14)

Live incident (2026-08-14 ~00:37 UTC, village_EW6_4, client Chipmunk on `6ce74c217`): after the
resistance battle, the client entered the looting wait-menu; the raid completed, and every
"End raid"/leave click was silently eaten — softlocked until game restart. Server journal shows the
resistance battle (`MapEvent_Created_73950`) conclude → continued-raid loot ticks (`ItemRoster`
scratch noise at 00:37:22) → the raiding party pulsing `mapEvent=none` with **no conclusion or
encounter close ever emitted** for the continued raid.

- **Root cause (server):** a slow village raid's loot phase concludes NATIVELY — `RaidEventComponent.
  Update` sets `BattleState = AttackerVictory` and `MapEvent.Update` calls `FinishBattle` →
  `FinalizeEventAux` directly. No mission result and no `NetworkChangeBattleState` means nothing
  publishes `MapEventConcluded`, so `BattleFinalizeHandler` (the only component that closes involved
  players' encounters) is bypassed entirely: the event is destroyed, the destroy replicates, and the
  raiding client is never told to leave its menu.
- **Root cause (client half):** with the local event unregistered by the replicated destroy, the
  End-raid consequence (`VillageRaidEndPatch`) published a `MapEventFinalizeAttempted` whose
  `TryGetIdWithLogging` could never resolve — silent early return, every click eaten, and the
  wait-menu's other leave options are patched identically.
- **Fix (server):** `MapEventPatches.Prefix_FinishBattle` — when the server natively finishes a
  raid-hostile-action event that contains player parties and is not yet finalized, publish
  `MapEventConcluded` first. The pipeline finalizes (marking dedup), moves raid attackers to the
  village gate, and sends `NetworkClosePvpEncounter`; the native `FinalizeEventAux` that follows
  no-ops on the already-finalized event. Also covers the no-winner variant (attacker side emptied).
- **Fix (client hardening):** `VillageRaidEndPatch` only routes a finalize request for an event the
  object manager can still resolve; otherwise it closes the local menu (detaching the stale event
  from the encounter and the main party so `Finish` does not re-publish an unresolvable finalize).
- **Tests:** `RaidLootingCompletion_ServerClosesRaidingPlayersEncounter` (drives real loot ticks to
  native completion; asserts the close + Looted state + destroy on server and clients) and
  `RaidEndRequest_UnresolvableLocalMapEvent_ClosesLocalRaidMenu` (stale local event → no finalize
  publish, encounter closed). Both fail without the fixes; full VillageHostileActionTests +
  CoopBattleFinalizeTests + MapEventLoadCleanerTests green locally (87 tests).

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

## Phase J — Equipment suppression, MapEvent robustness & ScoreboardTick restoration (2026-08-14)

- **Equipment UI Synchronization Storm (0xC0000005 AV):**
  - Diagnosis: Entering Character Developer, Inventory, Clan, or Party screens triggered unsuppressed equipment creation/modification on the UI thread, causing race conditions with the network thread and memory corruption.
  - Fix: Extended AllowEquipmentInGUI to dynamically suppress all declared methods across Gauntlet screens and ViewModels.
- **MapEvent Null Dereference:**
  - Guarded MapEvent.PlayerMapEvent and MapEvent.IsPlayerMapEvent against null MobileParty.MainParty.
- **Dedicated Server Harmony Safety:**
  - Guaranteed AllowEquipmentInGUI.TargetMethods() returns explicit methods on server to satisfy Harmony constraints while avoiding client-only UI reflection.
- **ScoreboardTickReadinessPatch Reinstatement (40b863f18):**
  - Restored missing SPScoreboardVM.OnTick finalizer guard against NullReferenceException during co-op retreat/encounter teardown.

## Phase K — Gemini review, crash triage, audit completion, secondary-update fixes (2026-08-14)

### Phase J (Gemini/Antigravity) review verdicts
- **Sound:** ScoreboardTick reinstatement (matches the held PR #10 fix), MapEvent
  `PlayerMapEvent`/`IsPlayerMapEvent` null guards, ObjectManager transient-`Created_*` Debug
  downgrade, LiveTestControlServer changes (repair of merge damage: duplicate `HandleCommandCatalog`
  removed, missing `TryParseStructuredResult` restored), `CoopMod.StartAsClient` call change (the
  method returns void since the launcher redesign — the old refusal check no longer compiled).
- **Net-zero:** launcher-release channel default was flipped to `both` (767c71999) and immediately
  reverted (1737916f6); nightly-only automatic publishing stands.
- **Ineffective as shipped:** the `AllowEquipmentInGUI` mass-wrap of 9 screen/VM types did NOT stop
  the equipment churn — the 00:03 crash session on the new build still logged 3,239
  `Equipment_DynamicPatches` + 1,659 `LifetimePatches<Equipment>` client errors (burst of 3,636/min
  during a battle at 23:44). Churn sources sit outside per-method windows (async agent-visual
  worker threads; post-`HandleFinalize` teardown). The allowance revoke was already
  exception-safe (finalizer, not postfix). Phase K adds the `ScreenManager.PopScreen` wrap for the
  teardown half; the worker-thread half remains open (see below).

### Crash triage — all 8 reports since 21:44 on 2026-08-13
- **5× exit 0xC0000005 (AV):** one family. Context every time: battles/loot + inventory,
  clan, or character screens; `IsReady`/Equipment client-churn floods precede each. Crashes
  continued on build `1737916f6` (with the Phase J equipment fix), so the fix did not close it.
  **No dump exists for any of them** — the TW dump prompt was cancelled each time, so the faulting
  module is unproven. The AutoSync/Lifetime client "errors" are log-only (local sets are applied,
  nothing is published or blocked), so the Phase J "sync storm memory corruption" mechanism is a
  hypothesis, not established. If the AVs continue after this update, capture one dump (accept the
  TW dialog) — that single artifact decides the diagnosis.
- **3× exit -1:** shutdown-path false positives — normal "Deleting Managed Interface" teardown with
  TW's known "Non-Zero Device Reference Count" exit error. Not gameplay crashes.
- **Crash-reporter defect (fixed):** four reports' `Coop_client.log` was the RELAUNCHED session's
  log — the post-dump-wait refresh re-copied the shared-path log after restart truncated it,
  destroying the evidence. The refresh now only accepts append-extensions of the crash-time copy.

### Workshop authority audit — COMPLETE (603f22e54)
- 13,310 of 13,729 required routes classified (was 8,162). Six of seven gameplay modules
  (UnblockableThrust, DismembermentPlus, Separatism, ImprovedGarrisons, **Bannerlord.Diplomacy
  4,121/4,121**, **PlayerSettlement 625/625**) are fully classified and now gated inside
  `WorkshopIntegration.Run-Tests` (real-audit validation, previously fixture-only).
- **Fourberie: 419 reviewed OPEN routes** held by a shrink-only ratchet
  (`tools/WorkshopIntegration/fourberie-open-routes.json`; new open routes fail the suite):
  - **FOURB-OPEN-1 (largest):** the stealth/fight-club/banditry **mission stack is un-adapted** —
    `FStealthMissionLogic` (128), mission controllers/spawners/`FourbCom`/`InsideMissionsHelper`
    (~70). End-of-mission consequences (`OnEndMissionInternal`, militia routines, dialog
    consequences) mutate campaign state on the entering client with no Coop route → silent desync
    whenever a client runs an infiltration/larceny/fight-club mission. Needs either typed
    consequence routing or a fail-closed mission-entry gate (product call: the gate removes crime
    missions from co-op).
  - **FOURB-OPEN-2:** unrouted behavior/menu consequences — `FourberieBehavior` (80: incl.
    `PlayerActionsConsequences`, `PickAction`, `OnConfirmLeaveKingdomWithOption`), fight-club (35),
    bandit (30), escape (3), safehouse behavior (7).
  - **FOURB-OPEN-3:** VM canonical-state writes without a route — `CriminalVM` (11: `FOpenStash`,
    pact/tribute list mutations), `FourbSafeHouseDataSourceVM` (5), detection view (2), misc.
- Five Diplomacy records whose campaign-mutation evidence is heuristic false positive (read-only
  `CanGrantFief`/`PreviewPositiveRelationChange`, UI event subscriptions) were hand-verified and
  classified PurePolicy.

### Fixes in this phase (904d0af8e)
- **Loot trade desync (user-visible):** `TradeHandler` silently dropped battle-loot roster elements
  whose `ItemObject` instance wasn't the registered catalog instance (fresh instance, same
  StringId) — the player kept loot locally that the server never received (32–36 hits per loot
  screen in last night's logs). Wire ids now resolve through the catalog StringId.
- **Kingdom decision noise/asymmetry:** `NetworkRemoveDecision` now passes the same own-kingdom
  gate as `NetworkAddDecision` (was: guaranteed "Index is out of bounds" + Clan→Kingdom cast
  errors on every broadcast for foreign kingdoms).
- **Inventory teardown window:** `ScreenManager.PopScreen` joined the equipment allowance wrap.
- **Crash-reporter log preservation** (above), with regression test.

### Secondary update (2026-08-14, client-only)
- **Scope:** everything unpushed from Phase J (ScoreboardTick reinstatement) + Phase K fixes
  (loot trade desync, kingdom-decision gate, PopScreen teardown window, crash-reporter log
  preservation). **No server redeploy required:** the trade fix changes the client sending path
  (the server already resolves StringId wire ids), the kingdom gate is client-side, the equipment
  wrap is inside the client-only branch, and the crash reporter is client tooling. grain.silo
  stays on its current pair, `NRestarts=0`.
- **Channel history note:** Gemini's 2026-08-14 02:31 UTC dispatch published to BOTH channels —
  the stable feed friends run has been on Phase J source `1737916f6` since 02:35 UTC (that is the
  build on five of last night's crash reports). This update supersedes it.
- **SHIPPED ✅ (2026-08-14 05:04 UTC):** push CI green (run `31771410901`, automatic nightly);
  stable dispatch run `31771623816` passed on source `3d5c2ad63` and published launcher manifest
  **`2026.08.14.0504`**, ZIP SHA-256
  `20688207b4ee142b953af46a167bfc916144824ea69da40630c9813a80389a23` — independently downloaded
  and hash-verified. Server untouched (`NRestarts` unaffected; client-only scope confirmed above).
- **Still requires a player action (rendered verification):** loot a battle → transfer items →
  server-side loot totals stay consistent (no `Failed to get id for Item` in the client log);
  close inventory near an encounter (the 0xC0000005 window) — and if an AV still occurs, ACCEPT
  the TW dump prompt this time; the preserved crash-time logs + one dump decide the diagnosis.

### Phase L — reviewed, hardened, and SHIPPED as the testable pair (2026-08-14 12:57 UTC)
- **Session review applied** (`8b6ef23b2`, high-effort /code-review over the whole session range):
  scoped the equipment teardown allowance (was opening every sync gate on every screen close),
  zombie death-mark clearing, deferred succession off the kill call stack with fresh-successor
  retry, arranged double-promise block, kingdom-decision gate fail-closed on unresolvable ids
  (with pre-registration grace), native comment-behavior reuse, `TryGetCatalogId` hoisted to
  ObjectManager and applied at six sibling silent-drop sites.
- **Army leave fixed** (`7837e4510`) — "join but never leave": stale MapEvent/siege residue no
  longer hides the Leave button; the leave consequence mirrors locally; the kicked-out path is
  routed; server-side removal authority (own party or army leader only). Upstream `33bf1031b`
  verified already present (ArmyDisbander).
- **Upstream easy wins merged**: 13 `-x`-stamped cherry-picks (kingdom vote freeze/recovery
  chain, character-creation transition guard, party-command checks). Skipped-as-present:
  inventory/party duplication, settlement party-forming, escort refresh. Deliberately deferred:
  troop-XP sync (~1,400-line feature), map-event load repairs (Phase D-F collision), live-test
  infra, wanderer content.
- **TESTABLE PAIR LIVE (lockstep)**: stable client **`2026.08.14.1251`** (ZIP SHA
  `546851cef222c3bab9dbaccd9f4b4dd20c1918f7e4ed84135b1fca1e2140797d`, independently verified) +
  server pair from the same code (paired core `2079129d1bd32b75035181134fc522319d57a566420557d2682560f3fe2e73df`),
  deployed with pin verification, `CAMPAIGN LOADED` → `SERVING` on `friendallmods1`, UDP 4200,
  `NRestarts=0`, pulses healthy 90s+ post-start. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-fa685f7f6-20260814T125655Z`.
- **Live smokes owed on this pair**: personal marriage via settlement-menu talk; one arranged
  marriage; army leave from the wait menu (incl. after a concluded battle); player death with an
  adult heir (succession handoff); loot a battle and verify totals.

### Phase M — 0xC0000005 dump diagnosis and staged correction (2026-08-14)
- **Exact artifact recovered:** Windows wrote
  `%LOCALAPPDATA%\CrashDumps\Bannerlord.exe.37960.dmp` (91,209,245 bytes) for the 09:57 EDT
  client crash on source `fa685f7f6`. It appeared after the Coop report's collection path had
  looked only under Bannerlord's `crashes` tree, which is why the report incorrectly said no dump.
- **Root cause proven in WinDbg:** the native tick boundary surfaced `0xC0000005`, but the fault is
  a managed `System.NullReferenceException` in
  `Diplomacy.ViewModelMixin.KingdomDiplomacyVMMixin.<.ctor>b__21_0`, dispatched by
  `CampaignEvents.OnMakePeace` from Coop's queued `FactionStanceHandler.HandleMakePeace` task.
  `Hero.MainHero.MapFaction` was a valid Kingdom; the null value was UIExtenderEx's weak
  `BaseViewModelMixin.ViewModel` target. The Kingdom screen had closed about ten seconds earlier,
  while its non-serialized listener remained subscribed. The Clan screen happened to be open when
  the incoming peace update ran and did not cause the crash.
- **Candidate fix:** a Diplomacy-category Harmony prefix covers the pinned 1.4.7 mixin's three exact
  constructor callbacks (peace, war, and alliance-ended). It permits refresh while the weak view
  model is live and skips only that stale presentation callback after collection. The authoritative
  stance action and campaign-event dispatch are not suppressed. The implementation gate now audits
  all three compiler-generated signatures against the exact pinned DLL.
- **Reporter fix:** dump discovery searches both Bannerlord's crash tree and Windows LocalDumps at
  `%LOCALAPPDATA%\CrashDumps`, then retains the existing PID/time validation and completion-copy
  retry. A regression creates a matching Windows-style late dump and proves discovery.
- **Verification / deployment boundary:** all five crash-reporter tests and all 155 Diplomacy
  namespace tests pass through the direct xUnit runner (the machine's known IPv4 vstest transport
  remains unusable). Release build and final gates are recorded with the candidate commit. Nothing
  was copied into the live client or server, and the server was not restarted. Promotion remains a
  lockstep client/server action after explicit green light; rendered proof should open then close
  Kingdom and receive peace/war/alliance updates while another screen is active.

### Phase N — native player clan membership — SHIPPED (2026-08-14; source `da18f9b95`)
- **Consent and eligibility:** player-to-player clan joining uses the existing party interaction
  flow, requires an affirmative leader response, and is available only when the receiving player's
  clan is Tier 2+. The server revalidates the current clan leader on acceptance. Optional
  player marriage uses native suitability and reciprocal spouse/romance state without moving either
  player between clans.
- **Ownership and party control:** first join permanently gives the receiving clan/leader the
  applicant's fiefs, workshops, caravans, alleys, gold, troops, prisoners, and inventory. The member
  is embedded in the leader's real party, so that party's leader owns map control and shared results;
  hero XP is not pooled. Registration updates precede replicated destruction of the retired party,
  preventing client-side zombie parties. Re-embedding is limited to the same joined clan.
- **Separation and exit:** a joined player may request a leader-approved hero-only party subject to
  the native party cap, or leave the clan without approval and return to the persisted personal clan.
  Transferred assets and shared gold remain with the joined clan. Dormant personal clans are retained
  while their player is away.
- **Offline safeguard:** leader disconnect creates emergency independent parties for embedded
  members before parking the leader party. Emergency creation may exceed the cap, further voluntary
  creation remains blocked by the cap, rejoin is voluntary, and a one-time carrier-pigeon notice is
  sent after leader return.
- **Review and verification:** full PR/CI review corrected stale-leader approval, roster XP removal,
  cross-clan re-embedding, old-party lifecycle replication, personal-marriage tuple orientation,
  and an unauthenticated siege-leave fixture. Release solution build completes with zero errors.
  Focused direct-xUnit coverage is 223 passing cases: CrashReporter 5, Diplomacy 155, patch
  registration 6, membership/restore/save/visibility units 39, and interaction/marriage/siege E2Es 18.
  PR #15 merged only after workflow `31818854020` passed build, test, and all eight E2E shards.
- **Release:** workflow `31819338262` published stable and nightly client `2026.08.14.1630` from the
  exact merge. The matching server pair uses core SHA-256
  `d3975ba0920186448cdce04eea29af4f1c0764efa03953bc7e784357586eff70`; it preserved and loaded
  `friendallmods1`, reached `SERVING` on UDP 4200, and remained at `NRestarts=0`. Byte-verified
  rollback snapshot: `/home/bishop/bannerlord-coop/server/_mod_backups/pre-da18f9b95-20260814T163244Z`.
  Rendered clan-join, independent-party, departure, player-marriage, and succession proof still
  requires player actions and is not inferred from headless server health.

### Still open after this phase
- **Equipment/IsReady client ERR floods** (~5k/min in battles) — worker-thread churn is
  by-design-unsynced but logged at ERR through Serilog on hot paths; wants a throttle/dedup plus a
  decision on worker-thread allowances. The recovered dump proves this crash came from the stale
  Diplomacy listener rather than those log paths, so they remain a separate observability/performance
  issue rather than the current crash mechanism.
- **Clan-registered-under-Kingdom-id** (`Could not cast (Clan) ... to Kingdom` while resolving a
  server-sent kingdom id): now silenced at the decision path, but the underlying cross-peer
  `Created_*` id divergence hint deserves a look if rebel-kingdom desyncs appear.
- Fourberie open routes (above), `NetworkUpdatePartyBehavior` storm (needs live profiling), #2632
  Separatism-compat rework — unchanged.
