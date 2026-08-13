# Frontir Coop — Program Status

Living board for the modded co-op productization. Update at each milestone.
Companion docs: `doc/COOP-MOD-INTEGRATION.md` (how the port works),
`doc/COOP-OPS-WORKFLOW.md` (ops rules + checklist).

> **AUTHORITY ROUTING IN PROGRESS (2026-08-11): prior RC superseded; corrective stable live.** The exact function
> ledger covers 41,050 methods across the ten active Workshop modules, retired RBM, and integrated
> Separatism. Deterministic IL evidence now includes static shared-state writes and collection mutations,
> identifying 13,729 authority candidates; 8,151 exact records are classified and release validation
> rejects the remaining 5,578 active gameplay records.
> All six active gameplay adapters now share `IWorkshopModule`, and a trusted host capability snapshot
> is green in unit/E2E tests. UnblockableThrust's four candidates are owned by the accepted collision
> authority, and all 17 DismembermentPlus candidates now use a deterministic, deduplicated replicated-
> cosmetic route without replaying damage. Separatism's 57 candidates are also closed: its recovered
> fallen-clan option now uses an authenticated, revision-checked, replay-safe server transaction, and
> settlement rebellion cannot run from a client. Improved Garrisons' 723 candidates are now closed too:
> management/settings commands carry stable selections, authenticated clan ownership, session/revision
> concurrency, exact replay results, canonical rollback, server-created parties, and an authenticated building-
> reserve command while clients retain the menu and roster-selection surface. Fourberie's forty-seven explicit
> operation families now use authenticated,
> rollback-safe server commands, while its menus and mission setup remain role-local presentation/lifecycle.
> Diplomacy's explicit player operations and server callbacks are routed too, including a persisted,
> controller-scoped server messenger queue with server-owned travel, arrival costs, and accident RNG.
> This is not a completed-suite claim: remaining Fourberie model/menu/mission dispositions, the exact
> Diplomacy ledger, Player Settlement construction, and other campaign-mod records still require closure.
> The previous
> ten-module archive remains an uninstalled historical RC and cannot be promoted. Stable also retains
> the rendered install/join and Sea Raider auto-resolve gates after functional closure. Foundation
> verification is green: 3,392 passed, 18 skipped, 0 failed; build completed with 0 errors.

> **2026-08-13 live Kingdom regression correction — replacement release pending (`bd425fa09`,
> `7fb31d007`).** The stable `315be775e` deployment did not fix the Kingdom tab. The 10:28 EDT client
> log proves `Diplomacy.Settings.Instance` was present, then `KingdomWarItemVMMixin` dereferenced a null
> `WarExhaustionManager.Instance`; the resulting native `GauntletKingdomScreen.OnFrameTick` null reference
> repeated 2,197 times and left the tab black. The matching server journal proves the client requested its
> Diplomacy snapshot from `CampaignReady` before `NetworkPlayerCampaignEntered` created its player mapping,
> so the server silently rejected the only request and never sent the manager/settings snapshot. `bd425fa09`
> authenticates that early request with the already accepted host-config session/revision/SHA instead of the
> not-yet-possible player mapping, retains the trusted snapshot, verifies the complete host MCM fingerprint and
> all four required manager shapes at `KingdomDiplomacyVM.RefreshValues`, repairs a lost singleton from that
> trusted snapshot, and blocks native row construction if the state is still unsafe. `7fb31d007` fixes a second
> real army divergence: the siege follower **Leave Army** consequence previously cleared only the client field;
> it now sends the authoritative army removal and converges the server plus every client before continuing.
> The pre-existing `44f6405f5` reserve-expansion and retreat-menu fixes remain in this candidate. Incremental
> gates are green with zero failures: 111 Diplomacy unit tests and 81 E2E tests spanning Diplomacy patching and
> commands, siege leave, army wait/create/destroy, mission-ready election, reserve building/reconnect,
> reinforcement spawn/quotas, retreat, and unstuck recovery; both affected test projects build with zero errors.
> Publication, paired server deployment, same-save reconciliation, and rendered Kingdom proof are not yet claimed.

