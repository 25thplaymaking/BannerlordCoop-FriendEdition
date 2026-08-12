# Calradia Co-op Launcher (Frontir)

A one-click, Bannerlord-themed launcher that loads the co-op mod set and rides straight to the
group host — no module list, no manual connect. Replaces `Desktop\Play Friend Edition.cmd`.

## What it does

1. **Finds Bannerlord** — explicit `gamePath`, else auto-detects from every Steam library
   (`libraryfolders.vdf`, app `261550`).
2. **Shows host status** — TCP-probes `serverHost:serverPort`; the banner sigil glows gold when the
   campaign is up, steel when it's down (re-probed every 12 s).
3. **Checks all three update receipts** — queries the launcher, mod-suite, and co-op-client manifests
   when it opens without downloading payloads. Each installed and available version stays visible.
4. **Prepares only when asked** — when any component is older or missing, **PREPARE YOUR ARMY**
   downloads the SHA-256-verified updates, exact-replaces their `Modules\` payloads, and applies the
   launcher first with rollback and a one-time continuation after restart.
5. **Fails closed when versions cannot be verified** — an unreachable, timed-out, missing, or invalid
   required feed shows **THE COURT JESTER IS ASLEEP** and blocks joining until a retry succeeds.
6. **Launches + auto-joins** — **MARCH TO WAR** appears only after every component is verified current.
   The launcher shows a masked join-password field, then runs:
   ```
   Bannerlord.exe /singleplayer <moduleToken> /coopjoin <serverHost> <serverPort> <serverPassword>
   ```
   The `/coopjoin` arg is read by our `CoopMod` at the main menu, which publishes the same
   `AttemptJoin` the in-game Join button does. See `source/Coop/CoopMod.cs` → `TryParseCoopJoin` /
   `TryCoopJoin`.

## The `/coopjoin` boot arg (client mod side)

`/coopjoin <host> <port> [password]` — parsed in `CoopMod.NoHarmonyInit`, fired once at
`InitialState`. Guarded off for servers and managed-host processes. `host` may be an IPv4 literal or
DNS name; the password is optional and never logged. This ships inside the client `Coop.dll` — build
it with the **Serilog 4.x** flip (see below), same as `GameInterface.dll`.

## Config — `launcher-config.json` (ships beside the .exe, edit without rebuilding)

| key | meaning |
|-----|---------|
| `groupName` | launcher display title |
| `serverHost` / `serverPort` | co-op host to join + probe (`205.209.116.114` / `4200`) |
| `serverPassword` | optional default for the masked join-password field; blank in the public release |
| `moduleToken` | `/singleplayer` launch order — full set minus RBM, in handshake order |
| `gamePath` | explicit install root, or empty to auto-detect |
| `launcherManifestUrl` | required launcher executable feed; stable by default |
| `updateManifestUrl` | required co-op-client build-feed JSON URL |
| `suiteManifestUrl` | required byte-exact framework/workshop-mod suite feed |

## Build & distribute

```sh
# dev build
"C:\Program Files\dotnet\dotnet.exe" build tools/CoopLauncher/CoopLauncher.csproj -c Release

# distributable: one self-contained .exe (bundles .NET 8 + WPF — friends need no runtime install)
"C:\Program Files\dotnet\dotnet.exe" publish tools/CoopLauncher/CoopLauncher.csproj -c Release
# → tools/CoopLauncher/bin/Release/net8.0-windows/win-x64/publish/CalradiaCoop.exe (+ launcher-config.json)
```

Hand a friend `CalradiaCoop.exe` plus `launcher-config.json` in the same folder. They can enter the
private password at launch; the UI does not save it or log it. Never commit or attach a populated
`serverPassword` to a public release. Launchers downloaded before self-update was added need this one
manual replacement; after that, **PREPARE YOUR ARMY** can replace the executable while preserving the
adjacent config. Opening the launcher checks versions but does not install anything without that click.

Design review render (no window shown, no focus steal): `CalradiaCoop.exe --shoot preview.png`.

## Update-feed contract (for the P6 GitHub-sync build feed)

`launcherManifestUrl` uses a separate executable manifest:

```json
{
  "version": "2026.8.11.1",
  "launcherUrl": "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/CalradiaCoop.exe",
  "sha256": "<lowercase hex of the executable>",
  "notes": "Launcher source <commit>"
}
```

The stable feed is `launcher-app/launcher.json`. Development pushes publish only to
`launcher-nightly/launcher.json`; opt in by changing that segment in the local config. Stable publishing
is manual. Launcher replacement never writes `launcher-config.json`, logs, passwords, or unrelated files.

The mod/client `updateManifestUrl` contract is:

`updateManifestUrl` must return this JSON:

```json
{
  "version": "2026.08.10.1",
  "clientZipUrl": "https://…/coop-client-2026.08.10.1.zip",
  "sha256": "<lowercase hex of the zip>",
  "notes": "one-line changelog"
}
```

- `version` — dotted-numeric, monotonic. Newer-than-installed triggers a pull (compared against
  `Modules\Coop\installed-version.txt`).
- `clientZipUrl` — the client `Modules\` payload (the zip's root entries are `Coop\…`, extracted over
  the install; zip-slip guarded).
- `sha256` — mandatory 64-digit SHA-256, verified before anything is written.

The launcher extracts each verified zip into a private staging directory on the same volume, then
exact-replaces the zip's top-level module directories. Stale files are removed by replacement, and
transient access/sharing locks from filesystem scanners are retried for up to five seconds. Any
non-transient move or version-write failure restores every previous module directory. A malformed manifest, an HTTP
error from a reached feed, integrity failure, unsafe archive path, or failed install disables **MARCH TO
WAR** until the required update succeeds. A genuinely unreachable feed also blocks: the launcher will
not claim the installed files match the server when GitHub cannot confirm their required versions.

## Member update flow

1. Opening the launcher shows `Checking…` for **Launcher**, **Mod suite**, and **Co-op client**.
2. If all three receipts match, their versions show `Current` and the primary action becomes
   **MARCH TO WAR**.
3. If any required version is newer or absent, the row shows installed → available and the primary
   action becomes **PREPARE YOUR ARMY**. Nothing downloads before that click.
4. Preparation re-queries all manifests, applies the launcher first when needed, resumes the same
   approved transaction after restart, then installs the suite and client in that order. Once the
   final receipts are current, the same window immediately enables **MARCH TO WAR**.
5. A final manifest query and local receipt read must confirm all three components current before
   **MARCH TO WAR** is enabled.
6. If GitHub or any required feed cannot be verified, **TRY THE JESTER AGAIN** performs only a fresh
   manifest check; it never launches Bannerlord.

These operations touch only the portable launcher and Bannerlord's `Modules` installation. They do
not create or migrate a campaign save and do not change the game server or its selected save.
