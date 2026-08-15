# Friend Edition Changelog

## 2026-08-15 — Fourberie finalization review (draft PR; release still held)

The final bounded `v1.4.8` review classified Fourberie's remaining local mission
plumbing, rollback-protected menu/model presentation, and 14 internal helpers
already reached through source-derived Coop transactions. The exact authority
ledger moved from 13,309/13,729 with 420 unclassified to 13,682/13,729 with 47
unclassified and zero blocked. Focused structural coverage was added for these
classifications, production/test code builds with zero errors, and the focused
in-process xUnit class passes 3/3.

This is not a functional-completion claim. The transitive gameplay validator
still rejects 272 Fourberie records and the feature ledger remains 28/30; the
remaining consequence callbacks require bounded host commands and focused
tests. A proposed generic callback/closure bridge was rejected because it would
trust client-captured costs and outcomes. The exact remaining families and
execution order are now recorded in `docs/bannerlord-1.4.8-migration.md`. War
Sails remains absent, and no package was published or server/save changed.

## 2026-08-15 — Bannerlord v1.4.8 offline migration candidate (release held)

Friend Edition now identifies Bannerlord `v1.4.8` as its source/build and Steam
Workshop compatibility target. Exact client build `24573425`, dedicated-server
build `24571419`, depot manifests, full inventories, the complete `v1.4.7`
rollback inventory, and all ten Workshop manifests are recorded in
`deploy/bannerlord-1.4.8-inputs.json`. War Sails remains disabled and absent;
RBM remains retired.

The candidate adopts DismembermentPlus `v2.0.8.8` manifest
`751945004455697202` and Fourberie `v1.4.7.6` manifest
`1598945672157391038`; the other eight Workshop pins remain exact. The verified
847-file client suite ZIP hashes to
`bbdc850913fb3d943c8105ee26e6ee1eb2ce33c5c9df647c73e84850970bc825`.
Production code builds against the actual `v1.4.8` assemblies with zero errors,
the package/overlay gates pass, and the dedicated-server loader and 16-file UI
support closure are repinned to actual `v1.4.8` bytes. A Serilog-2 headless Coop
bin was release-paired offline: paired core SHA-256
`6b3ed5a858aaf3afcab1f7770d76ef976e8bdc097adfcc374d368b4697daf74c`,
receipt SHA-256
`8b93ba231e9f822bf0a77b4c3688544b713ca0c1e45f78e0d1a8b634fbdf6c2f`.

The regenerated authority audit classifies 13,309 of 13,729 required candidates
with zero blocked and 420 unclassified, all in Fourberie. The older conflicting
419/495 counts are superseded. Structural validators pass for Diplomacy,
Improved Garrisons, Player Settlement, DismembermentPlus, Unblockable Thrust,
and Separatism, but this does not substitute for rendered functional proof.
Stable stays held: the 420 Fourberie mission/menu/VM routes, Player Settlement's
non-empty construction path, full server overlay/isolated-save boot,
rendered two-client matrix, and live promotion remain incomplete. No production
server or save was changed.

## 2026-08-11 — Separatism authority closure (development; stable held)

Separatism now has exact authority closure for all 57 required candidates. The recovered
`player_is_requesting_fallen_to_join` option is restored on clients behind the host capability
snapshot. Its obsolete vanilla persuasion destination is replaced by a complete typed request/result
route: the server re-derives the ruler from the authenticated peer, validates the expected kingdom,
target clan/leader stable IDs, membership revision, peace, ruler, and minor-faction rules, then commits
one clan transition through the existing Coop membership funnel. Exact duplicate requests replay the
cached result; stale/conflicting requests are rejected; failed membership commits restore independent
clan state.

The global friendship/enemy, clan join/leave/defection, settlement-rebellion, and loyalty policies now
have direct boundary tests. Configured settlement rebellion is explicitly disabled on clients so the
native behavior cannot become a second campaign owner. The authority ledger is now 4,599 classified
and 5,483 open of 10,082 candidates across 41,050 methods. Focused unit/E2E, inventory, development
audit, and exact Separatism validation pass. Stable remains held for the four remaining campaign-mod
closure slices and rendered/release gates.

## 2026-08-11 — combat authority routes (development; stable held)

