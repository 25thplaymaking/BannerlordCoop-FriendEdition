# Friend Edition Changelog

## 2026-08-09 — workshop3 (current): ALL MODS ON

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
