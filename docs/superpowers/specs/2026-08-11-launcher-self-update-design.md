# Launcher Startup Self-Update Design

## Decision

The portable Calradia Co-op launcher will update its own executable from a GitHub Release feed when
it opens, before it updates the mod suite or co-op client. The implementation will keep the existing
single-file distribution and will not introduce an installer framework or update `launcher-config.json`.

The stable launcher feed is the default. A separate manifest URL in the local configuration permits
nightly opt-in without changing the executable. Updates are published release assets built from Git;
the launcher will not execute or copy raw repository content.

## Startup flow

1. Load the local configuration and resolve the current executable path and build version.
2. Fetch the configured launcher manifest with a bounded timeout.
3. If the manifest version is newer, download its Windows executable into a staging directory beside
   the installed launcher and verify its SHA-256 before execution.
4. Start the verified staged executable in an internal apply-update mode and close the old process.
5. The staged process waits for the old process to exit, backs up the installed executable, replaces
   it, and launches the installed path in post-update mode.
6. The relaunched executable confirms normal startup, removes the staging files and backup, then runs
   the existing suite/client update checks and enables Join.

If no newer launcher exists, startup continues directly to the existing suite/client update checks.
Only one self-update/restart is allowed in a launch chain so malformed feed state cannot create a loop.

## Feed contract

The release workflow publishes `CalradiaCoop.exe`, the existing distribution zip, and a new
`launcher.json` containing:

- a monotonic launcher version;
- the direct executable asset URL;
- the lowercase SHA-256 of that executable;
- optional release notes.

The build embeds the same version in the executable. Stable configuration points at the rolling
`launcher-app` manifest. Nightly is opt-in through a distinct configured manifest URL and rolling
nightly release. Existing configs that lack the new property inherit the stable URL.

Publishing must generate the manifest from the exact built asset and upload both together. The release
safety check will verify that the workflow emits the version, hash, executable, and manifest and does
not publish a private server password.

## Replacement and rollback

Windows cannot overwrite a running executable, so replacement is performed by the already verified
staged copy. Paths passed to apply mode must be absolute and must resolve to the expected installed and
staging locations; arbitrary target paths are rejected.

Replacement uses a same-directory temporary file and backup so the final move is atomic on the target
volume. If copying or replacing fails, the backup is restored and the installed launcher is relaunched.
The local configuration, logs, and any unrelated neighboring files are never moved or overwritten.
Stale staging files from an interrupted attempt are cleaned on a later successful startup.

## Failure behavior

- A true offline or timeout result records a warning and continues with the installed launcher.
- A reached server returning an invalid manifest, a malformed version, a bad hash, or an unsafe URL or
  path blocks Join and shows the precise update error.
- A download or apply failure retains or restores the installed executable and gives a retryable error.
- The updater must never execute an unverified payload or silently downgrade.
- Existing suite/client fail-closed behavior remains unchanged.

## Boundaries

This work adds launcher self-update, release-manifest generation, stable/nightly feed configuration,
and focused tests. It does not add a general installer, background service, code-signing platform,
delta patching, UI redesign, or automatic changes to the user's server/password settings.

## Verification

Tests will cover manifest parsing, newer/equal/older version decisions, offline continuation, reached
HTTP and malformed-manifest failures, SHA-256 rejection, unsafe path rejection, successful replacement,
rollback, relaunch arguments, loop prevention, configuration preservation, and cleanup. The existing
launcher updater tests and XAML resource check must continue to pass.

The release workflow will be exercised to produce a candidate manifest and executable, whose recorded
hash must match the asset. A local end-to-end harness will install an older launcher into a temporary
directory, serve a newer manifest and executable, start it headlessly in update mode, and prove that
the installed executable advances while the adjacent configuration remains byte-identical.
