# Calradia Co-op Launcher expansion proposal

Status: proposal for review; no implementation is authorized by this document.

## Decision record

```text
REQUIREMENTS
- Replace the current shape-driven presentation with a distinct medieval campaign identity.
- Create an original Calradia Co-op launcher mark instead of using the Frontir wordmark as the product logo.
- Expand the roster from registered players to all campaign lords.
- Add search, filtering, sorting, and useful current-state information.
- Let players add images and correlated diagnostics to reports.
- Detect completed crash bundles and ask whether the player wants to report them.
- Evaluate optional background imagery, motion, and medieval music.
- Preserve updater, launch, version-selection, chronicle, and reporting behavior.
- Plan first; do not implement or publish changes yet.

MINIMUM COMPONENTS NEEDED
- The existing WPF launcher and its current updater services.
- The existing campaign stats publisher and portal.
- The existing Coop.CrashReporter process and crash bundle format.
- Original, licensed launcher artwork, logo, fonts, and optional audio.

REJECTED/NEEDS CLARIFICATION BEFORE ACTION
- A launcher rewrite in Electron, WebView, or another UI framework.
- A second crash watcher or resident Windows service.
- A separate leaderboard database for data already present in the campaign save.
- Copying launcher code, TaleWorlds art, faction marks, music, or competitor assets.
- Storefront, advertising, gifting, chat, or multi-game-library features.
- Publicly accessible crash dumps or attachment URLs.

PRIMARY RISKS
- Copyright and trademark restrictions around Bannerlord imagery, music, and faction marks.
- Large all-lord snapshots exceeding the portal's current 64 KiB request limit.
- Crash dumps containing private memory data.
- Decorative motion reducing readability or launcher performance.
- Historic action statistics being unavailable in existing saves.

REQUEST INTERPRETATION
- Produce a research-backed redesign and functionality plan, including the reported bug,
  while leaving product code and release output unchanged.

UNDERSTOOD: YES
```

## Outcome

Evolve the launcher into a **Calradian war-room companion**: it should still get a player into the
campaign quickly, but it should also answer three questions before launch:

1. Is my game ready and is the campaign reachable?
2. What is happening in Calradia and who is rising or falling?
3. If something failed, can I send the maintainers a useful, consented report?

The redesign should feel authored for this campaign, not like a generic dark dashboard decorated
with medieval colors.

## Current-state findings

### What is already strong

- The launcher is a self-contained .NET 8 WPF application with no heavy UI framework.
- It already provides launcher, mod-suite, and co-op-client update status.
- It discovers installed Bannerlord versions and blocks incompatible launches.
- The direct-join flow is already the launcher's primary action.
- Chronicle data is cached for offline viewing.
- The portal already publishes authoritative player data and creates GitHub issues.
- Report excerpts are bounded and redact credentials and Windows account paths.
- The game module already starts `Coop.CrashReporter.exe`, which monitors the Bannerlord process,
  locates a matching dump, preserves crash-time logs, and creates `shareable.zip`.
- Reduced Windows animation settings already disable the decorative ember animation.

These systems should be connected and extended rather than replaced.

### Observable defects and limits

1. **Roster crash:** `DataGridCheckBoxColumn` binds `IsChecked` to the getter-only
   `RankedCharacterStats.Online` property. The checkbox binding tries to use a writable mode, which
   causes the reported `TwoWay or OneWayToSource` exception. The binding must be explicit
   `Mode=OneWay`.
2. **Roster scope:** the host publisher enumerates only registered player controllers. It does not
   publish AI lords.
3. **Portal bounds:** stats publication accepts at most 100 player rows and the Node adapter rejects
   bodies over 64 KiB. An all-lord snapshot will exceed one or both limits.
4. **Report payload:** the launcher can submit only text plus a redacted log excerpt. There is no
   attachment protocol.
5. **Crash handoff:** crash bundles are generated, but the launcher does not discover them or guide
   the player through review and submission.
6. **Visual identity:** the current background is primarily flat XAML geometry. The large Frontir
   banner remains the strongest brand element, so the product reads as a themed utility rather than
   a Calradia Co-op application.
7. **Information density:** the main page has substantial dead space while the roster compresses
   many abbreviated columns into an undifferentiated grid.
