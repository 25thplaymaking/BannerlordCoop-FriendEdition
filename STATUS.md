# Frontir Coop — Program Status

Living board for the modded co-op productization. Update at each milestone.
Companion docs: `doc/COOP-MOD-INTEGRATION.md` (how the port works),
`doc/COOP-OPS-WORKFLOW.md` (ops rules + checklist).

> **EXECUTION ACTIVE (2026-08-11): containment complete; certification in progress.** The inherited
> launcher and auto-resolve commits plus containment fixes are pushed through `bdfe4138b` on
> `25vid/workshop-integration`. Public launcher defaults/assets contain no join password; the live
> password was rotated into only the server launch script and Bryce's private pinned config. Stable
> releases are manual and development pushes are nightly-only. The larger all-functions review,
> Separatism certification, and full live/release gate remain open below.

## Known playtest bugs (live)

- **[open] Auto-resolve vs bandit party loops the encounter menu.** (2026-08-11, Bryce, Sea Raiders.)
  Choosing **"Send your troops to attack"** (auto-resolve) instead of **"Attack!"** (manual) against a
  bandit party leaves the encounter menu looping until you pay off / surrender. Mechanism (from
  `Coop_client.log` MapEvent_Created_1155): the auto-resolve is server-gated (`BattleSimulationStartPatch`
  → `BattleStartCoordinator.RequestBlocking(Simulation)`) and accepted, but the client-paced simulation's
  authoritative writes are blocked — `AutoSync … Client updated managed MapEvent.IsPlayerSimulation`, then
  `DestroyPartyActionPatch … Client attempted to apply DestroyPartyAction for party Sea Raiders`. The
  beaten party is never destroyed/synced, so the encounter re-evaluates as "enemy present" and re-opens the
  menu; pay-off/surrender are separate terminal paths, which is why paying escaped it.
  **Fix direction:** apply the auto-resolve OUTCOME authoritatively — server (or an authoritative client
  replay window) must run the loser `DestroyPartyAction` + event resolution and broadcast, the same way the
  manual battle path resolves cleanly (`MapEvent_Created_897/905` closed with no loop). Needs a focused pass
  + **live co-op verification** before shipping to the feed.
  **Workaround for now:** fight bandit encounters with **"Attack!"** (manual battle) — it syncs and resolves
  cleanly; avoid "Send your troops to attack".

## Where we are (2026-08-10)

Modded co-op **joins and loads**: server hosts the full campaign, validation +
mod-config barrier + 34 MB save transfer succeed, client reaches the **world map**
with all four campaign mods (Diplomacy, ImprovedGarrisons, Fourberie,
PlayerSettlement) active. Two known issues remain before it's *playable*:

- **RBM** — native `0xc0000005` during co-op campaign-init (its combat-param data).
  DECISION: **drop RBM from the loadout.** (It's already a `SubModuleOnly` stub
  server-side; not worth stubbing client-side for a combat-param mod.)
- **Map-nav UI NRE** — on the map, `MapNavigationHelper.IsNavigationBarEnabled(handler)`
  NREs every tick (null `MapNavigationHandler` on the client map state), so the HUD
  never renders and the player is frozen. Coop's robustness patches catch it but the
  session is unplayable. THIS is the immediate unblock.

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
1. **Mods integrated** — Diplomacy ✅, ImprovedGarrisons ✅, Fourberie ✅ (menus + create-actions
   routed: saboteur/bandit recruit + scam spawn). PlayerSettlement loads; CONSTRUCTION deferred.
2. **Launcher** (NOW) — self-updating, one-click `/coopjoin` into grain.silo, module list hidden.
3. **Discord bot** — build/update alerts to Bryce's Discord (+ server up/down).
4. **GitHub nightly sync** — merge upstream BannerlordCoop fixes.
5. **Custom create-settlement (LAST)** — build OUR OWN lightweight settlement creation instead of
   PlayerSettlement's save+reload flow: create the Settlement MBObject at runtime + sync via Coop's
   Settlement create funnel, gated by a player VOTE-TO-PAUSE. Deferred because it needs multiple
   live testers (Bryce has none today). PS's own BuildTown/Overwrite/Rebuild stay blocked.

### Fourberie fork/own — action routing (DECIDED 2026-08-10: fork & own it)
Approach: **own Fourberie's co-op behavior via deep GameInterface Harmony adapters** — the
Fourberie DLL stays byte-unmodified (handshake intact); we intercept + reimplement its
party-creating actions server-authoritatively and per-player. NOT a decompile-fork.
Root problem (confirmed in the decompile): `FourberieBehavior._agentsParty` is a `static`
`MobileParty` created **on the client** via `CreateVirtualParty("fb_saboteurs_party",…)`, and
every action targets `MainParty`/`MainHero`. Client-side party creation floods the object sync
(`Failed to get TroopRoster using Created_####`).
Plan (each = Diplomacy-pattern route: client intent → server apply for the requesting player):
- [ ] Per-player party registry: replace the single static `_agentsParty` with a
      player→party map; parties created on the SERVER through the MobileParty funnel
      (`NetworkCreateParty`) so `TroopRoster`s auto-register + broadcast.
