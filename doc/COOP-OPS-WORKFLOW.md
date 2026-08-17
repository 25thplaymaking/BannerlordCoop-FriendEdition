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

10. **A launcher `{StaticResource}` that isn't defined crashes the launcher at RENDER time and leaves
    a ghost window — looks exactly like "I clicked and nothing happened."** WPF resolves a
    `Setter`/`TemplateBinding` `{StaticResource X}` inside a `ControlTemplate` trigger *lazily*: if `X`
    is undefined it becomes `DependencyProperty.UnsetValue`, and the throw only fires when that visual
    state actually renders (e.g. hovering the button applies the `IsMouseOver` trigger). The process
    dies (`0xe0434352`, `InvalidOperationException: '{DependencyProperty.UnsetValue}' is not a valid
    value for property 'Background'` in `Border.OnRender`) but the already-painted window stays on
    screen, ignoring every click — no flash, no `launcher.log` line, nothing. Two guards, both now in:
    (a) `App.OnStartup` installs a `DispatcherUnhandledException` reporter that logs and shows the
    real exception, then leaves it unhandled so WPF terminates instead of continuing corrupted UI;
    (b) a dangling-`StaticResource` check (every `{StaticResource K}` in `App.xaml`/`MainWindow.xaml`
    must have a matching `x:Key`). Diagnose a dead launcher via the Windows event log, not just
    `launcher.log`: `Get-WinEvent Application | ? Message -match CalradiaCoop` shows the `.NET Runtime`
    1026 event with the full exception + stack. **Validate launcher UI in its trigger states (hover,
    disabled), not just the default `--shoot` render** — the default state didn't touch `BloodBright`,
    so only a hover-state render reproduced it.
    *(Earned: `WarButton`'s hover trigger set `field.Background="{StaticResource BloodBright}"` but only
    a `BloodBrightColor` Color existed — no `BloodBright` brush; every hover-to-click crashed the
    launcher before the click registered.)*

11. **DedicatedServer.Core and the Coop server bin are one fail-closed release pair.** Rebuild the
    loader patch first, then run `DedicatedServerCompatibilityPatcher` against the final four Coop
    DLLs. Deploy its exact output to both physical `Win64_Shipping_Server` core locations and keep
    `SERVER-COOP-PAIRING.json` beside `deployment-sha256.txt`. A `COOP MODULE VERIFICATION FAILED`
    line is a failed boot, never an acceptable warning.

12. **Allow systemd's full stop timeout before touching the live files.** Wine can leave a helper
    process in `final-sigterm` after the Bannerlord process exits, so this service may consume its
    configured 45-second `TimeoutStopSec` and finish in `failed (Result: timeout)`. Do not race it
    with a manual kill or start copying while it is deactivating. Wait until the unit is no longer
    active and the Bannerlord `dotnet.exe` process is gone, take and byte-verify the save/module
    rollback snapshot, install the paired payload, then `systemctl --user reset-failed` before start.

13. **A Coop submodule constructor is too late to change dedicated-server eligibility.** Bannerlord
    v1.4.8 reads `DedicatedServerType` and filters the submodule set before constructing the active
    entries. A server-only exception must be installed by `coophook.dll` from
    `DOTNET_STARTUP_HOOKS`, when `TaleWorlds.MountAndBlade` loads, and it must remain an exact audited
    class-name allowlist. Do not enable client-presentation entries such as DismembermentPlus merely
    because their descriptor is active: its bootstrap loads WinForms and correctly remains filtered
    on the headless host. UIExtenderEx and MCM stay dependency-resolvable from their client bins, but
    their four presentation submodule classes are explicitly blocked before the engine requests
    missing server-path assemblies. Descriptor discovery is not proof: require the RGL log to show
    each required server-gameplay assembly loading before declaring those server modules active.

14. **The v1.4.8 save-definition scan also runs before Coop constructs compatibility handlers.**
    The pinned R&D package registers several native containers repeatedly; the rendered engine
    tolerated that, while the headless engine records fatal asserts and exits 84. Install the
    preserve-first container guard from `coophook.dll` when `TaleWorlds.SaveSystem` loads. A
    campaign-handler patch is too late. Require a fresh RGL error log with zero duplicate-definition
    asserts before accepting an R&D-enabled dedicated boot.

15. **Do not format `CampaignTime` during the first dedicated `OnGameStart` callback.** The raw
    tick value is available, but Bannerlord has not initialized the calendar divisor yet, so
    `ToString()` reaches `GetYear` and terminates the native host with divide-by-zero exit 84. Use
    invariant `NumTicks` for early canonical snapshots and require the host to reach `SERVING`.