8. **Accessibility:** several small steel labels are low contrast, visible keyboard focus is not
   consistently designed, and table/status information relies heavily on color and abbreviation.

## Comparable launcher review

| Launcher | Useful pattern | Calradia adaptation | Do not copy |
|---|---|---|---|
| RSI Launcher 2.0 | Real-time service status, environment selection, download management, per-environment settings, content and patch notes, direct issue reporting | Keep stable/nightly selection; add a clear campaign-health strip, component progress, and a guided report path | Its layout, graphics, copy, or component implementation |
| Battlestate Games Launcher | Strong game-first hero area, build/edition/server context beside Play, update/repair/cache/report tools kept near the launch action | Put campaign, selected game version, server state, update state, and repair/report entry points in the deployment rail | Promotional-commerce density and the Tarkov visual language |
| Battle.net | Full-page content, persistent status/download notifications, keyboard and screen-reader improvements, social presence | Use a consolidated notification center and expose online campaign members without building chat | Multi-game navigation, storefront, gifting, or social platform scope |
| Minecraft Launcher | Install/version management, one-click quick play, patch notes, profile and accessibility settings | Preserve direct join; make installed version selection and compatibility clearer; add an Accessibility section | Account/profile systems not needed by this private launcher |
| Rockstar Games Launcher | Clear verify-integrity and code-redemption utilities | Expose an explicit **Inspect and repair co-op files** action; defer redemption unless campaign access codes become a real requirement | Store and activation infrastructure |

The common lesson is not “add more tabs.” Good launchers keep **Play, readiness, environment,
downloads, status, and recovery** close together, while secondary content remains available without
blocking launch.

### Recommended feature adoption

Adopt:

- campaign/server status with last successful snapshot time;
- explicit component update progress, retry, and repair;
- environment/channel selection already supported by the launcher;
- patch notes and campaign dispatches;
- contextual issue reporting and crash recovery;
- searchable online/player presence;
- accessible keyboard navigation and notifications.

Defer unless separately requested:

- account profiles and authentication;
- chat, groups, friends, or voice;
- storefront and promotions;
- code redemption;
- multi-game navigation;
- a full download queue when only three managed components exist.

## Product structure

Keep four primary destinations, but rename and reshape them around campaign tasks:

1. **Muster** — readiness, campaign status, join password, selected installation, update progress,
   repair, and the primary launch action.
2. **Chronicle** — release notes plus campaign events once the prospective activity journal exists.
3. **Lords** — all-lord roster, rankings, filters, comparison, and detail view.
4. **Steward** — installation, channels, accessibility, audio, logs, normal reports, and pending
   crash reports.

“Options” becomes “Steward” only if the label tests clearly with users; otherwise retain Options and
use Steward as the visual section title. Familiarity is more important than lore vocabulary.

## Visual and brand direction

### Recommended concept: The Concord Standard

Create an original swallowtail standard representing multiple warbands joining one campaign:

- two opposing shield arcs form a linked `CC` monogram;
- a central road or river line joins them, representing the shared campaign;
- six small stitched divisions reference Calradia's cultures through material and color, not copied
  faction emblems;
- the silhouette remains recognizable at 16, 32, 48, and 256 pixels;
- the full lockup reads **Calradia Co-op**, with a small optional “by Frontir” credit rather than
  Frontir serving as the product name.

Alternative concepts for an initial three-mark exploration:

- **Calradic Seal:** a campaign-map compass with two mounted figures circling toward the same keep.
- **Twin Pennants:** two distinct banners crossing behind a simple round shield.

Do not trace the Bannerlord logo, culture emblems, launcher art, or another mod's mark.

### Art direction

Use a restrained “painted war room” system:

- one original 16:9 master painting with page-specific crops or lighting treatments;
- layered parchment, worn wood, cloth, and forged-metal edge textures where controls physically
  need a surface;
- functional areas treated as campaign artifacts: muster order, chronicle folio, lord ledger, and
  steward's report desk;
- readable content on calm tonal fields rather than placing text directly over high-detail art;
- faction influence conveyed through weather, architecture, equipment silhouettes, cloth color,
  and landscape rather than copied crests.