UnblockableThrust and DismembermentPlus now have exact authority closure: 21 required candidates,
21 classified, none blocked or open. UnblockableThrust remains a pure collision decision inside
Coop's accepted collision owner. DismembermentPlus replaces its peer-local random `RegisterBlow`
path with a canonical, capability-gated replicated cosmetic event selected by the victim-authority
peer; receivers re-derive the battle/agent authority, reject malformed, duplicate, stale, or
conflicting events, and call only the original visual routine without replaying damage. One eager
coordinator owns the session subscription while both dedupe and source sequences are bounded and
battle-scoped; failed presentation does not consume an event, and slow motion remains disabled in Coop.

The authority ledger is now 4,542 classified and 5,537 open of 10,079 candidates. Focused combat,
module-lifetime, capability-convergence, absent-module, and mismatch suites pass, as do the
development audit and exact gameplay-module validator. Stable remains held for Separatism and the
remaining campaign-mod routes plus the existing rendered/release gates.

## 2026-08-11 — mod authority-routing foundation (development; stable held)

The earlier ten-module RC is superseded for functional-completeness work. The generated review still
covers 41,000 exact methods, now with deterministic IL called-member evidence and direct/transitive
authority signals. A reconciled authority ledger identifies 10,079 candidates: 4,521 framework or
retired records were classified at this foundation milestone, while release validation rejected
5,558 open active-gameplay records.

All six active gameplay adapters now use the common `IWorkshopModule` catalog/fingerprint/config
path; retired RBM is no longer declared by the mission composition root. A server-owned, canonical,
revisioned capability snapshot reuses the authenticated host config session and is covered for
registry validation, convergence, late requests, and forged local origin.

This milestone does not call blocked behavior complete. Typed routes remain for Improved Garrisons
management, Dismemberment cosmetics, Fourberie actions/models/mission entry, Diplomacy operations,
Player Settlement construction/persistence, and Separatism's omitted conversation. Stable remains
held until release-mode authority validation and rendered option coverage pass, followed by the
existing install/join and auto-resolve gates.

Foundation verification completed with 3,392 tests passed, 18 skipped, and zero failed. The solution
build completed with zero errors; Workshop tooling, the 41,000-method inventory validator, and the
development authority validator all passed. Release authority validation intentionally remains red
on the exact 5,558 open gameplay candidates.

## 2026-08-11 — ten-module stabilization release candidate (stable held)

The production contract now contains exactly ten Workshop modules; RBM is retired after its native
campaign-initialization crash. Catalog, deployment manifest, launcher token, and client/server order
agree. Separatism is included as Friend Edition source behavior, not an eleventh Workshop payload.

The bounded stabilization pass closes the inherited defects found after workshop8:

- the dedicated host now pins the exact four live Coop assemblies and aborts on mismatch;
- ButterLib and loader patchers require exact input hashes plus type/method/signature/count matches;
- launcher updates are SHA-required, staged, traversal-safe, transactional, and fail Join closed;
- auto-resolve completion has one authoritative conclusion and releases undecided claims;
- map readiness suppresses only the known transient null path, and UDP host probing is tested;
- Fourberie contextless create routes and Dismemberment's unsynchronized cosmetic path fail closed;
- Separatism, Improved Garrisons, Diplomacy collisions, combat rules, configuration authority, and
  the complete managed mod function surface have focused certification records.

Live server integrity is verified: exact release pins pass, the host reaches `phase:"serving"`, UDP
4200 is bound on IPv4 and IPv6, and all entries in `deployment-sha256.txt` verify. Stable publication
remains manual.

The local release gate is now complete: 3,383 tests passed, 18 were skipped, and none failed. A fresh
ten-module release candidate was built from commit `6e3aaa7d9`, independently verified across 845
files, and passed the managed-client dry run. Its archive SHA-256 is
`9e355ef696872f9477faa7c255503cec517eb691bda7bc1bb4506afc8f7d7bc0`. Because Claude's earlier
cleanup removed the original Steam snapshots, the retained sanitized inputs were accepted only after
each of the ten modules exactly matched the historical audited receipt on Workshop ID, Steam manifest,
content hash, and configuration hash. RBM is the only historical receipt entry omitted. The builder's
production manifest remains strict; the RC has a separate provenance receipt documenting the bounded
input accommodation.

