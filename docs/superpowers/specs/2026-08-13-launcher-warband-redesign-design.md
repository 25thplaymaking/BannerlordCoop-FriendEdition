# Calradia Co-op Launcher — "Warband" Redesign (approved 2026-08-13)

Bryce approved this design in-session; it is the authoritative scope for the launcher overhaul.

## Goal

Turn the compact single-plaque launcher into a maximal Bannerlord-themed pre-game launcher with
three sections, while keeping every existing behavior (armory check, prepare, self-update, launch,
fail-closed jester) byte-for-byte intact underneath.

## Shell & navigation

- Borderless window grows to **1280×800 default, resizable, min 1100×700**.
- Title bar merges with a top tab strip: `MUSTER │ CHRONICLE │ OPTIONS` — etched tabs, gold
  underline on the active tab. Frontir wordmark left, min/close right.
- One content pane below swaps per tab; Muster is the default.

## Visual identity

- Full-bleed layered backdrop, all **original vector art in XAML** (no TaleWorlds assets):
  dusk-sky gradient → mountain silhouettes → war-camp foreground (tents, spear rack, the
  swallowtail banner enlarged as hero element) → slow ember particle drift (capped count,
  storyboard-driven, skipped when `SystemParameters.ClientAreaAnimation` is off).
- Palette/typography unchanged: Frontir ink / gold / leather / blood, Constantia display,
  Consolas dispatch. Backdrop swappable later for painted art without code changes.

## Muster (default tab)

Existing functionality recomposed over the art: host sigil + status dispatch, three-receipt armory
ledger, join password, MARCH TO WAR / PREPARE YOUR ARMY, update rail. No behavior changes; all
control names preserved so the code-behind and `--shoot` design-review mode port unchanged.

## Chronicle

- **Curated feed**: `chronicle/changelog.json` in this repo, published by `launcher-release.yml`
  as an asset beside `launcher.json` on the same release tags. Entries: version, date, title,
  plain friend-readable highlight bullets.
- **Build-note accumulation**: the one-line `notes` from the three manifests are recorded locally
  (`%LocalAppData%\CalradiaCoop\chronicle-history.json`, capped) each armory check, so every build
  appears even between curated write-ups.
- Fetched via the existing `FeedFetch` retry/backoff; cached locally; renders offline.
- URL derived from the client feed URL (`launcher.json` → `changelog.json`); `ChronicleUrl`
  config override available.

## Options

- **Game path override** — browse to the install + "re-detect from Steam" reset.
- **Update channel** per component (launcher / suite / client), stable ↔ nightly, with a
  "nightly can break joins" warning. Rewrites only the known feed tokens
  (`launcher-app`↔`launcher-nightly`, `client-stable`↔`client-nightly`,
  `suite-stable`↔`suite-nightly`); custom URLs are never mangled.
- **Remember join password** — opt-in, DPAPI-encrypted per Windows user
  (`System.Security.Cryptography.ProtectedData`); default remains never-persisted.
- **Launch behavior + logs** — close-after-launch toggle (default on = current behavior),
  verbose logging toggle, Gather Logs relocated here plus an open-logs-folder shortcut.
- **GitHub fork link** button (`ProjectUrl` in config).
- Persistence: **new** `%LocalAppData%\CalradiaCoop\launcher-settings.json` — never
  `launcher-config.json`, which ships beside the exe and is replaced by updates. Precedence:
  user settings > shipped config.

## Testing & release

- Extend `CoopLauncher.Tests` (runs on windows-latest in `launcher-app-release.yml`): settings
  round-trip incl. DPAPI, channel URL rewrite matrix, chronicle parse/merge ordering,
  corrupted-file resilience.
- UI verified by offscreen `--shoot` render — no window is ever shown during verification.
- `check-xaml-resources.py` stays green.
- Release flow unchanged; authoring a `chronicle/changelog.json` entry becomes a deploy-runbook
  step. `launcher-release.yml` additionally uploads `changelog.json` beside `launcher.json`.

## Non-goals

- No TaleWorlds art assets, ever.
- No player-count probe (the server exposes no count on the probe port).
- No changes to update/verify/launch semantics.
