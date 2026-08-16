# Workshop suite distribution

## Decision record

```text
REQUIREMENTS
- Deliver the byte-exact 14-module suite through the launcher and its stable/nightly suite.json feeds.
- Use no service that requires accepting new billing terms; neither signed-in Cloudflare account has R2 enabled.
- Keep every GitHub release asset below GitHub's 2 GiB per-file limit.
- Keep Workshop/source bytes out of CI and preserve SHA-256 verification, manual stable promotion,
  rollback, and the existing single-URL/R2 launcher contract.
- Do not publish, deploy, delete existing artifacts, or change the private suite builder in this work.

MINIMUM COMPONENTS NEEDED
- One optional parts array in the existing suite manifest. The launcher concatenates parts into the
  same temporary ZIP, verifies every part and the complete ZIP, then uses its existing transactional install.
- One local distribution helper that splits an already-built ZIP into immutable sub-2-GiB assets and
  writes the descriptor; it does not build or alter suite contents.
- One `suite-payloads` GitHub release that retains content-addressed parts/descriptors, plus the existing
  manual workflow to validate GitHub-recorded sizes/digests and publish only suite.json to a rolling feed.

REJECTED/NEEDS CLARIFICATION BEFORE ACTION
- Cloudflare R2: technically suitable, and the launcher keeps supporting it, but enabling either
  available account accepts billing terms and needs explicit user approval.
- GitHub Actions artifacts or a CI build: CI lacks the licensed Workshop inputs; routing 9+ GiB through
  Actions also adds temporary storage and transfer without improving the recipient outcome.
- Portal/remote-host file service: adds host bandwidth, credentials, deployment, and operations; the
  existing portal's attachment contract is only 120 MiB and is intentionally private.
- Git LFS or another storage vendor: adds billing/quota or a new service when GitHub Releases already
  permits up to 1,000 individually compliant assets and unlimited aggregate release size.

PRIMARY RISKS
- Existing launchers do not understand multipart suites. Publish the updated launcher to the applicable
  launcher channel and prove its self-update before publishing a multipart suite feed.
- A failed part download restarts preparation, but installed modules remain untouched. The design does
  not add persistent partial-download state.
- Installation still needs space for the reconstructed ZIP on the Windows temporary-file volume and the
  expanded staging tree on the Bannerlord volume; updates temporarily retain old module folders too.
- The local GitHub upload requires an authenticated maintainer with repository release-write access.

OVERALL REQUEST AS INTERPRETED BY AGENT/CHAT
- Implement the narrow no-new-billing fallback: GitHub-hosted immutable parts, a backward-compatible
  manifest extension, local splitting, metadata-only CI verification/promotion, tests, and exact runbook.
```

## Feed contract

The original single-object form remains valid for R2 or any other HTTPS host:

```json
{
  "version": "2026.08.16.1",
  "clientZipUrl": "https://example.invalid/Coop-suite.zip",
  "sha256": "<complete ZIP SHA-256>",
  "notes": "one line"
}
```

The no-new-billing GitHub form replaces `clientZipUrl` with ordered parts:

```json
{
  "version": "2026.08.16.1",
  "sha256": "<complete reconstructed ZIP SHA-256>",
  "notes": "one line",
  "parts": [
    {
      "url": "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/suite-payloads/Coop-suite.<digest>.part001",
      "bytes": 1992294400,
      "sha256": "<part SHA-256>"
    }
  ]
}
```

The two payload forms are mutually exclusive. Every part must use HTTPS, contain a positive byte count
strictly below 2 GiB, and carry an exact SHA-256. The launcher writes parts directly and sequentially
into one temporary ZIP, so it does not store a second set of part files. It rejects a wrong response
length or part digest before extraction, then verifies the complete ZIP through the unchanged hash gate.

GitHub documents that a release can have up to 1,000 assets, each file must be under 2 GiB, and the
aggregate release size and bandwidth are unlimited:
<https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas>.