The RC has not been installed, deployed, uploaded, or promoted. Stable is held for one rendered client
install/update/join and the original Sea Raider auto-resolve reproduction.

## 2026-08-10 — workshop8 (historical): full modded server brought live

**Supersedes workshop7's per-role gating.** The wall workshop7 hit — "the
headless dedicated build cannot initialise Bannerlord.ButterLib" — is **broken**.
grain.silo now serves the full modded co-op campaign live (all 11 Workshop mods
active on both roles), reproduced across service restarts (`phase":"serving"`,
port 4200, 0 restarts).

### How ButterLib was ported to the .NET Core dedicated server
The client runs .NET Framework (two same-named assembly versions coexist); the
server runs .NET Core (unifies by simple name, refuses a second copy). The fix
is a **server-bin-only** kit (hash-excluded, so the join handshake is unaffected):

- `coophook.dll` (`DOTNET_STARTUP_HOOKS`) resolves each module's deps from its
  bin and reuses already-loaded assemblies before `LoadFrom`.
- Cecil-neutralize ButterLib's `ValidateLoadOrder`/`ValidateHarmony` and the
  WinForms crash-reporter subsystems; stub the WinForms/ImGui renderers.
- Swap ButterLib's net472 MonoMod for the **net6** build.
- Rebuild **Coop on Serilog 2.x** so it shares one Serilog with the BUTR mods.
- Cecil-patch **`DedicatedServer.CoopDriver.EnsureLoaded`** (root-bin copy) to
  reuse an already-loaded assembly instead of a second `LoadFrom` — this was the
  final `FileLoadException: Assembly with same name is already loaded` at
  coop-host start.

Kit sources: `tools/CoopServerModKit/`. Full writeup + how to add a mod / migrate
to Serilog / wire into the coop framework: `doc/COOP-MOD-INTEGRATION.md`.

### Distributable
`activationPolicy` flipped to **all-active**; corrected load order so
**PlayerSettlement activates before Coop** (was after — harmless while inactive,
crashes when active). New catalog `GameInterface.dll` (all mods
`featureActiveExpectedOn{Server,Client}: true`) ships in the client pack.

**Test boundary:** server proven live head-lessly; the first real *client* join
handshake is the remaining proof (needs a Windows Bannerlord client).

## 2026-08-10 — workshop7: per-role activation

**Supersedes workshop6**, whose all-seventeen-active policy crashes clients
while loading into the server.

### What changed and why

Loading into the server crashed clients with a native access violation while
initialising the transferred world. The cause was an asymmetry, not a bug in
any one mod: the client was running four campaign-mutating modules that the
host was not running at all, so the two sides did not agree on the world's
objects.

I then tested whether the host could run them. **It cannot, and the wall is
specific: the headless dedicated build cannot initialise Bannerlord.ButterLib.**
Verified live three ways — plain client-binary copy, de-duplicating the eight
Serilog/System assemblies ButterLib ships at different versions than Coop's,
and seeding the shared dependencies into the root bin so they resolve before
Coop loads. It dies loading `Bannerlord.ButterLib.dll` every time. Diplomacy,
ImprovedGarrisons, Fourberie and PlayerSettlement all depend on ButterLib.

### Activation is now per role

| Module | Host | Client |
| --- | --- | --- |
| Bannerlord.Harmony | active | active |
| ButterLib, UIExtenderEx, MBOptionScreen | inactive | active |
| RBM, DismembermentPlus, UnblockableThrust | inactive | active |
| ImprovedGarrisons, Fourberie, Diplomacy, PlayerSettlement | inactive | inactive |

Combat and visual modules create no campaign objects, so they run where they
are felt and the host stays authoritative over the world. The four campaign
modules stay packaged, hash-pinned and handshake-verified — enabling them later
is a catalog change, not a repackage — but they stay off on **both** roles,
because client-side-only campaign mods are exactly what crashed the load.

Commit `9a9db6751`; 259 unit tests green; installer validate-mode passes.

## 2026-08-10 — workshop6 (superseded): join fixed (PlayerSettlement pin)