> **2026-08-13 corrective release — SHIPPED from source `315be775e`.** The overnight
> Kingdom/Diplomacy, raid, encounter, and army fixes were re-audited against fresh client/server evidence
> instead of retained as symptom patches. The candidate consists of four verified commits:
> `9105c7cc8` restores Diplomacy client settings/UI lifecycle without fabricating internal managers;
> `435385a1f` routes enemy-capture completion through authenticated server validation and signals native
> battle presentation before synchronized graph teardown; `4434f7505` aggregates raid loot by item across
> modifier stacks before computing the net delta; and `44f6405f5` signals the initial host's expansion from
> own-party to full army reserves and closes the retreating player's stale encounter menu. Auburn's live
> `MapEvent_Created_70760` proved both army defects: the server sent 815-vs-2,736 reserves after initially
> assigning only the player's 81 troops, but no ownership-expansion transition reached the client; after the
> retreat the server removed the party and rejected two stale-menu mission-start retries.
>
> Local release gates are green: the unit/integration assemblies executed 2,491 tests with 14 intentional
> skips; the E2E assembly executed 1,412 tests with four intentional skips (the lone first-pass failure was a
> runner-relative XML fixture and passed once the repository fixture was exposed); Fourberie is 337/337,
> Separatism is 59/59, the launcher is 65/65, and the complete solution builds with zero errors. These changes
> are runtime-only and did not create, replace, or migrate a campaign save. GitHub run `31708916585` published
> stable client `2026.08.13.1416` (zip SHA-256 `1bc2bed1ebaa1370e9784de256f50357cdac1435ed5a03ceddb727a406c0365f`).
> The matching Serilog-2.x server build is paired to dedicated-core SHA-256
> `058c2646b5d5685305a7965cd3de984a7c2ae4918ae8281eaa4a55c7cf36c404`; every release-pin and deployment-ledger
> entry passed. The stopped-server save and JSON sidecar matched their pre-stop hashes, were copied byte-for-byte
> to `/home/bishop/bannerlord-coop/server/_mod_backups/pre-315be775e-20260813T141932Z`, and remained the configured
> `friendallmods1` files on restart. The host loaded that world, reached `SERVING` on UDP 4200, emitted repeated
> pulses, and reports `NRestarts=0` with no release-verification or unhandled-fatal marker. Rendered gameplay
> validation of the four corrected paths remains a playtest gate, not a deployment claim.

## Known playtest bugs (live)

- **[stable and paired server deployed; live playtest validation pending] Hideout boss-stage black screen and reconnect loop.**
  (2026-08-12, Bryce.) Live logs tied the loop to a player disconnect during the hideout: the server retained
  the hideout map event as reconnectable even though the mission/boss-stage state could not be resumed. Commit
  `2dfdf38dd` now requests authoritative finalization for that disconnected hideout event and parks the party
  after the existing finalization callback; ordinary field battles and sieges retain reconnect behavior.
  Hideout tests pass (7/7), and the map-event collection passes 8 with its existing single skip.
- **[stable and paired server deployed; live playtest validation pending] Fourberie prisoner and safehouse loot transfers did not persist.**
  The earlier nightly moved prisoner enslavement to the server but still performed its roster/loot/skill changes
  inside `AllowedThread`, which suppresses the publishers clients need. Its generic stash route also submitted
  transient inventory-screen rosters that have no stable network identity. Commit `6e5463200` keeps replication
  enabled for prisoner removal, generated loot and Roguery XP, and adds a bounded stable item/modifier transfer
  command between the authenticated party and canonical crime-base roster. Fourberie tests pass (337/337).
- **[stable and paired server deployed; live playtest validation pending] Send Troops skipped the loot/capture flow.**
  Map-event destruction was force-finishing an encounter already staged at `CaptureHeroes`, discarding the
  authoritative result rosters before the player could loot. Commit `cf20807cc` preserves that staged encounter
  while retaining cleanup for abandoned encounters. Battle-finalization tests pass (9/9); battle-result
  distribution tests pass (6/6).
