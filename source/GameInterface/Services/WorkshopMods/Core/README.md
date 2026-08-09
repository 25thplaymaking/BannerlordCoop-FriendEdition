# Friend Edition Workshop compatibility contract

The runtime handshake expects the private suite builder to install this compact receipt at:

`Modules/Coop/WorkshopSuite/MANIFEST.json`

```json
{
  "schemaVersion": 1,
  "suiteId": "friend-edition-private-workshop-suite",
  "moduleCount": 11,
  "receiptSha256": "<lower-case SHA-256>",
  "modules": [
    {
      "moduleId": "RBM",
      "workshopId": "2859251492",
      "steamManifestId": "8508128689459287315",
      "version": "v4.3.4",
      "loadOrder": 100,
      "contentSha256": "<lower-case SHA-256>",
      "configurationSha256": "<lower-case SHA-256>"
    }
  ]
}
```

For each staged module, normalize relative paths to lower-case forward-slash form and exclude
`mod-config.json`, `coop-options.json`, `server-config.json`, and files ending in `.log`, `.pdb`,
`.md`, `.bak`, or `.tmp`. Configuration files are `.xml`, `.json`, `.config`, `.ini`, `.yaml`,
`.yml`, `.csv`, and `.txt`; every other included file is content.

For each class, sort files by normalized relative path and create lines in this exact form:

`path|decimal-byte-length|lower-file-sha256`

Join lines with LF and no trailing LF, then SHA-256 the UTF-8 bytes. An empty class is SHA-256 of
an empty byte sequence. The receipt digest uses module records sorted case-insensitively by module
ID, joined with LF and no trailing LF:

`lower-module-id|workshop-id|steam-manifest-id|version|load-order|content-sha256|configuration-sha256`

The receipt establishes that co-located `Modules/<id>` directories belong to the managed private
distribution; runtime hashing proves their installed bytes still match it. Numeric load order is
part of both the receipt and wire identity, while actual activation order is independently verified
from TaleWorlds' active-module enumeration. Package presence and feature activation are separate
claims. The current activation policy requires all eleven managed components to be active on both
client and server — the group runs the full modded experience, and the handshake verifies every
component's bytes each session. Either a missing required activation or an unexpected active
component is a hard handshake failure. Feature adapters remain fail-closed while their upstream
module is inactive.

Runtime preparation is deliberately split by thread affinity. TaleWorlds active-module and path
metadata is captured into an immutable snapshot on the game thread. Only deterministic filesystem
hashing of that snapshot runs on a worker. The server publishes only the resulting cached manifest;
a join arriving before warmup completes is rejected without blocking the network poller, and any
capture or hash failure is retained for the session so later joins fail closed instead of retrying
engine discovery or package hashing on a connection thread.