**Client package:** `FriendEdition-WorkshopSuite-workshop6.zip` — SHA-256
`c238782e4632df1eb6d7e3c569eb8185dc28ec52e71c145648e163e1d656d6fb`.
On the server as `BannerlordCoop-FriendEdition-2026-08-10-workshop6-WorkshopSuite.zip`.
**Supersedes workshop5, whose receipt refuses every join.**

### Fixed

- **"Server/Client loads an unmanaged copy of 'PlayerSettlement'" — every
  join refused.** The installed files were correct on both peers; the
  *pin* was wrong. The suite builder ordered each module's hash lines with
  PowerShell's `Sort-Object -CaseSensitive`, which is culture-aware, while
  the runtime hasher orders with `StringComparer.Ordinal`. For ten of the
  eleven modules those orders coincide — PlayerSettlement's file names
  diverge, so its pinned configuration hash was one the game could never
  reproduce. It stayed invisible while the module was staged-inactive
  (inactive modules advertise their pins without re-hashing) and surfaced
  the instant it was activated. Builder now sorts ordinally for both the
  module digests and the receipt digest; the receipt's PlayerSettlement
  entry is corrected and its digest recomputed (commit `ec5b60013`).
  A culture-dependent pin was also a latent cross-machine bug — two
  builders in different locales could disagree.
- Discovery now logs *which* condition made a component count as unmanaged
  (receipt entry / version / managed path), so this class of refusal is
  self-explaining instead of needing a live bisection.

Verified: all eleven modules re-hash to their pins under the ordinal rule;
server restarted with zero unmanaged reports; tooling suite passes;
installer validate-mode passes against a real game install.

## 2026-08-10 — workshop5 (superseded): all seventeen modules active

**Client package:** `FriendEdition-WorkshopSuite-workshop5.zip` — SHA-256
`3adce32dbdf7735ea354daf2d484fa4fbf89c602fa1a0aea32752054a53843a3`.
Also on the server as
`BannerlordCoop-FriendEdition-2026-08-10-workshop5-WorkshopSuite.zip`.
**Supersedes workshop4** (whose order deadlocks client startup).

### For players

Every module is enabled, in this exact order — note PlayerSettlement's
position, which is load-bearing, not cosmetic:

`Bannerlord.Harmony → ButterLib → UIExtenderEx → MBOptionScreen → Native →
SandBoxCore → CustomBattle → Sandbox → StoryMode → **PlayerSettlement** →
Coop → RBM → ImprovedGarrisons → DismembermentPlus → Fourberie →
Diplomacy → UnblockableThrust`

### Fixed

- **PlayerSettlement runs on 1.4.7 after all.** The apparent version
  incompatibility was an environment fault: a stale
  `BannerlordPlayerSettlement.debug.log` in `C:\ProgramData` left by an
  elevated 2025 session, Administrator-owned and read-only for the player.
  The mod opens it for append during `OnSubModuleLoad`, was denied, and
  died. Clearing that file lets it boot normally.
- **The remaining co-op hang was load order.** Coop's PlayerSettlement
  adapter purges and re-guards the module's load-time Harmony patches, so
  the module must load *before* Coop; the blanket "content mods load after
  Coop" rule put it after and startup deadlocked. Components can now
  declare `LoadsBeforeCoop`, activation-order validation expects two groups
  around Coop, and a pre-Coop component placed after Coop is rejected
  (commit `ea3c8ba95`). No LoadOrder pins changed, so receipts and package
  hashes are unaffected.

### What "every mod playable" means today

Fully live: RBM combat, DismembermentPlus, UnblockableThrust, Fourberie,
Diplomacy (its screens plus the routed Donate Gold), ImprovedGarrisons
(settings routed client→server).

Still gated: **PlayerSettlement's settlement construction.** This is not a
switch. A new settlement is a new `MBObjectManager` object built from
generated XML during module registration — which happens before a campaign
loads — which is why the mod itself saves and restarts the game to
materialise one. In co-op that means every player's game must re-enter the
campaign with the new object registered, i.e. a coordinated group-wide
save-and-reload. That is the next feature, not a flag flip, and it will be
built and tested deliberately rather than rushed into a live world.

## 2026-08-09 — workshop4 (superseded): all-mods client actually boots