The TaleWorlds press kit and game screenshots may be used as **mood-board reference**. Shipping any
of those assets requires an explicit license review or permission. The safest production route is
original commissioned or generated artwork that does not reproduce protected marks or characters.

### Motion

Prefer three to five original stills with slow opacity crossfades and minimal depth movement over
animated GIFs or continuous video. This is lighter, sharper, and natively reliable in WPF.

- Maximum one ambient moving layer plus small UI transitions per view.
- Pause when minimized or when Bannerlord launches.
- Respect `SystemParameters.ClientAreaAnimation` and add an explicit reduced-motion setting.
- Provide a static fallback and never block readiness or launch on media loading.

Video can remain a later option if still-image crossfades fail the desired quality bar. Adding a
video library solely for the background is not justified in the first implementation.

### Typography and color

- Use an embeddable, OFL-licensed display serif for titles, such as Crimson Pro or Cormorant
  Garamond, after testing at Windows display scaling.
- Use Atkinson Hyperlegible or another highly readable sans serif for controls and data.
- Keep a tabular/monospaced face only for versions, hashes, and diagnostics.
- Avoid blackletter for body text and avoid excessive all-caps tracking.
- Retain ink, oxblood, aged parchment, and brass, but add a distinct non-color icon and label for
  ready, warning, offline, and failed states.
- Verify 4.5:1 contrast for normal text and visible focus on every interactive control.

## Muster redesign

The Muster page should use a clear foreground hierarchy over the artwork:

1. **Campaign health strip:** host online/offline, campaign day, players online, last snapshot age,
   and required Bannerlord version.
2. **Deployment order:** selected installation and the three managed components, each with status,
   installed version, required version, and progress only when active.
3. **Primary action:** `MARCH TO WAR`, `PREPARE THE WARBAND`, `RETRY`, or `SELECT BANNERLORD 1.4.8`
   based on the existing armory state machine.
4. **Recovery actions:** inspect/repair files, open logs, or review a pending crash; shown only when
   relevant.

The Play action remains visually dominant. News, artwork, and leaderboard summaries must never
compete with a required repair or version mismatch.

## Lords and leaderboard

### Snapshot scope

Publish every relevant living campaign lord from the host's authoritative campaign object manager,
not just the player registry. Player registrations are joined onto those rows to determine
controller and connection state.

Recommended first-version fields:

```text
id                    stable Hero StringId
name
controller            player | ai
online                 true only for connected player-controlled heroes
clan
kingdom
culture
level
gold
clanRenown
clanInfluence
clanTier
partySize
fiefs
status                 active | prisoner | wounded | missing | dead (if dead rows are enabled)
currentAction          traveling | waiting | raiding | besieging | defending | in-army | unknown
location               settlement/region name when safely available
```

The exact `currentAction` mapping must be based on stable Bannerlord APIs and return `unknown`
rather than inventing activity.

### User experience

- Default view: all living lords ranked by clan renown, with player lords visually identified.
- Search as the user types across lord, clan, kingdom, and culture.
- Filter chips: All, Player Lords, AI Lords, Online, Prisoner, Has Party, Has Fief.
- Dropdown filters: kingdom and culture.
- Sortable columns including money, renown, influence, level, party size, and fiefs.
- Clear all filters and a useful no-results message.
- Row detail drawer for status, location, clan totals, controller type, and last-published time.
- Full keyboard navigation, accessible names for abbreviations, and text/icon status indicators.
- Persist only presentation preferences such as last sort and visible filters; never cache a second
  authoritative ranking.

Search, filter, and sort remain client-side. The all-lord snapshot is small enough for a private
campaign once portal limits are raised, and this avoids query endpoints and a leaderboard database.

### API evolution

Use a versioned snapshot contract:

```json
{
  "schemaVersion": 2,
  "updatedAt": "...",
  "gameVersion": "1.4.8",
  "campaignDay": 123,
  "onlinePlayers": 4,
  "lords": []
}
```

- Raise the publication body limit to a measured bound, initially 1 MiB.
- Bound `lords` to a defensible maximum such as 2,000 and validate every row.
- Add `ETag`/conditional GET support only if measurement shows repeated downloads matter.
- Keep the last good snapshot when a publish fails.
- Display stale-data age prominently instead of silently presenting old data as current.

### Historic actions

