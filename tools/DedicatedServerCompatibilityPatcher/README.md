# Dedicated-server compatibility patcher

The official dedicated server pins four Coop module assemblies to the client build it shipped with.
That is useful for preventing accidental version mixing, but it also exits with code 4 when a privately
built fork is installed. This utility removes only that exit call from `DedicatedServer.Core.dll`.
The two verification passes and their diagnostic messages remain intact.

The patcher is deliberately version-guarded. It refuses to write an output unless it finds exactly the
known `A.j::B(string)` abort path, exactly one call to `A.J::A(int)`, and exit code 4 immediately before it.

## Usage

1. Stop the dedicated-server process.
2. Make a verified backup of every real `DedicatedServer.Core.dll` loaded by the server.
3. Run:

   ```powershell
   dotnet run --project tools/DedicatedServerCompatibilityPatcher -- `
     C:\path\to\DedicatedServer.Core.dll `
     C:\path\to\DedicatedServer.Core.patched.dll
   ```

4. Install the patched output in the engine binary directory and the
   `Modules/DedicatedServer.Windows` binary directory. Resolve symlinks first; the process may load a
   different physical copy than the module path suggests.
5. Keep the server-compatible `0Harmony.dll` from the official server distribution when overlaying a
   newer Coop build. Newer client Harmony builds can fail during server bootstrap.
6. Start the service and require all three health signals before accepting the deployment: active
   process with zero restarts, UDP game port bound, and repeated `[DedicatedServer] pulse:` log entries.

Do not distribute the patched third-party DLL. This repository's current source-available license does not
grant permission to create a private derivative; obtain written permission from the BannerlordCoop
maintainers before deploying this fork, plus any separate permission required for the dedicated-server
binary. This tool contains no third-party binary.