**Client package:** `FriendEdition-WorkshopSuite-workshop4.zip` (sidecar
`.sha256.txt` beside it; also on the server under `private-distributions/`).
**Supersedes workshop3, which crashed at first launch and was never playable.**

### Fixed

- **Every all-mods client launch crashed (or silently broke the mods) at
  boot.** A second, older policy enforcer — the framework compatibility
  boundary — still implemented the retired staged-inactive design: when it
  saw ButterLib/UIExtenderEx/MCM active it deregistered UIExtenders,
  disabled ButterLib subsystems, purged their patches, and aborted Coop
  startup by design. It now byte-verifies the active framework cohort
  against the audited fingerprints and lets it run unmodified; partial
  cohorts and fingerprint drift still abort. Live-proven with three
  consecutive clean full-17-module boots (commit `d122df3d1`).
- **Startup fatals died invisible**: the TaleWorlds watchdog is an attached
  debugger and kills the process before logs flush, which is why the crash
  above took a live bisection to name. Coop now writes its own first-chance
  and unhandled exceptions synchronously to `Coop_firstchance.log` beside
  the game executable — the next mystery crash names itself.

## 2026-08-09 — workshop3 (superseded, never playable): ALL MODS ON

**Client package:** `FriendEdition-WorkshopSuite-workshop3.zip` (sha in the
sidecar `.sha256.txt`; also on the server under `private-distributions/`).
**Supersedes workshop2 — everyone must update; a workshop2 client is refused**
(the activation policy is part of the handshake).

### For players

- **Every mod is on, everywhere.** The installer enables all seventeen
  modules in the pinned order: Harmony → ButterLib → UIExtenderEx →
  MBOptionScreen → Native → SandBoxCore → CustomBattle → Sandbox →
  StoryMode → Coop → RBM → ImprovedGarrisons → DismembermentPlus →
  Fourberie → Diplomacy → UnblockableThrust → PlayerSettlement.
  RBM combat, Fourberie, Diplomacy screens, ImprovedGarrisons — all of it
  is live in your game now. Same rule as always: extract, run
  `Run-ClientSetup.cmd`, don't rearrange the mod list by hand.
- **Honest caveat:** mod actions that aren't yet routed through server
  authority are best-effort for cross-player consistency — a mod feature
  may apply on your screen before (or without) the server's world agreeing.
  Routing work continues underneath and needs no further package updates.

### Server / engineering record

- Catalog activation policy flipped to all-active-both-peers
  (commit `9914cbfff`); the handshake now byte-verifies every mod package
  every session instead of warning that nothing is active.
- The wine-hosted 117131 server engine loads module DLLs only from
  `bin\Win64_Shipping_Server`; the host overlays client binaries there and
  the module hasher ignores that overlay so audited pins stay valid.
- **RBM runs as a server-side stub** (`SubModule.xml` only): its
  combat-parameter data natively crashes the old server engine (bisected
  live). The server attests RBM's audited receipt pins so clients are still
  byte-verified; RBM executes on clients, which is where its combat math
  matters. Full RBM module preserved at `backups/rbm-full-module`.
- Discovered en route: the "legacy" modded server loadout had been running
  bare module stubs all along — no mod data or code was ever loaded
  server-side before tonight. Ten of eleven mods now load real data
  server-side; boot verified SERVING on `friendeditionws1`.

## 2026-08-09 — workshop2 (superseded)

**Client package:** `FriendEdition-WorkshopSuite-workshop2.zip` — SHA-256
`528c9e848a51765c80cd2d940d103ffe1aed7478283267d599653b48b09bf884` (724 MB).
Also at `~/bannerlord-coop/private-distributions/BannerlordCoop-FriendEdition-2026-08-09-workshop2-WorkshopSuite.zip` on the server.
**Server:** grain.silo `bannerlord-coop-seven.service`, fresh world `friendeditionws1`, direct connect UDP 4200 (password in `server-config.json`).
**Supersedes workshop1 — everyone must update; the workshop1 package cannot join** (its join validator is the one fixed below).

### For players

- **One installer, one experience.** Extract the ZIP to a normal folder and run
  `Run-ClientSetup.cmd`. It verifies every file, backs up anything it replaces,
  installs all twelve modules, and enables exactly the seven that should run:
  `Bannerlord.Harmony → Native → SandBoxCore → CustomBattle → Sandbox → StoryMode → Coop`.