- [x] Route `FourberieBehavior.AgentsEnlistRoutine(int)` (saboteur recruiting). DONE via generic
      `RoutedCreateAction` kind + `FourberieRecruit{Messages,Interface,Handler}.cs` (compiles, staged).
- [x] Route `FourbBanditBehavior.FourbRecruitBandit(int)` + `HelperSubInsuScam.SpawnBandits(int)`
      (same generic routed mechanism; all 3 static `void M(int)` player create-actions covered).
- [ ] Client-UI link: set client `_agentsParty` from the synced server party (display refinement;
      no crash without it — server null-guard prevents duplicate parties).
- [ ] Route bandit-party TICK spawns are already server-gated (ServerTick) — no action needed.
- [ ] Route safe-house crew / fight-club fighter spawns + contract rewards.
- [ ] Ownership validation server-side (peer→hero) like `DiplomacyDonateGoldHandler`.
Reuse (already exists — do NOT rebuild): auto-registry create broadcast
(`AutoRegistryHandler<TroopRoster>` / `NetworkCreateInstance`), `MobileParty` lifetime
(`NetworkCreateParty`), `TroopRosterDeltaHandler`, `ServiceModule` auto-DI for
`IHandler`/`IGameAbstraction`. Template: `WorkshopMods/Diplomacy/DiplomacyDonateGold*`.
This is the biggest remaining phase (Bryce: "route them all, I'll test afterward").

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
  - **OPEN:** actions that CREATE authoritative objects (TroopRosters — recruiting/spawning)
    hit the Coop object-sync boundary: `AutoSync … Client updated MBObjectBase.IsReady` +
    `[ObjectManager] Failed to get TroopRoster using Created_####`. Needs per-action routing
    to the server. This is a Coop-framework limit (affects any object-creating action), not
    Fourberie-specific. `Fourberie.Main.OnApplicationTick`/`OnMissionBehaviorInitialize` stay
    blocked (direct MainHero/Mission mutation).

### Playable session (DONE 2026-08-10)
- [x] Map-nav NRE fixed (`MapNavigationReadinessPatches` — swallow the transient
      map-load throw so the tick completes; self-heals when the map wires up).
- [x] Diplomacy client init (`DiplomacyClientInitializationPatch` — create DiplomacyEvents
      before the map builds; resolve the type at runtime, NOT a static field, because Coop
      loads before Diplomacy).
- [x] RBM dropped (native crash in co-op campaign-init; stays out of the loadout).
- [x] Client reaches a playable map with Diplomacy/IG/PS/Dismember/UnblockableThrust active.
- Client GameInterface builds against Serilog **4.x** (flip Common.csproj to 4.2.0, build,
  restore to 2.12.0). Deploy to `…/Modules/Coop/bin/Win64_Shipping_Client/GameInterface.dll`.

### P1 — Playable session (was IN PROGRESS)
- [ ] Fix map-nav UI NRE (null `MapNavigationHandler` / `HandleIfBlockerStatesDisabled`
      on the co-op client map). Proper init preferred over catch-and-limp.
- [ ] Drop RBM from server token + client token + catalog expectation + pack.
- [ ] Remove the base-game build-version check (keep hash validation).
- [ ] Verify: client reaches map AND is playable (HUD, move, open menus) with the
      four campaign mods. Re-cut the pack.

### P2 — Launcher (one-click into grain.silo) — DONE (2026-08-10, testing deferred)
- [x] **`/coopjoin <host> <port> [pw]` boot arg** in `CoopMod.cs`: at `InitialState` the client
      publishes the same `AttemptJoin` the Join button does → auto-connects. Guarded off on the
      server / managed-host paths. Client `Coop.dll` rebuilt (Serilog 4.x) + deployed to
      `mb2\Modules\Coop\bin\Win64_Shipping_Client`.
- [x] **Frontir "Calradia Co-op" launcher** (`tools/CoopLauncher`, WPF net8.0-windows):
      Bannerlord-themed (hanging war-banner signature; sigil = live host status), one-click
      "March to War" runs `Bannerlord.exe /singleplayer <token> /coopjoin 205.209.116.114 4200 <private-password>`.
      Steam auto-detect for the game path; TCP host probe; self-contained single-file publish
      (`CalradiaCoop.exe`, no .NET install for friends). Replaces `Play Friend Edition.cmd`.
- [x] Module token lives in `launcher-config.json` (edit, no rebuild); the loadout is the full
      set minus RBM, in handshake order.
- [x] **Self-update feed is wired:** `ModUpdater` pulls the SHA-256-verified client zip from the
      rolling `client-stable` manifest. Transactional updater hardening remains part of the active
      launcher certification phase.
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
