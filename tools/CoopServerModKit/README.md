# CoopServerModKit

Server‑bin‑only tooling that lets the **.NET Core** Linux dedicated server run **.NET Framework**
BUTR/Workshop mods (ButterLib + the campaign mods). All outputs live in
`Modules/*/bin/Win64_Shipping_Server` and are **excluded from the Workshop content hash**, so they
never affect the join handshake. Full rationale: [`../../doc/COOP-MOD-INTEGRATION.md`](../../doc/COOP-MOD-INTEGRATION.md).

| Piece | Build | Output | Deploy to |
|---|---|---|---|
| `StartupHook.cs` (+ `coophook.csproj`) | `dotnet build -c Release` (net6.0) | `coophook.dll` | `~/bannerlord-coop/server/coophook.dll`, referenced via `DOTNET_STARTUP_HOOKS='Z:\…\coophook.dll'` |
| `dspatch/` | `dotnet run -- <in.dll> <out.dll>` (net8, Mono.Cecil) | loader-patched `DedicatedServer.Core.dll` | input to the release-pairing patcher |
| `butterlib-patcher/` | `dotnet run -- <in.dll> <out.dll>` | patched `Bannerlord.ButterLib.dll` | ButterLib server bin |
| `../DedicatedServerCompatibilityPatcher/` | see its README | release-paired `DedicatedServer.Core.dll` + receipt | both physical dedicated-server `Win64_Shipping_Server` locations |

Both Cecil patchers require the exact audited input SHA-256 and exact type/method/signature/count
fingerprints. The loader patch no longer writes the permanent `/tmp/ensure.log` probe. The startup hook
always records unhandled failures; high-volume first-chance logging is enabled only when
`COOP_SERVER_KIT_DIAGNOSTICS=1` and stops at 400 KB.

Verify the final pairing receipt and IL, then re-launch the service (`systemctl --user restart
bannerlord-coop-seven.service`) — never a manual server on `engine-mods` (port-4200 clash; see the
findings doc §2). Also required in the server bin but not built here: **net6 MonoMod** and the
netstandard2.0 **BUTR.CrashReport.Renderer.{WinForms,ImGui}** stubs (see doc §3).