- **Do not change the launcher mod list.** The other mods (Diplomacy, RBM,
  ImprovedGarrisons, and the rest) install unchecked on purpose: each one turns
  on for the whole group at once, server-side, when its co-op integration is
  ready. Checking one manually gets your join refused with a message saying so.
- If the game crashes at startup right after the launcher, or the launcher
  keeps unchecking Harmony: launch directly instead —
  `bin\Win64_Shipping_Client\Bannerlord.exe /singleplayer _MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*Coop*_MODULES_`
  (some installs are missing the launcher's DLL-verifier helper; Steam →
  Verify integrity of game files repairs it).

### Fixed

- **Joining was impossible in every configuration.** Bannerlord only resolves
  ACTIVE modules, so staged-but-inactive mods can never appear in the join
  handshake's manifests — yet the validator required all eleven on both sides.
  Mods off: refused for "missing" packages; mods on: refused by the activation
  policy. The validator now verifies exactly the modules a session actually
  runs; a mod inactive on both sides is a warning, not a refusal. One-sided
  presence, byte mismatches, and wrong activation remain fatal — those are the
  checks that protect a session. Versions also compare semantically now
  ("v4.3.4.0" is "v4.3.4", not a mismatch).
- Coop startup crash when the `Bannerlord.Harmony` module is not first and
  active (Coop deliberately ships no `0Harmony.dll` of its own): root-caused to
  the TaleWorlds launcher force-deselecting community modules whenever its
  DLL-verifier helper executable is missing from the game install.

### Server (same day, earlier)

- Deployed build `2026-08-09-workshop1` → validator hotfix (commit `327f60f50`):
  Workshop module integration foundation and contract, the first routed mod
  action (Diplomacy's Donate Gold through full server authority), the
  birthAndDeath config preflight, and the Workshop manifest handshake.
- Server topology conformed to the suite policy: seven active modules, all
  eleven Workshop mods staged id-named and receipt-backed, byte-identical to
  the client package. Fresh world `friendeditionws1`; previous world and every
  replaced file preserved in timestamped backups.
- CI became the gate of record and is green (first-ever runs on this branch
  surfaced six latent defects, all fixed; PR #2 and PR #3 merged).

### Server hotfix (same day, after release cut)

- **First join died right after character creation** ("Client has been
  stopped"): finishing character creation fires a mod-config request before
  the server has unpacked the joiner's hero transfer, and the server's
  unmapped-peer guard answered it by disconnecting. The guard now ignores
  requests from not-yet-mapped peers instead of disconnecting (commit
  `dc915a727`), deployed server-side only — the workshop2 client package is
  unaffected and stays current. Live-verified milestones from the same
  session: the fixed validator accepted a real join (all eleven staged mods
  warn-only), and launching with RBM hand-enabled was refused one-sided as
  designed.

### Known state / caveats

- **The audited Workshop bytes now live only in the suite** (and the server's
  copy): Steam Workshop sources were unsubscribed and deleted locally, and RBM
  and Fourberie have newer upstream versions, so the audited builds cannot be
  re-downloaded. Treat the suite staging directory and ZIP as the canonical
  archive. Adopting any mod update requires a fresh audit and re-pin.
- The mods are packaged, pinned, and handshake-verified but none execute yet —
  the game plays as vanilla co-op under Friend Edition authority. Activation
  flips per mod as each integration gate passes (Diplomacy first; its Donate
  Gold action is already routed and E2E-gated up to the activation boundary).
- The first successful end-to-end join on the fixed validator had not yet been
  observed when this release was cut; the join path is code-verified
  (256/0 unit gate) but not yet live-proven.
- workshop2 is a verified single-file transformation of workshop1: the same
  audited module bytes with only `Modules/Coop/bin/Win64_Shipping_Client/GameInterface.dll`
  replaced (validator fix) and its two hash records updated; the full suite
  verifier (965 files) and the installer's validate mode both pass on the
  result. The suite builder could not be re-run because the Workshop sources
  no longer exist (see above).

## 2026-08-08 — mountfix1

- Prior deployment lineage (commit `3c130aac`); see the server's
  `releases/2026-08-08-mountfix1-3c130aa/` records.
