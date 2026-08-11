using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// Injected via DOTNET_STARTUP_HOOKS. Two jobs:
//  1. A BLSE-style assembly resolver: when the .NET runtime can't resolve a by-name assembly from
//     the root bin, probe every module's bin folder. This lets mod code (ButterLib and the
//     campaign mods) resolve their bundled dependencies WITHOUT polluting the shared root bin,
//     which would collide with Coop's own dependency versions.
//  2. Capture any unhandled exception to /tmp/coop-fce.log before the TaleWorlds watchdog kills it.
internal sealed class StartupHook
{
    private const string LogPath = "/tmp/coop-fce.log";
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private static string[] _binDirs;

    public static void Initialize()
    {
        try { File.WriteAllText(LogPath, "[hook] initialized " + DateTime.Now.ToString("O") + "\n"); } catch { }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromModuleBins;

        AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
        {
            try
            {
                var ex = e.Exception;
                var st = ex.StackTrace ?? "";
                // Skip the resolver's own probe misses (huge volume, benign).
                if (ex is FileNotFoundException && st.IndexOf("ResolveFromModuleBins", StringComparison.Ordinal) >= 0) return;
                if (new FileInfo(LogPath).Length > 400000) return;
                File.AppendAllText(LogPath,
                    "\n=== FIRST-CHANCE " + DateTime.Now.ToString("HH:mm:ss.fff") + " ===\n" +
                    ex.GetType().FullName + ": " + ex.Message + "\n" + st + "\n");
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.AppendAllText(LogPath, "\n=== UNHANDLED ===\n" + (e.ExceptionObject?.ToString() ?? "?") + "\n"); } catch { }
        };
    }

    private static string[] BinDirs()
    {
        if (_binDirs != null) return _binDirs;
        var list = new List<string>();
        try
        {
            // AppContext.BaseDirectory = <engine>/bin/Win64_Shipping_Server ; modules are ../../Modules
            var baseDir = AppContext.BaseDirectory;
            var engineRoot = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
            var modules = Path.Combine(engineRoot, "Modules");
            if (Directory.Exists(modules))
            {
                foreach (var mod in Directory.GetDirectories(modules))
                {
                    // Server bins first (patched ButterLib, stub renderers), then client bins.
                    var srv = Path.Combine(mod, "bin", "Win64_Shipping_Server");
                    var cli = Path.Combine(mod, "bin", "Win64_Shipping_Client");
                    if (Directory.Exists(srv)) list.Add(srv);
                    if (Directory.Exists(cli)) list.Add(cli);
                }
            }
        }
        catch { }
        _binDirs = list.ToArray();
        return _binDirs;
    }

    private static Assembly ResolveFromModuleBins(object sender, ResolveEventArgs args)
    {
        try
        {
            var simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName)) return null;
            lock (Sync)
            {
                if (Cache.TryGetValue(simpleName, out var cached)) return cached;
                // If something with this simple name is already loaded, reuse it — loading a second
                // copy from a different path throws "Assembly with same name is already loaded".
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!loaded.IsDynamic && string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        Cache[simpleName] = loaded;
                        return loaded;
                    }
                }
                // Never redirect TaleWorlds engine or Coop assemblies - the engine owns those.
                if (simpleName.StartsWith("TaleWorlds.", StringComparison.Ordinal) ||
                    simpleName.StartsWith("SandBox", StringComparison.Ordinal) ||
                    simpleName.StartsWith("StoryMode", StringComparison.Ordinal) ||
                    simpleName.StartsWith("Coop", StringComparison.Ordinal) ||
                    simpleName == "GameInterface" || simpleName == "Missions" || simpleName == "Common")
                    return null;
                // Serilog is needed by BOTH Coop (4.x) and ButterLib (2.x); only one can load.
                // Unify on Coop's newer 4.x set (Coop's logging is load-bearing for the server),
                // resolving Serilog* from Coop's bin first so the whole process shares it.
                if (simpleName.StartsWith("Serilog", StringComparison.Ordinal))
                {
                    var coopBin = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")), "Modules", "Coop", "bin", "Win64_Shipping_Server");
                    var cp = Path.Combine(coopBin, simpleName + ".dll");
                    if (File.Exists(cp))
                    {
                        try { var a = Assembly.LoadFrom(cp); Cache[simpleName] = a; return a; } catch { }
                    }
                }
                foreach (var dir in BinDirs())
                {
                    var p = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(p))
                    {
                        try
                        {
                            var asm = Assembly.LoadFrom(p);
                            Cache[simpleName] = asm;
                            return asm;
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
