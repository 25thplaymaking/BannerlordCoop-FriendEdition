# Coop Mod Ops — Standing Workflow & Rules

A living checklist for changing the modded Coop server/client. **Every time a mistake bites,
add a rule here.** Read this before touching anything that affects the join handshake.
Companion: [`COOP-MOD-INTEGRATION.md`](COOP-MOD-INTEGRATION.md) (how the port works).

---

## Rules (each earned from a real failure)

1. **Verify BOTH client and server for any handshake-affecting change — before saying "done."**
   The join validator runs on *both* peers with *both* manifests. A change that makes the server
   serve can still make the client refuse (or crash on load). Never conclude from the server alone.
   *(Earned: shipped a Serilog-2.x `GameInterface.dll` to the client; server was fine, client's Join
   button silently died on `MissingMethodException`.)*

2. **Never leave a backup/temp file inside a hashed module directory.**
   The Workshop hasher only excludes `bin/win64_shipping_server/` and the extensions
   `.log .pdb .md .bak .tmp`. Anything else — `.orig`, `.vprefix`, `.pre-catalog`, `.new` — inside a
   `Modules/<mod>/` tree is hashed and breaks that module's content hash → "unmanaged copy /
   content mismatch." Put backups OUTSIDE the module tree (`server/_mod_backups/`).
   *(Earned: left `PlayerSettlement/SubModule.xml.vprefix` in the module dir → content mismatch.)*

3. **Serilog version must match the target runtime.**
   Server = .NET Core → Serilog **2.x**. Client = .NET Framework → Serilog **4.x**. `GameInterface`
   binds `Serilog.ILogger` at compile time via `Common`; a 2.x build dropped on the 4.x client
   throws `MissingMethodException: Serilog.ILogger Common.Logging.LogManager.GetLogger()`. Build the
   client `GameInterface.dll` with `Common.csproj` on Serilog 4.2.0, the server's on 2.12.0. Restore
   the csproj after a one-off cross-build.

   **3a. The committed default IS the client (Serilog 4.x); the SERVER build is the one-off flip
   DOWN to 2.x — and it's THREE files, not one.** `Common.csproj` (PackageReference 4.2.0 + a
   `Serilog.Sinks.Seq` package), `Coop.csproj` (its *own* `<Reference Include="Serilog" 4.2.0.0>`),
   and `LogManager.cs` (the `.WriteTo.Seq(...)` sink — Seq's client packages don't exist on the 2.x
   server set). For a server cross-build, flip all three to 2.x (`Serilog 2.12.0`, drop the Seq
   package + sink line), build, then **restore to the 4.x client default**. If you flip only
   `Common.csproj`, `Coop.csproj` fails `CS1705: 'Common' uses 'Serilog 4.2.0.0' … higher than
   referenced 'Serilog 2.0.0.0'`. **Do not commit the 2.x state** — HEAD must stay 4.x so the client
   build feed (`launcher-release.yml`) and the local `mb2` deploy are correct by default.
   *(Earned: nearly committed an uncommitted 2.x server-flip as the repo default while wiring the
   launcher — caught because HEAD was already 4.x.)*

   **3b. NuGet audit now fails the build.** `dotnet build` errors on `NU1902/NU1903` (Scriban 7.2.0
   advisory, transitive via Coop.Core) as *errors*. Pass `-p:NuGetAudit=false` to build. It's an
   advisory gate, not a real break.

4. **Every active workshop mod's non-server-bin bytes must stay byte-identical across server,
   client, and receipt.** Any server-side edit to a hashed file (e.g. `SubModule.xml`) breaks the
   handshake. Prefer not editing; if the dedicated build truly needs an edit, mirror it on the
   client AND regenerate the receipt config/content hash — or exclude the file in the hasher.
   *(The dedicated "Invalid version type" assert on PlayerSettlement's `1.0.0.*` turned out NOT to
   need the `v` prefix in the current setup — revert to the stock `SubModule.xml`.)*

5. **Activation order must satisfy `IsActivationOrderValid`** (GameInterface WorkshopModuleDiscovery):
   frameworks before `Native`; `loadsBeforeCoop` mods (PlayerSettlement) between `Native` and `Coop`;
   all other gameplay mods AFTER `Coop`. Managed subsequence must equal
   `[frameworks + loadsBeforeCoop by LoadOrder] ++ [gameplay by LoadOrder]`. Applies to the server
   token, the client launch token/`activeModuleOrder`, and the pack `MANIFEST.json`.

