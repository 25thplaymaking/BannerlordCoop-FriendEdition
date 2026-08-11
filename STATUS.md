# Frontir Coop — Program Status

Living board for the modded co-op productization. Update at each milestone.
Companion docs: `doc/COOP-MOD-INTEGRATION.md` (how the port works),
`doc/COOP-OPS-WORKFLOW.md` (ops rules + checklist).

> **EXECUTION ACTIVE (2026-08-11): bounded repairs complete; final release gates in progress.** The
> inherited launcher/auto-resolve work and the approved containment/certification repairs are pushed
> through `ae5ef9d04` on `25vid/workshop-integration`. Public launcher defaults/assets contain no join password; the live
> password was rotated into only the server launch script and Bryce's private pinned config. Stable
> releases are manual and development pushes are nightly-only. The all-functions review now covers
> 41,000 metadata methods across the ten active Workshop modules, retired RBM, and integrated Separatism; its exact-hash
> ledger and ownership decisions live in `doc/WorkshopFunctionReview.md`. Separatism is certified
> across 5 unit, 17 synchronized E2E, 92 Diplomacy-collision, and 8 config-authority cases.
> Fourberie's unsafe contextless create routes now fail closed, behavior initialization preflights
> atomically, and the full GameInterface baseline is green (1,146 passed, 11 skipped, 0 failed).
> Improved Garrisons recruit/upgrade/capture/save authority and the active combat-mod boundaries are
> now covered by 44 focused IG tests and 70 combat tests. Dismemberment remains intentionally disabled
> in live Coop; Unblockable Thrust's shield/parry/chamber and foot/mounted defaults are certified.
> Launcher updates are transactional and SHA-required; auto-resolve completion, UDP probing, and
> narrow map readiness are covered. The dedicated server now verifies the exact four-assembly Coop
> pair and fails closed; the live host reached `serving`, bound UDP 4200 on IPv4/IPv6, and its complete
> deployment ledger verifies. Remaining work is the full CI/package gate and rendered-client release proof.

## Known playtest bugs (live)

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
  **Current-live workaround:** use **"Attack!"** (manual battle) until the candidate is packaged and verified.

## Where we are (2026-08-11)

Modded co-op **joins and loads**: server hosts the full campaign, validation +
mod-config barrier + 34 MB save transfer succeed, and the client reaches a playable **world map**
with all four campaign mods (Diplomacy, ImprovedGarrisons, Fourberie,
PlayerSettlement) active. RBM is retired from the exact loadout after its native campaign-init
crash. Map navigation now suppresses only the audited transient null-readiness path and propagates
unrelated faults. The current ten-module candidate still needs a fresh package and rendered join proof.

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

## Program order (RENEGOTIATED 2026-08-10 — PS construction moved LAST)
1. **Mods integrated** — Diplomacy ✅, ImprovedGarrisons ✅, Fourberie menus/guarded behaviors ✅.
   Fourberie's contextless saboteur/bandit/scam create-actions intentionally fail closed.
   PlayerSettlement loads; CONSTRUCTION deferred.
2. **Launcher** (NOW) — self-updating, one-click `/coopjoin` into grain.silo, module list hidden.
3. **Discord bot** — build/update alerts to Bryce's Discord (+ server up/down).
4. **GitHub nightly sync** — merge upstream BannerlordCoop fixes.
5. **Custom create-settlement (LAST)** — build OUR OWN lightweight settlement creation instead of
   PlayerSettlement's save+reload flow: create the Settlement MBObject at runtime + sync via Coop's
   Settlement create funnel, gated by a player VOTE-TO-PAUSE. Deferred because it needs multiple
   live testers (Bryce has none today). PS's own BuildTown/Overwrite/Rebuild stay blocked.

### Fourberie create-action boundary (CERTIFIED 2026-08-11)
The audited create routines accept only an `int` and internally select the process-global
`MainHero`, `MainParty`, and `_agentsParty`. Authenticating a request's peer does not pass that
player context into the original routine, so replaying it on the server targets the wrong player.
All three routes now fail closed on both roles, including legacy network requests. Building a new
explicit-context/per-player Fourberie API is outside this bounded stabilization pass.

### Mod integration progress (2026-08-10)
- **Diplomacy** ✅ working (DiplomacyEvents client init).
- **ImprovedGarrisons** ✅ server-authoritative (works, mostly background).
- **PlayerSettlement** ⚠️ loads; new-settlement construction still blocked.
- **Fourberie** — integrated in increments:
  1. `BehaviorsWithoutModels` kind: `InitializeBehaviorsOnlyPrefix` adds the 8 gameplay
     behaviors, skips the 14 model replacements; `FourberieRuntimeSurface` now rejects only
     active MODELS, not behaviors.
  2. `RegisterEvents` un-gated (runs on both) so behaviors wire client menus.
  3. Menu builders (`AddGameMenus`/`*OnGaMenOpened`/contact/escape/spawn) → `ClientPresentation`.
  → Menus + simple actions confirmed working live.
  - **CLOSED/FAIL-CLOSED:** actions that create authoritative parties/rosters cannot safely carry
    the authenticated player's context through Fourberie's `static void M(int)` APIs. Saboteur
    enlistment, bandit recruitment, and scam spawning are blocked; client-side object creation and
    host-singleton replay are both prevented. `OnMissionBehaviorInitialize` also stays blocked.

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
- [ ] Verify: client reaches map AND is playable (HUD, move, open menus) with the
      four campaign mods. Re-cut the pack.

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

### P6 — Upstream nightly sync (requested 2026-08-10)
- [ ] Pull the main/nightly BannerlordCoop fixes into this fork. Fork has diverged
      heavily (the whole `WorkshopMods` system) — do a careful merge, not a blind pull:
      identify upstream since our base, apply non-conflicting fixes, re-run CI, re-verify
      the modded join still works. Do AFTER P1 (playable session) is locked.

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