Lifetime kills, victories, raids, captures, and tournaments are not currently available as reliable
per-lord history. Do not infer or backfill them.

If detailed actions are approved after the snapshot roster ships, add a **prospective, bounded
campaign event journal** stored with the co-op save. Each event should contain campaign time, event
type, actor lord ID, optional target ID, and small typed metadata. Publish only the latest bounded
window and clearly label totals as “tracked since campaign build …”. This is a separate phase because
it touches gameplay event wiring and save persistence.

## Reports, images, and crash review

### Reuse the current crash collector

Do not create another process monitor. Extend the existing handoff:

1. The launcher passes its validated executable path through the Bannerlord child environment.
2. The game inherits those values and the existing crash reporter inherits them from the game.
3. After the existing crash reporter finishes `report.txt` and `shareable.zip`, it starts the
   launcher normally.
4. If relaunch fails, the launcher scans the known crash-report root at its next normal startup and
   offers any unreviewed bundle.
5. A small LocalAppData settings value records the submitted crash-bundle ID so it is not prompted
   again after a successful upload.

This satisfies immediate and next-launch recovery without changing the single-launcher deployment
model or keeping the main window open in the background.

### Crash-review flow

The prompt should say what was detected and ask for consent:

> Bannerlord ended unexpectedly. A diagnostic bundle was prepared at 18:42. Would you like to
> review and report it?

The review page contains:

- crash time, build, role, last recorded phase, process exit code, and dump availability;
- title, description, and reproduction steps;
- a preview of the redacted log excerpt;
- separately selectable **logs**, **crash dump**, and **images**;
- a clear warning that a dump may contain fragments of private process memory;
- `Submit`, `Not now`, and `Delete local bundle` actions;
- no upload until the player confirms.

Normal report creation should use the same page without crash-only fields.

### Image handling

- Use the Windows file picker and show removable thumbnails.
- Accept PNG, JPEG, and WebP only in the first version.
- Constrain image type and aggregate size; clearly disclose that selected image metadata is included
  until a future deliberate metadata-stripping pass is implemented.
- Never capture the screen automatically.
- Show filenames and final upload size before consent.

### Attachment transport and storage

The existing 64 KiB JSON report endpoint is appropriate for text, not dumps. Keep metadata and
binary transfer separate:

1. Create the issue through the existing report endpoint and return an opaque report ID plus a
   short-lived upload token.
2. Upload one opaque `shareable.zip` or image bundle through a raw binary endpoint. Stream it to a
   non-release data directory; do not parse or extract untrusted ZIP content on the server.
3. Verify declared length, maximum size, SHA-256, token, rate limit, and report ownership.
4. Store files with restrictive permissions and a defined retention period, initially 30 days.
5. Update the GitHub issue with attachment receipt, hash, size, and report ID—not a public download
   link.
6. Retrieval remains an administrator operation over the existing host until a real authenticated
   admin UI is requested.

This adds one route to the existing portal rather than a new storage service. Public object storage
or Sentry is unnecessary for the stated private workflow.

## Audio

Background music is optional ambience, not required launcher functionality.

Recommended policy:

- do not redistribute the Bannerlord soundtrack inside the launcher;
- use a small number of explicitly CC0 or properly licensed original tracks;
- retain a license record, source URL, author, downloaded file hash, and download date for every
  asset;
- default audio to off for existing users, expose mute and volume, remember the choice, and stop or
  fade when Bannerlord launches;
- avoid network streaming and play a local cached file so launch never depends on media hosting.

OpenGameArt has individually marked CC0 medieval tracks, and Pixabay permits incorporated use under
its content license but prohibits standalone redistribution and use as a trademark. CC0 assets with
clear provenance are preferable for this launcher. A commissioned original loop is the cleanest
long-term identity.

## Delivery phases

### Phase 0 — stability gate

- Fix the `Online` binding to `Mode=OneWay`.
- Add a roster-window regression test or offscreen render that opens the populated roster.
- Run the XAML resource checker, focused launcher tests, Release build, and all-panel shoot render.

Acceptance:

- Roster opens without an unhandled binding exception.
- Existing launch, updater, chronicle, options, and report behavior remains green.

### Phase 1 — identity and shell prototype