16. **A per-collection `DisableParallelization` does NOT isolate a test from the rest of the
    assembly — only from its own collection's members.** GameInterface.Tests had accumulated five
    such collections (ModConfig, ModInformation role, Campaign.Current, GameThread cancellation,
    Fourberie) each trying to guard a different process-wide global, and tests kept failing anyway:
    `AuthorityRequestRouterTests` sits in `ModInformationRoleCollection` and still lost races to
    classes outside it that assign `ModInformation.IsServer`. Because the production code these
    suites drive reads process-wide state (`ModInformation`, `Campaign.Current`,
    `MBObjectManager.Instance`, the global Harmony set, `MessageBroker.Instance`), and the coupling
    is transitive through production code rather than visible in the test file, the only reliable
    fix is `[assembly: CollectionBehavior(DisableTestParallelization = true)]` — which
    `Coop.IntegrationTests` and `E2E.Tests` already use. It is now on `GameInterface.Tests` and
    `Coop.Tests` too. Symptom to recognise: a release build fails on a test that passes locally, and
    a *different* unrelated test fails each time. *(Earned: a nightly client release failed on
    Tournament and Registry tests that had nothing to do with the change being built.)*

17. **Never hard-code a "different" digest against `ManifestFactory.StableHash`.** It used to derive
    its fill char from `StringComparer.Ordinal.GetHashCode`, which .NET seeds randomly per process,
    so the value it produced changed between runs and a literal chosen to differ from it collided
    about one run in five. The fold is deterministic now, but the rule stands: pick the literal from
    outside the `fill..fill+4` band, and keep it a hex digit — the manifest wire shape requires 64
    hex characters, so a non-hex filler is rejected as a malformed manifest instead of as the
    mismatch the test intends.

18. **A published version string is not proof the payload uploaded — verify the asset against its
    own manifest.** During a GitHub API outage the stable launcher publish uploaded `launcher.json`
    and then 503'd on the binary, leaving a **9-byte** `CalradiaCoop.exe` behind a manifest quoting a
    real digest. The run showed as failed but the feed *looked* updated, and the previous good exe
    had already been clobbered by `--clobber`, so there was nothing to roll back to. The launcher's
    SHA gate fails closed, so every player is blocked from joining until it is republished. After any
    stable publish, download the asset and compare its SHA-256 to the manifest before calling it
    shipped — and check `githubstatus.com` before promoting during flaky API behaviour.
    *(Earned: `launcher-app` 2026.8.17.48 shipped a truncated exe; 2026.8.17.50 repaired it.)*

19. **A server cross-build in a worktree with an `mb2` junction silently overwrites the LIVE client
    module.** `Deploy.targets` runs on every build and copies the output into
    `$(RepoRoot)mb2\Modules\$(ModName)`, and every `ServerWork/*` worktree's `mb2` resolves to the
    real Steam install. So building `source/Coop/Coop.csproj -c Release` while the Serilog 2.x server
    flip is applied drops **server-flavour** assemblies onto the player's client — precisely the
    `MissingMethodException` failure rule 3 describes — and it stays broken until the launcher next
    reinstalls the client payload over it. Build server payloads with **`-p:ModName=`**, which is used
    only by `Deploy.targets` (`Condition="'$(ModName)' != '' and Exists('$(ModsRoot)')"`) so the
    references still resolve and the deploy is skipped. Afterwards verify with
    `Modules/Coop/bin/Win64_Shipping_Client/Common.dll` referencing **Serilog 4.2.0.0**, not 2.0.0.0.
    *(Earned: three server pairings in one session each deployed 2.x assemblies over the live install;
    the launcher happened to reinstall before the next launch, so it went unnoticed.)*

20. **A startup crash with no `rgl_log`, no `Coop_client.log` and no new `NoHarmony.txt` entry is not
    a mod crash — prove it from the WER report before touching the build.**
    `C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_Bannerlord.exe_*\Report.wer`
    lists every loaded module at fault time. If none of them are under `Modules\`, the engine died
    before the module layer and no client build can be responsible. *(Earned: a native
    `0xc0000005` in `TaleWorlds.Native.dll` was assumed to be a just-shipped client build; the WER
    report showed 125 loaded modules and not one mod assembly.)*

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
- [ ] If stop reaches `final-sigterm`, wait out `TimeoutStopSec`; require the unit to be non-active
      and the Bannerlord process absent before snapshotting or overwriting files.
- [ ] **Server verify:** `find Modules/<mod> -type f | grep -vi win64_shipping_server` hashes match
      the client's for every active mod.
- [ ] **Client verify:** launch `Play Friend Edition.cmd`, attempt Join, read `Coop_client.log`
      (+ the on-screen "Module validation failed" reasons) — must be clean before "done."
- [x] Build a fresh release-candidate pack from the ten-module manifest; never mutate or promote the
      historical `workshop8` archive as if it represented the current RBM-retired contract.
- [ ] For any launcher (`tools/CoopLauncher`) change: run `python tools/CoopLauncher/check-xaml-resources.py`
      (no dangling `{StaticResource}`), then verify a **hover-state** render, not just default `--shoot`.
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
- Pack: current non-stable RC is `work/release-candidate/FriendEdition-2026-08-11-6e3aaa7d9`
  with archive SHA-256 `9e355ef696872f9477faa7c255503cec517eb691bda7bc1bb4506afc8f7d7bc0`;
  its adjacent provenance receipt records the historical-input bridge. `workshop8` is historical only.
