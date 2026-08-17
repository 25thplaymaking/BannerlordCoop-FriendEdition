# CoopServerModKit

Server‑bin‑only tooling that lets the **.NET Core** Linux dedicated server run **.NET Framework**
BUTR/Workshop mods (ButterLib + the campaign mods). All outputs live in
`Modules/*/bin/Win64_Shipping_Server` and are **excluded from the Workshop content hash**, so they
never affect the join handshake. Full rationale: [`../../doc/COOP-MOD-INTEGRATION.md`](../../doc/COOP-MOD-INTEGRATION.md).

| Piece | Build | Output | Deploy to |
|---|---|---|---|
| `StartupHook.cs` (+ `coophook.csproj`) | `dotnet build -c Release` (net6.0) | `coophook.dll` | `~/bannerlord-coop/server/coophook.dll`, referenced via `DOTNET_STARTUP_HOOKS='Z:\…\coophook.dll'`; installs exact four-class v1.4.8 gameplay and presentation policies before module filtering and makes duplicate save-container registration preserve-first; DismembermentPlus remains client-only because its presentation bootstrap requires WinForms |
| `Sync-ServerModuleBins.ps1` + `server-module-roles.json` | PowerShell 7 | exact gameplay/mission server overlays | each role-declared Workshop module's `Win64_Shipping_Server` bin |
| `Verify-ServerModuleBins.py` | Python 3 | fail-closed startup preflight | invoked by the production service before Wine |
| `dspatch/` | `dotnet run -- <in.dll> <out.dll>` (net8, Mono.Cecil) | loader-patched `DedicatedServer.Core.dll` | input to the release-pairing patcher |
| `butterlib-patcher/` | `dotnet run -- <in.dll> <out.dll>` | patched `Bannerlord.ButterLib.dll` | ButterLib server bin |
| `../DedicatedServerCompatibilityPatcher/` | see its README | release-paired `DedicatedServer.Core.dll` + receipt | both physical dedicated-server `Win64_Shipping_Server` locations |

Both Cecil patchers require the exact audited input SHA-256 and exact type/method/signature/count
fingerprints. The loader patch no longer writes the permanent `/tmp/ensure.log` probe. The startup hook
always records unhandled failures; high-volume first-chance logging is enabled only when
`COOP_SERVER_KIT_DIAGNOSTICS=1` and stops at 400 KB.

## Bannerlord v1.4.8 candidate pins

The 2026-08-15 migration uses dedicated-server app `1863440` build `24571419`,
depot `1863441`, manifest `4619456710482553639`. The actual `v1.4.8`
`TaleWorlds.Library.dll` SHA-256 is
`f11400860476b5860457503b4090ba642bc29f183c36e0301d3c504f2720ac16`;
the exactly-once loader-patched output is
`f1c8885ad5c908e6b2e4b0cc16c740627ef3202caab178170bdcafae0cea6d98`.
The active 16-file UI-support closure is likewise regenerated from the actual
`v1.4.8` client. Complete build, depot, inventory, and rollback provenance is in
[`../../deploy/bannerlord-1.4.8-inputs.json`](../../deploy/bannerlord-1.4.8-inputs.json).

An offline Serilog-2-compatible Coop bin has also been paired: the candidate
`DedicatedServer.Core.dll` SHA-256 is
`6b3ed5a858aaf3afcab1f7770d76ef976e8bdc097adfcc374d368b4697daf74c`
and its receipt SHA-256 is
`8b93ba231e9f822bf0a77b4c3688544b713ca0c1e45f78e0d1a8b634fbdf6c2f`.
Those hashes prove the exact five-file pairing only. The full mod overlay and
isolated-save boot still must pass before deployment.

The role manifest is intentional: PlayerSettlement, Improved Garrisons, DismembermentPlus,
Fourberie, Diplomacy, and UnblockableThrust run server submodules. UIExtenderEx and MCM retain
their client runtimes so audited gameplay assemblies can resolve type dependencies, but they must
not expose or initialize dedicated-server presentation submodules. The verifier rejects both a
missing server runtime and a client-presentation DLL accidentally copied into a server bin.

Verify the final pairing receipt and IL, then re-launch the service (`systemctl --user restart
bannerlord-coop-seven.service`) — never a manual server on `engine-mods` (port-4200 clash; see the
findings doc §2). Also required in the server bin but not built here: **net6 MonoMod** and the
netstandard2.0 **BUTR.CrashReport.Renderer.{WinForms,ImGui}** stubs (see doc §3).