## Prepare and upload immutable payload assets

Build and validate the suite through the existing private builder. The launcher payload must be a ZIP
whose root entries are module directories directly; do not split the outer builder distribution ZIP if
it adds a `Modules` wrapper or installer records.

Prepare 1,900 MiB parts locally. The helper hashes the existing ZIP, streams the split, and never changes
the source artifact:

```powershell
$suitePath = 'D:\FriendEdition\Coop-suite.zip'
$payloadDir = 'D:\FriendEdition\suite-github-payload'

python .github/scripts/prepare_github_suite_release.py `
  --archive $suitePath `
  --version 2026.08.16.1 `
  --notes 'Bannerlord 1.4.8 Friend Edition 14-module suite' `
  --repository 25thplaymaking/BannerlordCoop-FriendEdition `
  --output-dir $payloadDir
```

The helper prints the descriptor asset name and upload-list path used by the exact command below. Review its `upload-files.txt`,
`suite-<complete-sha256>-v<version>.json`, and part hashes. Ensure `gh auth status` names the intended account.

Create the payload release once if it does not exist, then upload all content-addressed parts and the
descriptor. Do not use `--clobber`: an existing same-name asset is evidence to inspect, not overwrite.

```powershell
gh release view suite-payloads --repo 25thplaymaking/BannerlordCoop-FriendEdition *> $null
if ($LASTEXITCODE -ne 0) {
  gh release create suite-payloads `
    --repo 25thplaymaking/BannerlordCoop-FriendEdition `
    --title 'Calradia Co-op immutable suite payloads' `
    --notes 'Content-addressed multipart suite payloads. Do not delete assets referenced by a feed.' `
    --latest=false
}

$uploadFiles = Get-Content -LiteralPath (Join-Path $payloadDir 'upload-files.txt')
gh release upload suite-payloads $uploadFiles `
  --repo 25thplaymaking/BannerlordCoop-FriendEdition
```

`gh release upload` accepts multiple files in one command. If an upload is interrupted, rerun it only
with missing filenames from `gh release view suite-payloads --json assets`; never clobber these files.

## Verify and promote the feed

Dispatch the metadata-only workflow with the descriptor filename printed by the helper:

```powershell
$descriptor = Get-ChildItem -LiteralPath $payloadDir -Filter 'suite-*.json' |
  Select-Object -ExpandProperty Name -First 1

gh workflow run suite-release.yml `
  --repo 25thplaymaking/BannerlordCoop-FriendEdition `
  --ref development `
  -f channel=nightly `
  -f descriptor=$descriptor
```

The workflow downloads only the small descriptor, validates its manifest shape, and compares every
declared part name, byte length, and SHA-256 against GitHub's release-asset metadata. It rejects missing,
wrong-path, oversized, or mismatched parts and a non-increasing feed version. It then uploads only
`suite.json` to `suite-nightly` or `suite-stable`; CI never receives ZIP or part bytes.

Acceptance order:

1. Publish the launcher containing multipart support to `launcher-nightly`.
2. Prove an old nightly launcher self-updates to it before reading the multipart suite feed.
3. Promote the descriptor to `suite-nightly`; perform a rendered install/update/join and verify receipts.
4. Publish the launcher to `launcher-app` before promoting the same or a newer descriptor to
   `suite-stable`. Stable must never point old launchers at an unreadable feed.

## Rollback and cleanup

Keep payload parts immutable. A rollback publishes a descriptor for the previous known-good payload
with a **new, higher dotted-numeric version**; publishing an older version does not update clients that
already wrote a newer `coop-suite-version.txt`. Generate the rollback descriptor against the retained
previous ZIP/parts, upload only any descriptor that is not already present, then dispatch nightly and
stable in the same acceptance order.

Do not delete an asset while any retained descriptor or installed population may reference it. The
launcher verifies part and complete hashes before touching installed modules and retains its existing
transactional module rollback if extraction or installation fails.
