# Launcher version, campaign roster, and issue reporting

## Delivered behavior

- The launcher discovers every valid local Bannerlord installation, reads the version declared by
  `Modules/Native/SubModule.xml`, shows the Steam build id when available, and remembers the selected
  root. The stable client feed declares the host-required game version. Join is blocked when the
  selected installation does not match it.
- The Roster tab reads a host-authoritative snapshot of the existing co-op save. It lists every lord
  and supports player/AI, status, free-text, and stat-order filters. It shows clan, realm, activity,
  level, gold, renown, influence, party size, fiefs, and online state. No leaderboard database or
  gameplay mutation was added.
- The Options report form creates a GitHub issue through the campaign portal. It supports bugs and
  requests, limits input size and request rate, deduplicates repeated submissions for 24 hours, and
  redacts credential-shaped values and Windows account paths in log excerpts on both client and edge.
- After an abnormal game exit, the existing local crash collector reopens the launcher. The launcher
  scans its existing local crash-report folder and asks before a crash bundle or player-selected images
  are packaged or uploaded.

## Deployment contract

The portal handler stays Worker-compatible, but production runs as a user-level Node 24 service on
the co-op host and is exposed only through the dedicated Cloudflare Tunnel at
`https://calradia-coop.frontir.solutions`. Configure these repository secrets:

- `PORTAL_SERVER_HOST`: SSH hostname or address.
- `PORTAL_SERVER_USER`: restricted deployment user.
- `PORTAL_SERVER_SSH_KEY`: dedicated private deployment key.
- `PORTAL_SERVER_HOST_KEY`: pinned `known_hosts` entry for the host.

Run **Campaign Portal Deploy**. It tests the handler, deploys an immutable release over SSH, restarts
the service, verifies `/health`, and publishes `portal.json` to the rolling `portal-config` GitHub
release. The host stores `GITHUB_TOKEN` and `PUBLISH_TOKEN` in its mode-`0600` service environment
file; neither credential is sent to the launcher or committed. The Node service also accepts an
optional `REPORT_ATTACHMENT_PATH` environment variable. If omitted, private diagnostic packages are
stored under `report-attachments` beside `PORTAL_DATA_PATH`; the directory must be durable, mode 0700,
and retained for no more than 30 days by the host's normal cleanup policy.

The checked-in user service units under `services/coop-portal/deploy` run the portal on loopback port
4211 and its dedicated tunnel. `portal.env` and the tunnel credential/config remain host-only.

Set these environment variables in the dedicated server service and restart it:

```text
COOP_PORTAL_URL=https://calradia-coop.frontir.solutions
COOP_PORTAL_PUBLISH_TOKEN=<same value as PORTAL_PUBLISH_TOKEN>
```

The server publishes immediately after campaign start and every minute thereafter. `GET /stats`
should then return the current snapshot; no save migration is needed.

`POST /report-assets/<report-id>` is implemented by the Node service, not the Worker handler. It
accepts only a single `application/zip` package up to 120 MiB, authenticated by the opaque
15-minute `X-Report-Token` returned with a newly-created report. Packages are streamed directly to
the private attachment directory and are never exposed as GitHub attachments or public URLs.

## Intentionally excluded

- The launcher selects among installed game versions; it does not automate Steam account login or
  download arbitrary depots.
- Lifetime kills, deaths, wins, and hours are not displayed because the current co-op save does not
  persist those values per controller. Adding invented or partial history would make the leaderboard
  misleading. Those metrics can be added prospectively if explicit event tracking is requested.
