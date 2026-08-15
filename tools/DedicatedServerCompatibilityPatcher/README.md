# Dedicated-server release pairing patcher

The official dedicated server pins four Coop assemblies to the client build it shipped with and exits
with code 4 if any byte differs. Friend Edition previously neutralized that exit, which left the useful
diagnostics running but allowed a mismatched server to enter `SERVING`.

This tool takes the exact loader-patched `DedicatedServer.Core.dll` produced by
`../CoopServerModKit/dspatch`, computes the four hashes from a completed Coop server bin, rewrites the
server's four-entry hash dictionary, and restores the code-4 abort. It also emits
`SERVER-COOP-PAIRING.json`, the deployment receipt for those exact five binaries.

The transformation is version-pinned and fail-closed. It refuses an unexpected input SHA-256, assembly
identity, type, method signature, method count, neutralized-abort shape, missing module file, existing
output, or existing receipt. It never modifies an input in place.

## Usage

1. Finish and verify the Coop server build first. Do not point the patcher at a bin that is still being
   built or copied.
2. Produce the exact loader-patched input with `tools/CoopServerModKit/dspatch`.
3. Run:

   ```powershell
   dotnet run --project tools/DedicatedServerCompatibilityPatcher -- `
     C:\staging\DedicatedServer.Core.loader-patched.dll `
     C:\staging\DedicatedServer.Core.release-paired.dll `
     C:\staging\Coop\bin\Win64_Shipping_Server `
     C:\staging\SERVER-COOP-PAIRING.json
   ```

4. Verify the receipt against the staged files. Install the paired output in both physical
   `Win64_Shipping_Server` locations the process can load; resolve symlinks first.
5. Keep the receipt with the private release metadata. A later Coop DLL change requires a new pairing
   output and receipt; copying only the DLL must make the next boot fail.
6. Start the service and require: no `COOP MODULE VERIFICATION FAILED`, active process with zero
   restarts, UDP game port bound, `phase":"serving"`, and repeated `[DedicatedServer] pulse:` lines.

Do not redistribute the patched third-party DLL. The tool contains no third-party binary.

## Campaign-system setter preparation

The Coop runtime patches campaign property setters. The former working v1.4.7 server input differs
from TaleWorlds' client assembly only by `NoInlining` on every concrete property setter: 1,264 of
1,274 setters, with identical IL bodies and all abstract setters untouched. Prepare the exact v1.4.8
assembly with the same fail-closed transformation:

```powershell
dotnet run --project tools/DedicatedServerCompatibilityPatcher -- campaign-setters `
  C:\staging\TaleWorlds.CampaignSystem.dll `
  C:\staging\TaleWorlds.CampaignSystem.server.dll
```

The command accepts only the pinned v1.4.8 SHA-256 and exact setter shape, never edits its input in
place, and refuses an existing output.

## Prepare the v1.4.8 SandBox server descriptor

Bannerlord v1.4.8 added `DedicatedServerType=none` to `SandBox.SandBoxSubModule`. The Coop campaign
host needs that gameplay submodule to run `SandBoxManager.InitializeSandboxXMLs`; without it, the
engine reaches map loading with unresolved NPCs and settlements and fails while registering the
settlement distance cache. View and GauntletUI remain excluded.

```powershell
dotnet run --project tools/DedicatedServerCompatibilityPatcher -- `
  sandbox-server-descriptor `
  C:\staging\Modules\SandBox\SubModule.xml `
  C:\staging\Modules\SandBox\SubModule.server.xml
```

The command accepts only the exact v1.4.8 descriptor hash, proves the gameplay and presentation
submodule shape, removes only the gameplay submodule's two server-exclusion tags, and pins the
resulting output hash. `BirthAndDeath` does not need this treatment: its descriptor and presentation-
only DLL are unchanged from v1.4.7, while Coop owns the configured campaign birth/death lifecycle.
