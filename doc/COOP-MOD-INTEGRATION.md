# Coop Mod Integration — Findings & Framework

**Status:** grain.silo dedicated co-op server is **LIVE and SERVING the full modded campaign**
(ButterLib + RBM + ImprovedGarrisons + DismembermentPlus + Fourberie + Diplomacy +
UnblockableThrust + PlayerSettlement, plus the UI frameworks). This document is the reusable
"one system" other agents should extend instead of re-deriving the port each time.

Audience: agents working on Bannerlord Coop (this repo) and on the grain.silo server
(`bishop@205.209.116.114:~/bannerlord-coop`).

---

## 1. What was the actual blocker

The rendered **client** runs Bannerlord on **.NET Framework**, where two assemblies with the
same simple name but different versions can load side‑by‑side. Every BUTR‑family campaign mod
(Diplomacy, ImprovedGarrisons, Fourberie, PlayerSettlement) depends on **Bannerlord.ButterLib**,
which is built for .NET Framework.

The Linux **dedicated server** runs the game head‑less under **wine + xvfb** via
`dotnet.exe TaleWorlds.Starter.DotNetCore.dll` — i.e. **.NET Core (net6)**. .NET Core unifies
assemblies by *simple name* and **refuses a second copy** of an already‑loaded name. That single
difference is what made "just activate the mods on the server" fail. The failures cascaded:

| Symptom on the .NET Core server | Root cause | Fix (see §3) |
|---|---|---|
| `FileNotFoundException` resolving mod deps | Core won't probe module bins like the game's Framework loader does | `coophook.dll` AssemblyResolve |
| ButterLib "dependency conflict", then type‑load crash | `ValidateLoadOrder`/`ValidateHarmony` + WinForms crash‑reporter subsystems | Cecil‑neutralize to `ret`; stub renderers |
| `MarkSequencePoint` missing | ButterLib shipped net472 MonoMod | swap in **net6 MonoMod** |
| Serilog `IBatchedLogEventSink` / `FileStream` ctor | Coop used Serilog **4.x**; BUTR mods ship **2.x**; Core can't load both | rebuild Coop on **Serilog 2.x** (§4) |
| `FileLoadException: Assembly with same name is already loaded` at coop‑host start | `DedicatedServer.CoopDriver.EnsureLoaded` did a second `LoadFrom` of an already‑loaded assembly (e.g. `GameInterface`) | Cecil‑patch `EnsureLoaded` to reuse the loaded copy (§3) |

**Key invariant that makes all of this safe for the join handshake:** the Workshop file hasher
(`WorkshopModuleFileHasher`) **excludes `bin/win64_shipping_server/`**. Server‑bin edits (patched
ButterLib, stubs, net6 MonoMod, rebuilt Coop) therefore **do not change a module's content hash**,
so a modded server and a stock‑byte client still produce identical content hashes.

---

## 2. The runtime topology (where things live on grain.silo)

```
~/bannerlord-coop/server/
  engine-seven/        vanilla coop engine (ROLLBACK ONLY — do not delete)
  engine-mods/         LIVE modded engine (systemd runs this)
  engine/              shared base
  coophook.dll         durable startup hook (out of /tmp; survives reboot)
  run-seven-mods.sh    launch script (engine-mods + full mod token + hook)
  run-seven-vanilla.bak.*   vanilla launch script (rollback)
```

* Live service: **`systemctl --user … bannerlord-coop-seven.service`** → `run-seven-mods.sh`.
* Friends connect on the **coop LiteNetLib port 4200** (the `7210` arg is the TaleWorlds session
  port, not what clients dial). Password is group‑private.
* `WINEDEBUG=-all` hides wine's channels but **not** the `[DedicatedServer]` / `@DS@{…}` markers
  or the game log — watch those for health. `phase":"serving"` = up.
* **Save** (shared by every engine via the wine prefix):
  `~/Documents/Mount and Blade II Bannerlord/CoopData/DedicatedServer/Game Saves/…`.
  Because it is shared, **back it up before any modded run** — a modded save may not reload on the
  vanilla engine. Latest backup: `~/bannerlord-coop/backups/pre-modlive-*/GameSaves`.

