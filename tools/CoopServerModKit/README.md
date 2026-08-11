# CoopServerModKit

Server‑bin‑only tooling that lets the **.NET Core** Linux dedicated server run **.NET Framework**
BUTR/Workshop mods (ButterLib + the campaign mods). All outputs live in
`Modules/*/bin/Win64_Shipping_Server` and are **excluded from the Workshop content hash**, so they
never affect the join handshake. Full rationale: [`../../doc/COOP-MOD-INTEGRATION.md`](../../doc/COOP-MOD-INTEGRATION.md).

| Piece | Build | Output | Deploy to |
|---|---|---|---|
| `StartupHook.cs` (+ `coophook.csproj`) | `dotnet build -c Release` (net6.0) | `coophook.dll` | `~/bannerlord-coop/server/coophook.dll`, referenced via `DOTNET_STARTUP_HOOKS='Z:\…\coophook.dll'` |
| `dspatch/` | `dotnet run -- <in.dll> <out.dll>` (net8, Mono.Cecil) | patched `DedicatedServer.Core.dll` | **root** `engine-mods/bin/Win64_Shipping_Server/DedicatedServer.Core.dll` |
| `butterlib-patcher/` | `dotnet run -- <in.dll> <out.dll>` | patched `Bannerlord.ButterLib.dll` / `0Harmony.dll` | ButterLib server bin |

Verify a patch with `ilspycmd <patched.dll>` and re‑launch the service (`systemctl --user restart
bannerlord-coop-seven.service`) — never a manual server on `engine-mods` (port‑4200 clash; see the
findings doc §2). Also required in the server bin but not built here: **net6 MonoMod** and the
netstandard2.0 **BUTR.CrashReport.Renderer.{WinForms,ImGui}** stubs (see doc §3).