- Produce three original monochrome logo directions and test them at icon sizes.
- Produce one original master backdrop plus page crops; do not use protected art in the build.
- Establish typography, contrast, focus, spacing, surface, icon, and reduced-motion rules.
- Recompose Muster first while preserving every current control/state transition.
- Obtain explicit visual approval before converting the remaining pages.

Acceptance:

- The launcher is recognizable as Calradia Co-op without the Frontir wordmark.
- Play/readiness remains the strongest hierarchy at 1100×700 and 1280×800.
- Keyboard, 100/125/150% scaling, contrast, and reduced-motion checks pass.

### Phase 2 — all-lord roster

- Publish the versioned all-lord snapshot.
- Raise and test portal bounds.
- Implement search, filters, sorting, stale-state handling, and lord detail.
- Add publisher, portal validation, client parsing, sorting/filtering, and WPF interaction tests.

Acceptance:

- Every eligible campaign lord appears exactly once.
- Player and AI lords filter correctly.
- Money and other requested columns sort deterministically.
- Search covers lord, clan, kingdom, and culture.
- A stale or unavailable portal never crashes or displays misleading freshness.

### Phase 3 — guided reports and crash handoff

- Connect the existing reporter to launcher review mode.
- Add pending-bundle discovery and review-state persistence.
- Add image selection, re-encoding, preview, and removal.
- Add consented binary bundle upload and server retention controls.
- Reuse current redaction, rate limiting, deduplication, and GitHub issue creation.

Acceptance:

- Normal exit produces no crash prompt.
- Abnormal exit with or without a dump produces one reviewable pending report.
- Dismissed reports do not repeatedly prompt; deferred reports remain discoverable.
- Nothing uploads before explicit confirmation.
- Oversized, invalid, duplicate, and interrupted uploads fail safely.
- Reports correlate issue URL, report ID, build, session, logs, and optional attachments.

### Phase 4 — ambience and prospective activity

- Add optional still-image crossfades and a static/reduced-motion mode.
- Add opt-in licensed music with volume and mute.
- Only after separate approval, instrument and persist the bounded recent-action journal.

Acceptance:

- Media failure cannot delay readiness or launch.
- CPU and memory remain bounded when the launcher is idle or minimized.
- Every shipped asset has recorded provenance and license.
- Action history is labeled with its true tracking start and never presented as lifetime data.

## Verification boundary

Each implementation phase should finish with one consolidated launcher verification run:

1. focused unit tests for services and data contracts;
2. portal tests for validation, rate limits, redaction, and storage behavior;
3. `check-xaml-resources.py`;
4. Release build;
5. offscreen renders of every primary page at minimum and default window size;
6. keyboard/focus and 100/125/150% Windows scaling checks;
7. a live launch smoke test using the configured Bannerlord 1.4.8 installation;
8. phase-specific failure drills, especially stale portal data, interrupted update, abnormal game
   exit, missing dump, and rejected upload.

Do not publish a launcher release until the current repository completion gates and the applicable
phase acceptance criteria pass.

## Reference sources

- [RSI Launcher 2.0 announcement](https://robertsspaceindustries.com/en/comm-link/transmission/19818-Introducing-The-RSI-Launcher-20)
- [Battle.net front-end and accessibility update](https://news.blizzard.com/en-us/article/23583668/welcome-to-the-new-battle-net)
- [Minecraft launcher features](https://www.minecraft.net/download/)
- [Rockstar launcher file verification](https://support.rockstargames.com/articles/1LPrvXoriD2629aSKW2gnQ/how-to-verify-game-files-in-the-rockstar-games-launcher)
- [TaleWorlds press kit](https://www.taleworlds.com/en/press)
- [TaleWorlds Bannerlord mod terms](https://www.taleworlds.com/en/static/mtla)
- [Bannerlord soundtrack release](https://www.taleworlds.com/en/News/538)
- [Pixabay content license summary](https://pixabay.com/service/license-summary/)
- [Example OpenGameArt CC0 medieval track](https://opengameart.org/content/medieval-minstrel-dance)

Battlestate Games launcher observations in this proposal are limited to visible interface patterns
in contemporary screenshots because an authoritative public feature specification was not found.
Those references are for product analysis only, not asset or implementation reuse.