- **[stable and paired server deployed; live playtest validation pending] Tournament participants did not receive skill progression.**
  Every accepted match result marked the entire session as having live hit progression, even when no hit packet
  awarded XP; one player's accepted hit also suppressed fallback progression for every other participant.
  Commit `f6cf93d06` records accepted live progression per controller and applies completion fallback only to
  participants without accepted hit XP, preventing both missing and double awards. Tournament tests pass (89/89).
  All four patches are runtime-only and do not create, migrate, replace, or require a new campaign save.
  PR #5 published them at merge commit `5f708bcfcd78442eb653717bdbf737d8b3846ef5`. Launcher stable
  version `2026.08.12.1816` serves the matching client payload (SHA-256
  `1cd55a823d6dd745ee1944be3b909b2626f9ee0fcd20e5bf89a22e9a6af958ca`). The paired Serilog-2.x
  dedicated-server build is live with core SHA-256
  `a6f99fed984156936d5871e48e1284208a02697205f6e69bf1cea486492adf45`; the complete deployment
  ledger passes. The host loaded the existing `friendallmods1` save, reached `SERVING` on UDP 4200,
  and has zero restarts or fatal startup markers. The pre/post save filename inventory is identical,
  and the live save matches the stopped-server backup byte-for-byte. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-5f708bcf-20260812T182209Z`.
- **[superseded by live 5f708bcf release] Upstream bugfix batch.**
  Nine compatible upstream nightly fixes (#2968, #2913, #2905, #2897, #2898, #2899, #2884,
  #2855, and #2768) shipped through launcher client version `2026.08.12.1517` and the matching
  Serilog-2.x dedicated-server build. Server source release `d90c6a020` is paired to core SHA-256
  `c966123121f21dc86b68fccef92ab0c5c576493052ee84aaf489f2bfd80aebc4`; every deployment-ledger
  entry passed after restart. The host loaded the existing `friendallmods1` save and reached
  `SERVING` on UDP 4200 with zero restarts and no fatal startup markers. No save was created or
  migrated. Rollback snapshot:
  `/home/bishop/bannerlord-coop/server/_mod_backups/pre-be838ba29-20260812T153007Z`.
- **[candidate fixed; live validation pending] Auto-resolve vs bandit party loops the encounter menu.**
  (2026-08-11, Bryce, Sea Raiders.)
  Choosing **"Send your troops to attack"** (auto-resolve) instead of **"Attack!"** (manual) against a
  bandit party leaves the encounter menu looping until you pay off / surrender. Mechanism (from
  `Coop_client.log` MapEvent_Created_1155): the auto-resolve is server-gated (`BattleSimulationStartPatch`
  → `BattleStartCoordinator.RequestBlocking(Simulation)`) and accepted, but the client-paced simulation's
  authoritative writes are blocked — `AutoSync … Client updated managed MapEvent.IsPlayerSimulation`, then
  `DestroyPartyActionPatch … Client attempted to apply DestroyPartyAction for party Sea Raiders`. The
  beaten party is never destroyed/synced, so the encounter re-evaluates as "enemy present" and re-opens the
  menu; pay-off/surrender are separate terminal paths, which is why paying escaped it.
  **Candidate fix:** the server-authoritative completion boundary now always closes client playback,
  publishes one conclusion for a decided battle, and releases the arbiter claim for an undecided result.
  Focused completion and related map-event tests pass. A rendered client must still reproduce the original
  Sea Raider path before this can be called live-fixed.
  **Current-live workaround:** use **"Attack!"** (manual battle) until the candidate is rendered and promoted.

## Where we are (2026-08-12)

Modded co-op **joins and loads**: server hosts the full campaign, validation +
mod-config barrier + 34 MB save transfer succeed, and the client reaches a playable **world map**
with all four campaign mods (Diplomacy, ImprovedGarrisons, Fourberie,
PlayerSettlement) active. RBM is retired from the exact loadout after its native campaign-init
crash. Map navigation now suppresses only the audited transient null-readiness path and propagates
unrelated faults. The previous ten-module release candidate is packaged and hash-verified but is now
superseded by the authority-routing work; it is not eligible for stable promotion.

### Superseded non-stable release candidate (2026-08-11)

- Commit: `6e3aaa7d9df629bef8c3fdfd4ba93cb4c01bc5db`.
- Archive: `work/release-candidate/BannerlordCoop-FriendEdition-2026-08-11-6e3aaa7d9-RC.zip`
  (776,037,986 bytes; SHA-256 `9e355ef696872f9477faa7c255503cec517eb691bda7bc1bb4506afc8f7d7bc0`).
- Package proof: 10 Workshop modules plus Coop, 845 verified files, 404 DLL payloads inspected,
  249 active managed assemblies, 53 closure proofs, 8 approved strong-name side-by-side cases, and
  zero unresolved same-identity duplicates. The managed-client dry run validates the exact 16-module
  activation order with RBM absent.
- Source provenance: the original Steam snapshots had been deleted, so the builder used the retained
  already-sanitized module trees only after all ten matched the historical audited receipt on Workshop
  ID, Steam manifest ID, content hash, and configuration hash. The production manifest remains strict;
  the temporary input manifest relaxed only the already-consumed exclusion counts for DismembermentPlus
  and PlayerSettlement. See the adjacent `-PROVENANCE.json` receipt.
- Full local gate: Common 101/0, CrashReporter 3/0, Coop.Tests 613 passed + 1 skipped,
  GameInterface 1,153 + 11 skipped, Integration 146 + 2 skipped, E2E 1,367 + 4 skipped;
  3,383 passed, 18 skipped, 0 failed overall. Build, release safety, XAML, launcher (16),
  server-kit (6), packaging, function-inventory, and native hook gates are green.
- Publication state: the superseded RC remains unpromoted. The save-compatible `03df14dd0` bugfix is live
  on the nightly launcher feed and paired dedicated server; stable remains held for rendered validation.

## Decisions (locked)

1. **Compatibility checks:** keep the byte-exact Workshop hash validation (clear
   "reinstall" errors); remove ONLY the base-game build-version gate (server modules
   117131 vs client 117484).
2. **Packaging:** keep **two thin before-Native framework modules** (Harmony +
   ButterLib, Frontir-forked from source), a **launcher hides the folder list**.
   Campaign mods stay **unmodified Steam bytes** (hash-verified handshake preserved,
   zero per-update maintenance). Non-critical adapters/data fold into `Coop`.
3. **No forking the campaign mods** to save folders — that's the "rewrite every time"
   treadmill we're avoiding. Fork a mod only if we deliberately choose to own it.

## Current program order (2026-08-11)
1. Complete and classify every remaining active mod authority route.
2. Pass the exact-method authority audit in release mode with no blocked/unclassified active candidates.
3. Run rendered all-option client/server coverage, then the existing install/join and Sea Raider checks.
4. Build a fresh ten-module candidate; stable promotion remains manual.

### Fourberie create-action boundary (ROUTED 2026-08-11)
The original create routines still accept only an `int` and select process-global player state, so Coop
does not replay them. Four authenticated, revision-checked commands now carry stable settlement, hero,
destination, and troop selections into explicit server implementations for both agent-enlistment paths,
bandit recruitment, and insurance-scam spawning. Each operation validates the controller's current party,
rolls back canonical Fourberie state and created parties on failure, and returns an exact replay result.

### Mod integration progress (2026-08-11)
- **Diplomacy** — explicit donate/fief/messenger/peace/war/alliance/pact commands and keep-fief callbacks
  are authenticated server operations. Messenger dispatch now commits a controller-scoped server queue;
  the server owns persistence, travel timing, arrival expenses, wanderer activation, accident RNG, and
  reconnect prompts while the client owns only inquiries and dialogue presentation. Exact classification
  of the remaining Diplomacy ledger is still open.
- **ImprovedGarrisons** ✅ all 723 authority candidates are exact-classified. Every management/settings
  consequence is routed to the server with authenticated clan ownership and stable IDs; party creation,
  recruiter/mobile orders, templates, culture, roster setup, hostile encounters, and building reserves now
  have live routes.
  Canonical state changes are revision-checked, exact-replay-safe, rollback-verified, and republished;
  clients retain presentation and see only their clan's valid garrison targets.
- **PlayerSettlement** ⚠️ loads; construction/rebuild/overwrite and persistence graph remain open.
- **UnblockableThrust** ✅ all 4 authority candidates are exact-classified under Coop's accepted
  collision owner; incompatible, swing, shield, parry, chamber, remote, foot, and mounted cases are gated.
- **DismembermentPlus** ✅ all 17 authority candidates are exact-classified. The victim-authority peer
  validates the accepted blow once and broadcasts a canonical limb/seed event; peers apply only the
  visual routine, with battle-scoped dedupe, capability gating, and no second `RegisterBlow`.
- **Separatism** ✅ all 57 authority candidates are exact-classified. The recovered fallen-clan option
  is restored as capability-gated client presentation; the server re-derives the authenticated ruler,
  validates stable target IDs plus kingdom/membership revision, replays exact duplicate results, and
  rolls back failed clan moves. Compatibility policy boundaries are directly tested, and configured
  settlement rebellion is forced off on clients.
- **Fourberie** — integrated in increments:
  1. Coop owns Fourberie's behavior/model initialization and requires its exact eight-behavior,
     fourteen-model composition. `FourberieRuntimeSurface` enumerates the active model manager directly,
     including mission-scoped models that are not exposed as public `CampaignModels` properties.
  2. `RegisterEvents` un-gated (runs on both) so behaviors wire client menus.
  3. Menu builders (`AddGameMenus`/`*OnGaMenOpened`/contact/escape/spawn) → `ClientPresentation`.
  → Menus + simple actions confirmed working live.
  4. Both agent-enlistment paths, bandit recruitment, and insurance-scam spawning bypass the unsafe
     `static void M(int)` entry points and use typed, rollback-safe server commands.
  5. Criminal-enterprise start, upgrade, and downgrade actions now send only a typed business key. The
     server re-derives the pinned cost/limit, owns the gold and dictionary transaction, rolls back both on
     failure, and republishes canonical state; the open client view refreshes after snapshot application.
  6. Both scheme-bonus controls now route raise/lower/reset through the same command channel. The server
     resolves the pinned victim and kingdom, derives the controller-specific network limit, applies the
     exact coverage rule, and republishes the result instead of letting the open client write scheme state.
  7. Saboteur-party create, refill, and disband now use authenticated server transactions. Location,
     capacity, trained-agent availability, and the current party are re-derived server-side; creation and
     refill have roster rollback, while an irreversible disband failure aborts instead of claiming recovery.
  8. The crime-base “all lads leave” confirmation is server-owned and location-checked. Its destructive
     party replacement is explicit about its irreversible boundary and aborts on an incomplete rebuild.
  9. Paymaster/enforcer assignment and removal now use typed role commands. The server re-resolves the
     selected hero, verifies party/clan membership and role eligibility, and owns the shared role mapping;
     client view refresh/close paths preserve those canonical role keys.
  10. Both scheme slots now route victim/type selection plus start, abort, completed-result clearing, and
      scheme-room stance changes through typed commands. The server revalidates target rules, network/war
      state, affordability and agent
      pools, and owns duration/outcome RNG, gold, campaign time, and canonical lifecycle state. Twelve
      client-only kingdom/clan/intelligence filters now preserve both canonical slots while retaining their
      local navigation selections.
  11. Corruption level, criminal-enterprise auto-investment, and both hideout duty sliders now use bounded
      typed commands. The server owns keys 5, 61, 1000, and 1001; client presentation reads cannot create or
      overwrite the missing auto-investment key while rendering the room.
  12. Contract-offer enable/disable and current-contract cancellation now use typed commands. Cancellation
      revalidates both canonical participants, applies the relation penalty to the authenticated controller,
      and owns the cooldown RNG and contract-state cleanup on the server. The broken headless inquiry in the
      hourly proposal tick is replaced by server-owned giver/target/reward selection plus a targeted client
      prompt; accepting or declining that prompt is a replay-safe server command, and reconnecting owners are
      re-offered a still-pending canonical proposal.
  13. Selecting a territory as the main crime base now sends its stable settlement ID to the server. The
      server verifies it is a current town territory, owns the base/timestamp change, and clears base-specific
      criminal roles before publishing the new canonical state. Removing a secondary territory and confirmed
      abandonment of the current town base use the same route; the server owns the full business/scheme cleanup
      and the client closes back to the town menu only after acceptance. Confirmed safehouse abandonment is also
      server-owned: the server validates the controller is still at the pinned non-town base, applies the slave
      strength/relation effect, and clears the safehouse/base/scheme state before the client closes the view.
      Clan-grudge settlement now uses a server-generated quote followed by a separately confirmed transaction;
      the server owns the random surcharge and revalidates the clan, live grudge, spy, gold, and quoted amount.
      All four crooked-trader choices now send only the current safehouse identity; the server revalidates the
      pinned non-town base and cooldown, derives slave quantity and payout, and owns gold and canonical state.
      Safehouse establishment is also server-owned: hideout location, bandit relation, existing base/party state,
      default duties, relic RNG, and retained-party reuse or creation are revalidated before the client opens its management view.
      Safehouse wait start/stop is server-owned too: the pinned base is revalidated before visibility, follower AI,
      and the canonical wait marker change; stopping also releases the original mod's permanently frozen follower AI.
      Safehouse mission return now clears its replicated marker through the server before performing the local
      encounter transition, preventing snapshots from repeatedly reopening a finished safehouse encounter.
      Safehouse mission-start geometry now runs on clients, so locally rendered upgrade entities are hidden as intended;
      mission-end marker changes remain server-owned.
      The safehouse location-character spawn call tree is now explicitly client mission lifecycle: it reads the
      replicated roster/state and mutates only transient spawn lists and local location characters.
      Fourberie's destructive `DataDeleteEx` teardown is now actually patched server-only; each peer can still run
      its harmless `OnGameEnd` screen cleanup without a client clearing campaign state or destroying parties.
      Outer startup hooks remain per-peer for local troop caches and module flags, while behavior/model registration
      continues through the exact audited Coop replacement.
      The initialization replacement now rebuilds replicated hero dictionaries only on the server; clients consume
      the canonical snapshot instead of independently rewriting those dictionaries.
      Safehouse prisoner enslavement now sends the selected prisoner roster to a server-owned operation; the
      server validates the pinned safehouse and current roster, then owns prisoner removal, generated loot,
      slave strength, and Roguery XP with Coop's mutation publishers enabled. Safehouse item transfers use a
      dedicated stable item/modifier command because the inventory screen exposes transient rosters that the
      generic trade route cannot identify. Both changes preserve the current campaign save format.
  14. `OnMissionBehaviorInitialize` currently preserves the mod's required peer-local setup, but its
     mission callbacks remain open until their authoritative/controller ownership is proven end to end.
  15. The exact secondary pass currently assigns metadata to 1,044/1,865 required candidates:
     427 presentation-only helpers, 222 pure/read-only policy methods, 186 server callbacks, 53 Coop-owner
     replacements, 59 server-command methods, 88 framework-lifecycle methods, and 9 unreachable
     Homesteads/Bellum Civile add-on methods.
     The strict gameplay gate also rejects campaign mutation, canonical Fourberie-state writes, and
     authority-sensitive calls mislabeled as client presentation. That gate currently passes 971 and
     rejects 894: 821 unclassified methods plus 73 unsafe presentation classifications.
  - **OPEN:** 894 exact UI/mission/lifecycle routes remain, including roughly 300 menu/dialog helpers
    that reach shared state and therefore require live command or mission-authority owners. This strict
    count, not the lower metadata-only count, is the completion baseline for subsequent increments.

### Playable session (DONE 2026-08-10)
- [x] Map-nav NRE fixed and narrowed (`MapNavigationReadinessPatches` suppresses only the transient
      null-readiness path; unrelated exceptions propagate).
- [x] Diplomacy client init (`DiplomacyClientInitializationPatch` — create DiplomacyEvents
      before the map builds; resolve the type at runtime, NOT a static field, because Coop
      loads before Diplomacy).
- [x] RBM dropped (native crash in co-op campaign-init; stays out of the loadout).
- [x] Client reaches a playable map with Diplomacy/IG/PS/Dismember/UnblockableThrust active.
- Client GameInterface builds against Serilog **4.x** (flip Common.csproj to 4.2.0, build,
  restore to 2.12.0). Deploy to `…/Modules/Coop/bin/Win64_Shipping_Client/GameInterface.dll`.

### P1 — Playable session (source fixes complete; rendered candidate proof open)
- [x] Narrow the map-nav UI NRE boundary to the known null-readiness failure.
- [x] Drop RBM from server token + client token + catalog expectation + pack. The exact ten-module
      contract is now enforced across catalog, manifest, launcher, and both peer-role orders.
- [x] Keep exact Workshop/module validation without imposing a base-game build-version gate.
- [x] Build and independently validate a fresh RBM-free ten-module release-candidate pack.
- [ ] Verify: client reaches map AND is playable (HUD, move, open menus) with the
      four campaign mods, then reproduce the fixed Sea Raider auto-resolve path.

### P2 — Launcher (one-click into grain.silo) — source certified; rendered auto-join open
- [x] **`/coopjoin <host> <port> [pw]` boot arg** in `CoopMod.cs`: at `InitialState` the client
      publishes the same `AttemptJoin` the Join button does → auto-connects. Guarded off on the
      server / managed-host paths. Client `Coop.dll` rebuilt (Serilog 4.x) + deployed to
      `mb2\Modules\Coop\bin\Win64_Shipping_Client`.
- [x] **Frontir "Calradia Co-op" launcher** (`tools/CoopLauncher`, WPF net8.0-windows):
      Bannerlord-themed (hanging war-banner signature; sigil = live host status), one-click
      "March to War" runs `Bannerlord.exe /singleplayer <token> /coopjoin 205.209.116.114 4200 <private-password>`.
      Steam auto-detect for the game path; UDP host probe; self-contained single-file publish
      (`CalradiaCoop.exe`, no .NET install for friends). Replaces `Play Friend Edition.cmd`.
- [x] Module token lives in `launcher-config.json` (edit, no rebuild); the loadout is the full
      set minus RBM, in handshake order.
- [x] **Self-update feed is wired:** `ModUpdater` pulls the SHA-256-verified client zip from the
      rolling `client-stable` manifest. Required updates use an exact SHA-256, same-volume staging,
      exact replacement, rollback, and zip-traversal defense; failure keeps Join disabled.
- [x] **Hover-crash fixed (2026-08-10, confirmed by Bryce).** `WarButton` hover trigger referenced an
      undefined `BloodBright` brush → render-time `UnsetValue` crash (`0xe0434352`) that killed the
      process on first hover and left a ghost window (clicks did nothing, no `launcher.log`). Fix: add
      the brush + a fail-fast `DispatcherUnhandledException` reporter + `check-xaml-resources.py` guard
      (commit `b2e5f8294`). See `doc/COOP-OPS-WORKFLOW.md` rule #10. Button now launches the game.
- [x] **Credential/release containment (2026-08-11).** Rotated the live server password; kept it only
      in the server launch script and Bryce's private pinned launcher config; replaced all three
      public `launcher-app` assets with password-free builds; made launcher-app publication manual;
      and limited automatic client publication to nightly builds from `development`.
- [x] **Reached-feed failures now fail closed (2026-08-11).** The launcher distinguishes a real
      transport outage from an HTTP error returned by the configured update feed. Missing, forbidden,
      or failed manifests keep **MARCH TO WAR** disabled instead of being mislabeled as offline; the
      self-contained publish, XAML guard, and all 17 launcher tests pass.
- [x] **Transient scanner locks no longer abort client updates (2026-08-12).** Exact-replacement
      directory moves retry access-denied, sharing-violation, and lock-violation failures for a bounded
      five-second window before using the existing rollback path. A regression test holds a freshly
      extracted staged DLL open and proves the verified payload installs after the lock is released.
- [x] **Post-update launch button re-enables (2026-08-12).** Successful preparation now clears the
      operation guard before rendering the final current snapshot, so **MARCH TO WAR** is usable in
      the same window. The WPF regression asserts the real button state, and updater failure tests use
      an injected log sink instead of writing simulated failures into the member's launcher log.
- [x] **Nightly test composition repaired (2026-08-11).** The shared Coop.Tests client/server
      container now registers the same real Workshop capability registry supplied by the production
      GameInterface module; all 614 Coop.Tests cases build their containers again (613 pass, 1 skipped).
- [ ] Live test (still open): confirm the launched game auto-joins grain.silo end-to-end with mods.

### Dedicated server release integrity — LIVE VERIFIED (2026-08-11)
- [x] ButterLib and dedicated-loader transforms pin exact inputs and exact method signatures/counts.
- [x] The release-pairing transform pins the final four Coop DLLs and restores the code-4 boot abort.
- [x] Both physical server core locations, `coophook.dll`, and `SERVER-COOP-PAIRING.json` are deployed.
- [x] Boot logs report `Coop module verified against release pins` before `phase":"serving"`; UDP
      4200 is bound on IPv4/IPv6, `NRestarts=0`, and every deployment-ledger entry verifies.

