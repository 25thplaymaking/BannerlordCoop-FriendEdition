# Launcher version, campaign roster, and issue reporting

## Delivered behavior

- The launcher discovers every valid local Bannerlord installation, reads the version declared by
  `Modules/Native/SubModule.xml`, shows the Steam build id when available, and remembers the selected
  root. The stable client feed declares the host-required game version. Join is blocked when the
  selected installation does not match it.
- The Roster tab reads a host-authoritative snapshot of the existing co-op save. It ranks characters
  by renown and shows level, gold, influence, clan tier, party size, fiefs, and online state. No
  leaderboard database or gameplay mutation was added.
- The Options report form creates a GitHub issue through the campaign portal. It supports bugs and
  requests, limits input size and request rate, deduplicates repeated submissions for 24 hours, and
  redacts credential-shaped values and Windows account paths in log excerpts on both client and edge.

## Deployment contract

The Worker deploy is intentionally credential-gated. Configure these repository secrets:

- `CLOUDFLARE_API_TOKEN`: Worker/KV edit token for the target Cloudflare account.
- `CLOUDFLARE_ACCOUNT_ID`: target account id.
- `PORTAL_GITHUB_TOKEN`: fine-grained token scoped only to Issues: write on
  `25thplaymaking/BannerlordCoop-FriendEdition`.
- `PORTAL_PUBLISH_TOKEN`: random high-entropy value shared only with the dedicated server.

Run **Campaign Portal Deploy**. Wrangler provisions the KV namespace, deploys the Worker, installs
the two runtime secrets, and publishes `portal.json` to the rolling `portal-config` GitHub release.
Launchers discover the assigned `workers.dev` URL from that document; it is not hard-coded.

Set these environment variables in the dedicated server service and restart it:

```text
COOP_PORTAL_URL=https://<deployed-worker>.workers.dev
COOP_PORTAL_PUBLISH_TOKEN=<same value as PORTAL_PUBLISH_TOKEN>
```

The server publishes immediately after campaign start and every minute thereafter. `GET /stats`
should then return the current snapshot; no save migration is needed.

## Intentionally excluded

- The launcher selects among installed game versions; it does not automate Steam account login or
  download arbitrary depots.
- Lifetime kills, deaths, wins, and hours are not displayed because the current co-op save does not
  persist those values per controller. Adding invented or partial history would make the leaderboard
  misleading. Those metrics can be added prospectively if explicit event tracking is requested.