6. **After changing any module file on disk, restart the server** — the manifest and hashes are
   computed at module discovery (boot), not per-join.

7. **`GameInterface.dll` is not integrity-gated at the client** (installer verifies only its own 2
   files; the runtime handshake hashes only Workshop mods, not Coop's own assemblies) — so swapping
   it is safe, but keep its `coop.files`/`SHA256SUMS` record in sync for hygiene.

8. **Nothing in `NoHarmonyInit` before `SetupLogging()` may touch the static `Logger`** — it's null
   until `SetupLogging` runs, so a `Logger.X(...)` call there throws NRE, which aborts mod init and
   crashes the whole game with `0xe0434352` on launch. Put boot-time arg parsing AFTER `SetupLogging`
   and null-guard (`Logger?.`) any logging that could run early. *(Earned: `TryParseCoopJoin` logged
   "auto-join armed" before logging existed → every launcher start (which always passes `/coopjoin`)
   crashed; the plain `.cmd` had no `/coopjoin` so skipped the path and masked it.)*

9. **"The launcher opened" is NOT "the game launched."** Verify a launcher change by confirming the
   GAME reaches the main menu and (for `/coopjoin`) logs `[CoopJoin] Main menu reached — publishing
   AttemptJoin` in `Coop_client.log` — not just that `CalradiaCoop.exe` showed a window. For a
   startup crash with no log, drop `coop-diag.on` beside `Bannerlord.exe` to arm the first-chance
   logger (`Coop_firstchance.log`), reproduce, read, then remove the file.

---

## Checklist: making a handshake-affecting change

- [ ] Decide what the change touches: catalog, activation order, a mod's bytes, or Coop assemblies.
- [ ] If a mod's on-disk bytes change on the server → they must change identically on the client and
      the receipt must be regenerated. Otherwise DON'T edit them server-side.
- [ ] Rebuild `GameInterface.dll` for the correct runtime(s): **server = Serilog 2.x**, **client =
      Serilog 4.x**. Restore `Common.csproj` afterward.
- [ ] Put any backup OUTSIDE the module tree (`~/bannerlord-coop/server/_mod_backups/`).
- [ ] Restart the service (`systemctl --user restart bannerlord-coop-seven.service`); confirm
      `phase":"serving"`, 4200 bound, `NRestarts 0`.
- [ ] **Server verify:** `find Modules/<mod> -type f | grep -vi win64_shipping_server` hashes match
      the client's for every active mod.
- [ ] **Client verify:** launch `Play Friend Edition.cmd`, attempt Join, read `Coop_client.log`
      (+ the on-screen "Module validation failed" reasons) — must be clean before "done."
- [ ] Update the friend pack (`suite-extract-ws8` → rezip) with the SAME change + fixed hashes.
- [ ] Add a rule above if anything surprised you.

## Reference: key paths
- Server: `bishop@205.209.116.114:~/bannerlord-coop/server/` — `engine-mods` (live), `run-seven-mods.sh`,
  `coophook.dll`, `_mod_backups/`. Service: `bannerlord-coop-seven.service`. Coop port **4200**.
- Client (owner): `P:\SteamLibrary\...\Mount & Blade II Bannerlord`; `Coop_client.log` beside
  `Bannerlord.exe`. Repo `mb2` junction → this install; a Release build of `source/Coop/Coop.csproj`
  auto-deploys the client DLLs there via `Deploy.targets` (ModName `Coop`).
- **Distribution launcher:** `tools/CoopLauncher` (`CalradiaCoop.exe`) — one-click, auto-joins via the
  `/coopjoin <host> <port> <pw>` boot arg (`CoopMod.TryParseCoopJoin`/`TryCoopJoin`). Replaces the old
  `Desktop\Play Friend Edition.cmd` for friends. See `tools/CoopLauncher/README.md`.
- Pack: `~/bannerlord-coop/private-distributions/…workshop8….zip`; extract `~/bannerlord-coop/upload-here/suite-extract-ws8`.