### Hard isolation rule
`engine-mods` is now BOTH the live engine and the only mod sandbox. **Never launch a second manual
server from `engine-mods`** — the coop socket is fixed at 4200, so a manual test collides with the
live service and orphaned wine `dotnet.exe` keeps the port. If you must test a token, do it *through
the service* (edit `run-seven-mods.sh`, `systemctl --user restart`), or make a fresh copy on another
coop port. (This bit us once; the orphan had to be `kill -9`'d and the service restarted.)

---

## 3. The server‑side patch kit (reusable for ANY BUTR mod set)

All of these are **server‑bin‑only** (hash‑excluded) and already deployed under `engine-mods`.
Sources are preserved in **`tools/CoopServerModKit/`** so agents can rebuild them.

1. **`coophook.dll`** — `DOTNET_STARTUP_HOOKS` startup hook
   (`tools/CoopServerModKit/StartupHook.cs`, target **net6.0**). Two jobs:
   * `AppDomain.AssemblyResolve` → probe every `Modules/*/bin/Win64_Shipping_{Server,Client}` for a
     by‑name miss, so mod code resolves its bundled deps without polluting the root bin.
     **Reuses an already‑loaded assembly by simple name before `LoadFrom`** (Core's same‑name rule).
     Excludes `TaleWorlds.*/SandBox*/StoryMode*/Coop*/GameInterface/Missions/Common` (engine owns
     those); Serilog resolves from Coop's bin first so the whole process shares one Serilog.
   * First‑chance/unhandled exception capture to `/tmp/coop-fce.log`.

2. **ButterLib Cecil patch** (`tools/CoopServerModKit/butterlib-patcher/`, Mono.Cecil):
   neutralizes method bodies to `ret` so head‑less ButterLib doesn't abort or pop WinForms:
   `ButterLibSubModule::ValidateLoadOrder` and the
   `Enable()` of `ExceptionHandler / CrashUploader / DelayedSubModule / SubModuleWrappers2`.
   The patcher accepts only the pinned ButterLib v2.11.1 SHA-256 and exactly those five concrete
   type/method/signature matches; it no longer patches same-named methods elsewhere.

3. **Renderer stubs** — `BUTR.CrashReport.Renderer.WinForms/ImGui` re‑implemented as
   netstandard2.0 stubs (same public entry types minus the `Form`/ImGui bases) so the crash‑report
   type graph loads on a head‑less box.

4. **net6 MonoMod** — replace ButterLib's net472 MonoMod set with the **net6** build
   (`MarkSequencePoint` 5‑arg overload is Framework‑only).

5. **`DedicatedServer.Core.dll` `CoopDriver.EnsureLoaded` patch**
   (`tools/CoopServerModKit/dspatch/`). Prepends a scan of
   `AppDomain.CurrentDomain.GetAssemblies()` by simple name (ordinal‑ignore‑case) and returns the
   already‑loaded assembly **before** the original `LoadFrom`. This is Coop's own dedicated‑host
   loader, not our hook — with ButterLib active the shared assemblies (e.g. `GameInterface`) are
   already loaded when it runs, and the un‑patched second `LoadFrom` throws
   *"Assembly with same name is already loaded"*.
   **Gotcha:** the copy that actually loads is the one in the **root** `bin/Win64_Shipping_Server/`,
   not the module's `Modules/DedicatedServer.Windows/bin/…` copy. Patch the **root‑bin** DLL.
   The patcher accepts only the pinned dedicated-server input and exactly one
   `EnsureLoaded(System.String):System.Reflection.Assembly`. The old permanent
   `/tmp/ensure.log` probe is removed.

6. **Dedicated-server release pairing**
   (`tools/DedicatedServerCompatibilityPatcher/`). After the Coop server bin is final, this tool
   rewrites the server's four expected Coop DLL hashes, restores the code-4 abort that an older
   compatibility patch disabled, and emits `SERVER-COOP-PAIRING.json`. A later DLL drift therefore
   blocks boot instead of merely logging `COOP MODULE VERIFICATION FAILED` and continuing.

`coophook.dll` always records unhandled exceptions. High-volume first-chance diagnostics are opt-in
with `COOP_SERVER_KIT_DIAGNOSTICS=1` and capped at 400 KB.

### Reproduce a patched DLL
```bash
# EnsureLoaded patch (example)
dotnet run --project tools/CoopServerModKit/dspatch -- \
  DedicatedServer.Core.dll DedicatedServer.Core.patched.dll
```
Verify with `ilspycmd DedicatedServer.Core.patched.dll` — `EnsureLoaded` should open with the
`AppDomain.CurrentDomain.GetAssemblies()` loop.

---

## 4. Migrating a mod / Coop itself to Serilog 2.x

.NET Core cannot load Serilog 2.x (BUTR mods) and 4.x (old Coop) together. We aligned **Coop** down
onto Serilog **2.12.0** so one Serilog serves everything. To do the same for any component:

1. **`Common.csproj`** — `<PackageReference Include="Serilog" Version="2.12.0" />`; remove
   `Serilog.Sinks.Seq` (that sink is what forced 4.x — it's dev‑only telemetry).
2. **`Coop.csproj`** — change the `Serilog` `<Reference>` to `Version=2.0.0.0`,
   HintPath `..\packages\Serilog.2.12.0\lib\net471\Serilog.dll`; drop
   `Serilog.Enrichers.Process`, `Serilog.Sinks.Debug`, `Serilog.Sinks.File` refs that only exist in
   the 4.x closure.
3. **`LogManager.cs`** — remove `.WriteTo.Seq(...)`. Keep enrichers + `OutputSinkManager` sink:
   `Configuration.Enrich.With(new NetworkEnricher()).Enrich.With(new StackTraceEnricher())
   .WriteTo.Sink(new OutputSinkManager()).CreateLogger()`.
4. **`CoopMod.cs`** — drop `.Enrich.WithProcessId()` (Process enricher is 4.x‑only).
5. Serilog 2.x has no `IBatchedLogEventSink`; if you need a file sink on Core, build
   `Serilog.Sinks.File` for **net5.0+** (the net472 build uses a Framework `FileStream(FileSystemRights…)`
   ctor that throws on Core). Staged at `Modules/Bannerlord.ButterLib/bin/Win64_Shipping_Server/`.

Rebuild and deploy the affected assemblies to **`Modules/Coop/bin/Win64_Shipping_Server/`** (server)
— they are `netstandard2.0`, so the same DLL also drops into the client bin.

---

## 5. Connecting a NEW mod to the coop framework

The framework is the **Workshop suite** (`GameInterface/Services/WorkshopMods/…`). To make a new
BUTR/Workshop mod a first‑class, interchangeable member:

1. **Catalog** — add a `new(...)` row to `WorkshopModuleCatalog.ExpectedModules`:
   `moduleId, workshopId, steamManifestId, version, loadOrder, Role, Profile,
   featureActiveExpectedOnServer, featureActiveExpectedOnClient, loadsBeforeCoop?`.
   * Pick a **Role/Profile**: `Campaign + ServerAuthoritativeCampaign` (world state — server
     authoritative), `Mission + DeterministicMission` (combat), `Presentation + ClientPresentation`
     (visual; config hash is exempt), `Framework + AllPeersExact`.
   * **All‑playable = symmetric:** set **both** `featureActiveExpectedOnServer: true` **and**
     `featureActiveExpectedOnClient: true`. The validator (`WorkshopManifestValidator`) refuses a
     peer that runs a mod the catalog marks inactive ("must keep X inactive"), and refuses a mod
     present on one side but absent on the other. So server and client must run the **identical**
     active set.
   * If the mod's Harmony patches must exist before Coop builds its container (PlayerSettlement
     does), set `loadsBeforeCoop: true` and place it **before Coop** in the activation order.
2. **Activation order** — add it to the server token in `run-seven-mods.sh` **and** to
   `activationPolicy.client.{exactModuleOrder,activeModuleOrder}` + `server.exactActiveModuleOrder`
   in the distributable `MANIFEST.json`, keeping Workshop mods in `loadOrder` order and any
   `loadsBeforeCoop` mod ahead of Coop.
3. **Receipt** — regenerate `Coop/WorkshopSuite/MANIFEST.json` (content + configuration SHA‑256).
   The suite builder uses **ordinal** sort (`WorkshopIntegration.psm1` → `[StringComparer]::Ordinal`);
   never `Sort-Object` (culture‑aware) — a culture sort produced the "unmanaged copy" refusal.
4. **Server deps** — if the mod pulls a framework the server can't load, add its server‑bin overlay
   the same way (§3): resolve via `coophook`, and Cecil‑neutralize any head‑less‑hostile
   validator/WinForms path. Content hash is unaffected (server‑bin excluded).
5. **Rebuild** `GameInterface.dll` (netstandard2.0 → both roles) and deploy to
   `Modules/Coop/bin/Win64_Shipping_{Server,Client}/`. Server + client **must** share the same
   catalog build or their handshake verdicts diverge.

Adapters that bridge a mod into Coop live under
`GameInterface/Services/WorkshopMods/…`; they must **fail closed** when their original module is
inactive.

---

## 6. Client distributable (Friend Edition)

* Latest: `private-distributions/BannerlordCoop-FriendEdition-2026-08-10-workshop8-WorkshopSuite.zip`
  (all 17 modules **ACTIVE**, PlayerSettlement before Coop, new catalog `GameInterface.dll`).
* The installer (`Setup-ManagedSuiteClient.ps1`) drives activation from
  `MANIFEST.json.activationPolicy.client.activeModuleOrder` and writes `LauncherData.xml`. It throws
  if `activeModuleOrder` is empty **or** `activateAllManagedModules` is true — so keep
  `activateAllManagedModules:false` and list every active module explicitly.
* The installer only hash‑verifies its own two files, and the runtime handshake only hashes the
  **Workshop** mods (via the receipt) — **not** Coop's `GameInterface.dll` — so swapping the catalog
  DLL is safe; we still re‑bump its `coop.files`/`SHA256SUMS` record for hygiene.
* Steam Workshop sources were deleted; the **only** archive of audited mod bytes is the server's
  `engine-mods/Modules` + the suite staging dirs. Protect them; build packs from there.

---

## 7. Verified vs unverified (report honestly)

* **Verified live:** the dedicated server boots the full modded campaign, reaches
  `phase":"serving"`, binds 4200, 0 restarts. Reproduced across several service restarts.
* **Not yet verified end‑to‑end:** an actual rendered client completing the join handshake against
  the modded server. That needs a Windows Bannerlord client (owner/friend machine) and cannot be
  done head‑lessly here. The distributable is built and internally consistent (activation policy,
  load order, receipt, hashes), but the first real join is the remaining proof.

## 8. Rollback to vanilla
Point `engine_root` at `engine-seven` and restore the vanilla token in `run-seven-mods.sh`
(`cp run-seven-vanilla.bak.<ts> run-seven-mods.sh`), then `systemctl --user restart`. Restore the
save from `backups/pre-modlive-*/GameSaves` if a modded save won't load on vanilla.
