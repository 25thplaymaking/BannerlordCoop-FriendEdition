# Workshop suite distribution

## Decision record

```text
REQUIREMENTS
- Deliver the byte-exact 14-module ZIP through the launcher's existing suite.json contract.
- Accommodate a payload larger than GitHub's per-release-asset limit.
- Keep Workshop/source bytes out of CI and keep all credentials out of the repository and feed.
- Preserve SHA-256 verification, manual stable promotion, and a practical rollback path.
- Do not publish, deploy, or change the private suite builder as part of this work.

MINIMUM COMPONENTS NEEDED
- One immutable, content-addressed Coop-suite.zip in the existing public R2 bucket.
- One manually dispatched GitHub workflow that verifies the public object's byte length and publishes
  only suite.json to suite-nightly or suite-stable.
- One small feed generator with local tests, plus this operator runbook.

REJECTED/NEEDS CLARIFICATION BEFORE ACTION
- GitHub release payload: each asset must be under 2 GiB, so the new ZIP cannot be one asset there.
- Split GitHub assets: requires new launcher reassembly/state/error handling even though R2 accepts the
  complete object; it increases disk use and failure surfaces without meeting another requirement.
- CI package build: CI does not possess the licensed Workshop inputs and must not fetch them.
- Portal/remote-host file service: adds host bandwidth, credentials, deployment, and operations when
  the repository already has suitable public object storage.

PRIMARY RISKS
- Publication remains blocked until a maintainer uploads the locally verified artifact with a
  bucket-scoped R2 credential. The workflow has no credential and cannot upload or alter payload bytes.
- r2.dev is Cloudflare's managed public-development endpoint and is rate limited. It is adequate for
  this private group; configure a custom R2 domain before treating the feed as a broad public CDN.
- The launcher downloads one large file without cross-run resume. A failed transfer is retried by the
  player, while the installed suite remains untouched.
- Installation needs space for the ZIP on the Windows temporary-file volume and the expanded staging
  tree on the Bannerlord volume; an update also temporarily retains the old managed module folders.

OVERALL REQUEST AS INTERPRETED BY AGENT/CHAT
- Add the smallest safe promotion path around the existing launcher and public R2 storage, prove its
  metadata behavior locally, document the external upload and rollback, and leave release execution
  for an authorized maintainer with the artifact and credential.
```

## Why one R2 object

GitHub requires every individual release file to be under 2 GiB. Cloudflare R2 accepts multipart
objects up to 5 TiB, and Cloudflare documents `rclone` as a supported uploader that automatically
uses multipart transfers for large objects. The launcher already streams any HTTPS `clientZipUrl`
to disk, verifies the complete SHA-256, stages the ZIP, and exact-replaces its top-level modules.
Nothing in that feed contract requires the payload and manifest to share a host.

- GitHub release limit: <https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas>
- R2 multipart upload: <https://developers.cloudflare.com/r2/objects/upload-objects/>
- Cloudflare's rclone example: <https://developers.cloudflare.com/r2/examples/rclone/>
- r2.dev rate-limit warning: <https://developers.cloudflare.com/support/troubleshooting/http-status-codes/4xx-client-error/error-429/#r2-managed-public-buckets>

## Upload and promote

Build and validate the suite through the existing private builder. The resulting launcher payload
must be a ZIP whose root contains the module directories directly; do not upload the builder's outer
distribution ZIP if it adds a `Modules` wrapper or installer records.

Obtain an R2 S3 credential restricted to object read/write on
`bannerlordcoop-nightly-releases`. Put its values into the current shell's environment through the
team's secret manager; never paste values into this repository, a command transcript, or workflow
input. The variable names are the same ones already used by the R2 release jobs:

```text
R2_ACCOUNT_ID
RCLONE_CONFIG_R2_ACCESS_KEY_ID
RCLONE_CONFIG_R2_SECRET_ACCESS_KEY
```

In PowerShell, set the non-secret rclone configuration and calculate the immutable object key:

```powershell
$suitePath = 'D:\FriendEdition\Coop-suite.zip'
$suiteSha = (Get-FileHash -LiteralPath $suitePath -Algorithm SHA256).Hash.ToLowerInvariant()
$suiteBytes = (Get-Item -LiteralPath $suitePath).Length
$suiteKey = "suite/objects/$suiteSha/Coop-suite.zip"

$env:RCLONE_CONFIG_R2_TYPE = 's3'
$env:RCLONE_CONFIG_R2_PROVIDER = 'Cloudflare'
$env:RCLONE_CONFIG_R2_ENDPOINT = "https://$env:R2_ACCOUNT_ID.r2.cloudflarestorage.com"

rclone copyto $suitePath "r2:bannerlordcoop-nightly-releases/$suiteKey" `
  --s3-no-check-bucket --s3-chunk-size 256Mi --s3-upload-concurrency 4 `
  --retries 3 --low-level-retries 10 --progress

$remoteBytes = (rclone lsl "r2:bannerlordcoop-nightly-releases/$suiteKey").Split()[0]
if ($remoteBytes -ne [string]$suiteBytes) { throw "R2 byte length mismatch" }
```

Then dispatch the metadata-only workflow. Start with `nightly`; promote `stable` only after the
nightly launcher performs a rendered install/update/join successfully:

```powershell
gh workflow run suite-release.yml --ref development `
  -f channel=nightly `
  -f version=2026.08.16.1 `
  -f sha256=$suiteSha `
  -f bytes=$suiteBytes `
  -f notes='Bannerlord 1.4.8 Friend Edition 14-module suite'
```

The workflow derives the only allowed object URL from the SHA-256, checks that the public R2 object
exists with the declared byte length, and uploads only `suite.json` to the selected rolling GitHub
release. It does not receive an R2 credential or transfer the ZIP.

## Rollback and cleanup

Keep content-addressed objects immutable. A feed rollback must use a **new, higher dotted-numeric
version** pointing to the previous known-good object's URL and SHA-256; publishing an older version
does not update clients that already wrote a newer `coop-suite-version.txt`. Dispatch the workflow
with the previous object's SHA/length and the new version, first to nightly and then to stable.

Do not delete the previous object until every installed receipt that may need it has aged out. If a
manifest is wrong but no client installed it, republishing the prior object under a higher version is
still the deterministic recovery. The launcher verifies SHA-256 before touching installed modules
and rolls module directories back if installation itself fails.
