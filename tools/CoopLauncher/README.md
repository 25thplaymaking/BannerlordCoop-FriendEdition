# Calradia Co-op Launcher (Frontir)

A one-click, Bannerlord-themed launcher that loads the co-op mod set and rides straight to the
group host — no module list, no manual connect. Replaces `Desktop\Play Friend Edition.cmd`.

## What it does

1. **Finds Bannerlord** — explicit `gamePath`, else auto-detects from every Steam library
   (`libraryfolders.vdf`, app `261550`).
2. **Shows host status** — TCP-probes `serverHost:serverPort`; the banner sigil glows gold when the
   campaign is up, steel when it's down (re-probed every 12 s).
3. **Self-updates the client build** — if `updateManifestUrl` is set, pulls a SHA-256-verified zip
   and extracts it into `Modules\`. Fails soft: an unreachable feed just launches the installed build.
4. **Launches + auto-joins** — the launcher shows a masked join-password field, then runs:
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
| `updateManifestUrl` | build-feed JSON URL, or empty to disable updates |

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
`serverPassword` to a public release.

Design review render (no window shown, no focus steal): `CalradiaCoop.exe --shoot preview.png`.

## Update-feed contract (for the P6 GitHub-sync build feed)

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
any move/version-write failure restores every previous module directory. A malformed manifest, an HTTP
error from a reached feed, integrity failure, unsafe archive path, or failed install disables **MARCH TO
WAR** until the required update succeeds; a genuinely unreachable feed still permits the already-installed
build.

Wire this to the nightly build output when P6 lands; until then leave `updateManifestUrl` empty.