### P3 — Packaging consolidation
- [ ] Fold the co-op adapters + non-framework mod data into `Modules/Coop` where safe.
- [ ] Keep Harmony + ButterLib as their own before-Native modules (launcher-hidden).
- [ ] `Frontir.ButterLib` source fork (keeps `Bannerlord.ButterLib` module ID) —
      replaces the server-side Cecil binary-patching with a clean compiled build;
      targets .NET Core (server) + Framework (client) from one source.

### P4 — Coop mod-integration SDK (the reusable pipeline)
- [ ] Extract the `WorkshopMods` adapter pattern into a documented framework:
      a base `CoopModAdapter` + attributes for (guard mod patches, route authoritative
      actions, register net messages, declare E2E fixtures).
- [ ] Goal: wire a NEW mod into co-op networking by writing one adapter class, not a
      hand-port. Document with one worked example (e.g. Diplomacy Donate Gold).

### P6 — Upstream nightly sync — LIVE DEPLOYED (2026-08-12)
- [x] Audited upstream nightly PRs after the Friend Edition base and selectively backported
      nine final reviewed bugfixes: #2968, #2913, #2905, #2897, #2898, #2899, #2884,
      #2855, and #2768. Bannerlord remains pinned to `v1.4.7`; upstream project/module
      identity changes were not imported.
