# Friend Edition private Workshop suite builder

This builder composes the built Friend Edition `Coop` module with the exact Bannerlord Workshop subscriptions pinned in `deploy/workshop-mods.json`. Each permitted upstream component remains a managed, separate runtime module under `Modules/<module-id>`; the result is one installable Friend Edition distribution rather than a disconnected add-on archive.

It never deletes or writes into Steam Workshop content. Before staging, it hashes every source file. After staging and copy verification, it hashes every source module again and aborts if any source digest changed.

## Recommended use

Open PowerShell in the repository and validate first:

```powershell
.\tools\WorkshopIntegration\Build-PrivateWorkshopSuite.ps1 -ValidateOnly
```

See the complete copy/exclusion plan without staging package output or changing Workshop source:

```powershell
.\tools\WorkshopIntegration\Build-PrivateWorkshopSuite.ps1 -DryRun
```

`-ValidateOnly` and `-DryRun` can compile the repository's ignored PE metadata inspector under `AssemblyInspector/bin` and `AssemblyInspector/obj`. They do not create the suite/output archive and never write to Workshop source. Normal repository cleanup may remove that ignored build cache; it is regenerated when needed and is never shipped in the suite.

Stage the private three-person suite and create a distributable ZIP:

```powershell
.\tools\WorkshopIntegration\Build-PrivateWorkshopSuite.ps1 `
  -CoopModuleRoot 'D:\FriendEdition\build\Coop' `
  -OutputRoot 'D:\FriendEdition\WorkshopSuite' `
  -ArchivePath 'D:\FriendEdition\FriendEdition-WorkshopSuite.zip'
```

If Steam cannot be discovered, the script asks for a Steam library/workshop path. For unattended builds, use `-NonInteractive` and specify either `-SteamRoot` or `-WorkshopRoot`:

```powershell
.\tools\WorkshopIntegration\Build-PrivateWorkshopSuite.ps1 `
  -WorkshopRoot 'P:\SteamLibrary\steamapps\workshop' `
  -DryRun `
  -NonInteractive
```

Use `-Force` only to replace an existing directory created by this builder for the same suite. It refuses to replace an unmarked directory, another suite, any Workshop source path, a drive root, or a path that contains/is contained by Workshop source.

## Generated records

- `MANIFEST.json`: pinned Workshop manifests, module IDs/versions, source digests, file hashes, exclusions, credits, and discovered upstream license files.
- `Modules/Coop/WorkshopSuite/MANIFEST.json`: compact managed-suite receipt consumed by the Friend Edition client/server handshake. Because it lives inside `Coop`, a normal Modules-only install preserves it.
- `SHA256SUMS.txt`: hashes for every staged module file.
- `CREDITS.md`: creator and Workshop attribution.
- `PERMISSIONS.md`: the project owner's private-use permission attestation.
- `LOAD-ORDER.txt`: deterministic module ordering and placement guidance.
- `CLIENT-LOAD-ORDER.txt`: exact rendered-client activation order.
- `SERVER-ACTIVATION.txt`: guarded dedicated/headless activation policy.
- `Verify-ServerHarmony.ps1`, `Verify-ServerHarmony.py`, and `SERVER-HARMONY.json`: read-only Windows/Linux fail-before-launch validation of the selected nightly's build-time-audited Harmony wrapper/runtime identity, exact byte hashes, provider uniqueness, launch order, and the resolved runtime CoopData `difficulty.birthAndDeath` value. The final suite does not ship a custom metadata loader or runner.
- `Run-ClientSetup.cmd` and `Setup-ManagedSuiteClient.ps1`: guided Windows client installation. Setup discovers Steam libraries or accepts a pasted Bannerlord folder on any drive (including paths such as `G:\Steam\steamapps\common\Mount & Blade II Bannerlord`), validates `Modules\Native\SubModule.xml`, verifies every pinned suite file, backs up same-ID module folders and `LauncherData.xml`, installs each managed module separately, and selects only `activationPolicy.client.activeModuleOrder`.
- `INSTALL.txt`: concise role-aware installation instructions.

The exclusions remove only known embedded runtime duplicates from gameplay mods and Coop. This includes Coop's `0Harmony.dll`, all staged `TaleWorlds.*` and `SandBox*.dll` game/module copies, and noncanonical JSON/framework copies where exact reference closure proves another provider. Strong-named, different-version dependencies are retained only through explicit AssemblyName/token-aware side-by-side allowances. The builder does not edit `SubModule.xml`, and planning fails if an exclusion would remove a DLL declared by a module's own descriptor. Required dependencies, managed-module load order, full assembly identities, hashes, and dependency closure are validated before any staging write.

One framework payload is intentionally canonical to active Coop: `System.Numerics.Vectors` 4.1.5.0. Coop's active `System.Memory` has an exact strong-named 4.1.5.0 AssemblyRef, while the selected game has only 4.1.3.0 and staged-inactive ButterLib has 4.1.4.0. The builder pins Coop's exact path, size, SHA-256, full AssemblyName, and consumer references. ButterLib must remain staged-inactive; co-activation is forbidden until its framework/load-context E2E gate passes. This is recorded as a required provider proof, not a filename-based duplicate allowance.

Rendered clients stage the complete receipt-pinned suite but activate only entries marked `ACTIVE` in `CLIENT-LOAD-ORDER.txt`. Frameworks and original gameplay modules remain `STAGED-INACTIVE`/feature-blocked until their framework, authority, and E2E gates pass; staging is not a compatibility claim. The guided installer applies this active-only policy and deselects unrelated launcher entries rather than enabling everything it copies. It detects old/noncanonical same-ID module folders, copies them to a verified durable backup, then uses only same-volume renames under the game installation for the live swap and rollback; this remains safe when the game is on `G:` and Documents is on `C:`. The selected dedicated-server topology uses exactly `Bannerlord.Harmony, Native, SandBoxCore, CustomBattle, Sandbox, StoryMode, Coop`; production preflight rejects added/reordered modules, every `neverActivateModuleIds` and `guardedModuleIds` entry, and any stale Coop/other `0Harmony.dll`. The verifier is not a bootstrap and never installs, deletes, overwrites, or launches the server.

The optional TaleWorlds `BirthAndDeath` module must remain disabled on both clients and headless. The package verifies only that its seed template defaults `birthAndDeath` to true; that is not evidence of the live value. Deployment preflight must parse the resolved `COOP_DATA_DIR`/`BANNERLORD_USER_DIR` `mod-config.json`, require semantic Boolean `difficulty.birthAndDeath=true`, and print its path and SHA-256. Friend Edition's authenticated runtime config authority must separately log the same effective setting/fingerprint before authoritative aging/pregnancy is accepted.

Run the self-contained fixture test with:

```powershell
.\tools\WorkshopIntegration\tests\Run-Tests.ps1
```
