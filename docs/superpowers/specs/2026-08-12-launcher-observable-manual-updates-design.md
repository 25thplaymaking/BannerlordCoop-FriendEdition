# Launcher Observable Manual Updates Design

## Decision

The Calradia Co-op launcher will automatically query the launcher, mod-suite, and co-op-client
manifests at startup, but it will not download or install a payload until the member explicitly
presses the primary update action. Each component's installed and available version will remain
visible so a member can tell whether the launcher checked, found an update, or completed one.

The primary action is adaptive. It reads **PREPARE YOUR ARMY** while a known update is pending and
performs the complete verified update sequence. It reads **MARCH TO WAR** only after every required
component has been confirmed current. An unreachable, invalid, or missing required feed is an
unverified state and blocks both updating and joining until verification succeeds.

This design supersedes the automatic-install and offline-continuation policy in
`2026-08-11-launcher-self-update-design.md`. The existing verified staging, atomic replacement,
rollback, and stable/nightly feed contracts remain in force.

## Components and version display

The launcher treats these as three independently visible required components:

1. **Launcher** — the running portable executable, compared with `launcherManifestUrl`.
2. **Mod suite** — the framework and workshop-module receipt, compared with `suiteManifestUrl`.
3. **Co-op client** — Coop's client assemblies, compared with `updateManifestUrl`.

Each row shows one concise state and enough version information to explain it:

- `Checking…`
- `Current — 2026.08.12.0218`
- `Update available — 2026.08.12.0218 → 2026.08.13.0042`
- `Updating to 2026.08.13.0042…`
- `Could not verify`
- `Not installed` when no local receipt exists

Version labels must name their component. A single combined `build` label is not used because it can
mistake a current mod-suite receipt for proof that the launcher and co-op client are also current.
Optional release notes may appear beneath an update row, but they do not replace the version status.

## UI states and actions

The existing update rail becomes a compact three-row armory status panel. It keeps the current visual
language and progress rail rather than adding a modal installer. The existing primary button changes
purpose according to the verified aggregate state:

| Aggregate state | Primary label | Enabled | Action |
| --- | --- | --- | --- |
| Checking manifests | `CHECKING THE ARMORY…` | No | None |
| One or more known updates | `PREPARE YOUR ARMY` | Yes | Install all pending required updates |
| Installing | `PREPARING YOUR ARMY…` | No | None |
| Every component verified current | `MARCH TO WAR` | Yes | Launch and join as today |
| Feed unavailable or unverifiable | `TRY THE JESTER AGAIN` | Yes | Re-run all manifest checks only |
| Install or integrity failure | `TRY PREPARING AGAIN` | Yes | Re-check, then retry the safe update sequence |

When any required feed cannot be verified, the armory panel presents a prominent blocking state:

> **THE COURT JESTER IS ASLEEP**
>
> The royal update scrolls cannot be reached. Wake the jester and try again.

The failure detail identifies the affected component without exposing raw exception text. Pressing
**TRY THE JESTER AGAIN** performs a fresh, cache-bypassing query. It never launches the game.

## Check and update flow

Startup follows this sequence:

1. Load configuration, determine the running launcher version, locate Bannerlord, and read the local
   mod-suite and co-op-client version receipts.
2. Query all three configured manifests. Manifest checks may run concurrently, have bounded timeouts,
   and bypass stale HTTP caches. No executable or zip payload is fetched during this phase.
3. Validate every manifest's version, HTTPS asset URL, SHA-256, and required fields.
4. Render all three component results and derive the aggregate primary-button state.
5. Permit **MARCH TO WAR** only when all three results are verified current.

Pressing **PREPARE YOUR ARMY** provides consent for one complete update transaction:

1. Re-check all manifests so the versions and hashes cannot go stale between display and download.
2. Install a pending launcher update first using the existing verified staging and rollback mechanism.
3. Restart into the installed launcher and carry a one-time `continue preparation` intent. This is a
   continuation of the member's button press, not an automatic startup update.
4. Re-check all manifests after restart, then install pending mod-suite and co-op-client payloads in
   dependency order.
5. Re-query and re-read the installed receipts. Show **MARCH TO WAR** only when every component is
   confirmed current.

If the launcher itself is already current, the same button press proceeds directly to the suite and
client updates. If any stage fails, later stages do not run and the installed files are retained or
rolled back according to the existing transactional rules.

## Verification and failure policy

The production launcher requires all three feed URLs. An empty required URL, HTTP error, timeout,
unreachable host, malformed JSON, invalid version, unsafe asset URL, or missing hash produces an
unverified aggregate state. Joining is fail-closed in every such case, including a total GitHub
outage. The launcher does not infer currency from dates, release notes, or another component's build.

Known older versions also block joining until updated. Bad hashes, unsafe archive contents, failed
replacement, and post-install version mismatches retain or restore the previous installation and
offer a retry. The launcher never silently downgrades and never executes an unverified executable.

The UI logs technical diagnostics to the existing launcher log while showing members short,
actionable copy. Concurrent button presses and timer callbacks are ignored while a check, install,
restart, or game launch is active.

## Save and server boundaries

Update checks and installs operate only on the launcher executable, its temporary staging files, and
the configured Bannerlord `Modules` installation. They do not start Bannerlord, create a campaign,
open or rewrite a save, alter the server's save-selection setting, or deploy server files. The server
and its existing save remain untouched by this launcher feature.

## Implementation boundaries

The update services will separate query/planning from payload installation instead of using the
current combined check-and-install methods. A component result model will carry the installed version,
available version, notes, verification state, and whether an update is required. A small aggregate
state reducer will determine the primary action without embedding version-policy decisions in XAML
event handlers.

This work includes the observable status panel, adaptive primary action, one-click continuation across
a launcher restart, strict verification gate, logging, tests, launcher documentation, and publication
of the resulting portable launcher build. It does not introduce background services, delta patching,
an installer framework, automatic game launching, server deployment, or save migration.

## Verification plan

Automated tests will prove:

- startup queries manifests without downloading payload assets;
- launcher, suite, and client versions remain independently visible;
- equal/newer/older and missing local-version cases derive the correct component state;
- any offline, timeout, HTTP, disabled, or malformed required feed blocks joining;
- known updates select **PREPARE YOUR ARMY**, while all-current selects **MARCH TO WAR**;
- pressing the update action re-checks before downloading and installs in launcher/suite/client order;
- the one-time continuation survives launcher replacement without creating an update loop;
- integrity, archive, install, and post-install verification failures retain the previous installation;
- retry actions query again but never launch the game;
- repeated clicks cannot overlap checks or updates;
- no update path accesses campaign-save locations or changes server configuration.

Existing updater transaction, rollback, launcher apply-mode, game-launch, configuration, and XAML
resource tests must remain green. A release candidate will also be exercised against controlled local
manifests for all-current, update-available, unavailable-feed, successful restart continuation, and
bad-hash states before the launcher feed is published.