- [x] Added or imported regressions for party-screen inventory reset, trade-gold coalescing,
      troop-roster XP normalization, player-garrison protection, local settlement visibility,
      escort following, chat behavior/settings, invalid map-event parties, and clan-party
      disband safety. Combined local gate: Release build 0 errors, 45 focused GameInterface
      tests passed, and 5 isolated E2E tests passed.
- [x] Existing-save boundary preserved: this batch adds no save field or migration, creates no
      save, and performs no live-server deployment. #2903 remains deferred specifically because
      it adds persisted session data.
- [x] Launcher nightly published from `development` commit `d90c6a020` by GitHub run
      `31611057545`: safety, full unit/integration tests, Serilog 4.x client build, package, and
      publish all passed. The rolling `client-nightly` manifest is version `2026.08.12.1517`
      with SHA-256 `200226f389a847a65e2dd470d29373519d316014e2bef3705c4181025fefb286`.
- [x] Matching Serilog 2.x server build deployed in place without changing the save identity.
      `friendallmods1` loaded successfully; release-pin verification, the complete deployment
      ledger, UDP 4200, and repeated server pulses are the live acceptance gates.
- Deferred for separate reconciliation: #2931/#2912 overlap settlement authority work;
  #2941/#2773/#2863 overlap existing Friend Edition backports; #2867 overlaps custom auto-resolve;
  feature/content PRs and upstream 1.4.8/nightly-identity changes remain out of scope.

### P5 — Serilog hardening
- [ ] Audit every bundled assembly; pin each to the Serilog its runtime needs
      (server .NET Core = 2.x, client Framework = 4.x); bake into the build so it
      can't regress.

## Serilog / runtime rules (carry-forward)
- Server GameInterface = Serilog 2.x build; Client GameInterface = Serilog 4.x build.
  A 2.x DLL on the client throws `MissingMethodException` on `LogManager.GetLogger()`.
- Never leave backups inside a hashed module dir (breaks content hash). Use
  `server/_mod_backups/`.
- Verify BOTH client and server for any handshake-affecting change.
