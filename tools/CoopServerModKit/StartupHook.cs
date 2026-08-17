using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

// Injected via DOTNET_STARTUP_HOOKS. Two jobs:
//  1. A BLSE-style assembly resolver: when the .NET runtime can't resolve a by-name assembly from
//     the root bin, probe every module's bin folder. This lets mod code (ButterLib and the
//     campaign mods) resolve their bundled dependencies WITHOUT polluting the shared root bin,
//     which would collide with Coop's own dependency versions.
//  2. Capture any unhandled exception to /tmp/coop-fce.log before the TaleWorlds watchdog kills it.
//     High-volume first-chance diagnostics are opt-in via COOP_SERVER_KIT_DIAGNOSTICS=1 and capped.
internal sealed class StartupHook
{
    private const string LogPath = "/tmp/coop-fce.log";
    private const string MountAndBladeAssemblyName = "TaleWorlds.MountAndBlade";
    private const string DedicatedModuleFilterHarmonyId = "BannerlordCoop.ServerKit.DedicatedModuleFilter";
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedResolutionEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ClientOnlyEngineAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SandBox.GauntletUI",
        "SandBox.View",
        "SandBox.ViewModelCollection",
        "StoryMode",
        "TaleWorlds.CampaignSystem.ViewModelCollection",
        "TaleWorlds.Core.ViewModelCollection",
        "TaleWorlds.Engine.GauntletUI",
        "TaleWorlds.GauntletUI",
        "TaleWorlds.GauntletUI.Data",
        "TaleWorlds.GauntletUI.ExtraWidgets",
        "TaleWorlds.GauntletUI.PrefabSystem",
        "TaleWorlds.MountAndBlade.GauntletUI",
        "TaleWorlds.MountAndBlade.GauntletUI.Widgets",
        "TaleWorlds.MountAndBlade.View",
        "TaleWorlds.MountAndBlade.ViewModelCollection",
        "TaleWorlds.TwoDimension",
    };
    private static string[] _binDirs;
    private static int _dedicatedModuleFilterInstalled;

    public static void Initialize()
    {
        try { File.WriteAllText(LogPath, "[hook] initialized " + DateTime.Now.ToString("O") + "\n"); } catch { }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromModuleBins;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            OnAssemblyLoaded(null, new AssemblyLoadEventArgs(assembly));

        if (string.Equals(
            Environment.GetEnvironmentVariable("COOP_SERVER_KIT_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal))
        {
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try
                {
                    var ex = e.Exception;
                    var st = ex.StackTrace ?? "";
                    // Skip the resolver's own probe misses (huge volume, benign).
                    if (ex is FileNotFoundException && st.IndexOf("ResolveFromModuleBins", StringComparison.Ordinal) >= 0) return;
                    // Save loading intentionally probes thousands of assemblies and reflection
                    // shapes. Preserve the finite log for faults capable of terminating the host
                    // or crossing one of Friend Edition's runtime boundaries.
                    if (!(ex is InvalidOperationException) &&
                        ex.HResult != unchecked((int)0x80004005) &&
                        st.IndexOf("BannerlordPlayerSettlement.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("PlayerSettlementFixes.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("GameInterface.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("Coop.", StringComparison.Ordinal) < 0)
                        return;
                    if (new FileInfo(LogPath).Length >= 400000) return;
                    File.AppendAllText(LogPath,
                        "\n=== FIRST-CHANCE " + DateTime.Now.ToString("HH:mm:ss.fff") + " ===\n" +
                        ex.GetType().FullName + ": " + ex.Message + "\n" + st + "\n");
                }
                catch { }
            };
        }
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.AppendAllText(LogPath, "\n=== UNHANDLED ===\n" + (e.ExceptionObject?.ToString() ?? "?") + "\n"); } catch { }
        };
    }

    private static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
    {
        var loaded = args.LoadedAssembly;
        var name = loaded?.GetName().Name;
        if (string.IsNullOrEmpty(name)) return;

        if (string.Equals(name, MountAndBladeAssemblyName, StringComparison.Ordinal))
            InstallDedicatedModuleFilter(loaded);

        if (!name.StartsWith("Bannerlord.Diplomacy", StringComparison.Ordinal)) return;
        LogResolutionOnce(
            "assembly-load:" + loaded.FullName + ":" + SafeLocation(loaded),
            "[assembly-load] " + loaded.FullName + " from " + SafeLocation(loaded));
    }

    private static void InstallDedicatedModuleFilter(Assembly mountAndBladeAssembly)
    {
        if (Interlocked.CompareExchange(ref _dedicatedModuleFilterInstalled, 1, 0) != 0) return;

        try
        {
            Type moduleType = mountAndBladeAssembly.GetType(
                "TaleWorlds.MountAndBlade.Module",
                throwOnError: true);
            MethodInfo target = AccessTools.Method(moduleType, "CheckIfSubmoduleCanBeLoadable")
                ?? throw new MissingMethodException(moduleType.FullName, "CheckIfSubmoduleCanBeLoadable");
            MethodInfo prefix = AccessTools.Method(
                typeof(StartupHook),
                nameof(CheckIfSubmoduleCanBeLoadablePrefix))
                ?? throw new MissingMethodException(typeof(StartupHook).FullName, nameof(CheckIfSubmoduleCanBeLoadablePrefix));

            new Harmony(DedicatedModuleFilterHarmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix));
            LogResolutionOnce(
                "dedicated-module-filter-installed",
                "[module-filter] installed exact dedicated Workshop submodule allowlist");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dedicatedModuleFilterInstalled, 0);
            LogResolutionOnce(
                "dedicated-module-filter-error",
                "[module-filter] ERROR: " + exception.GetType().FullName + ": " + exception.Message);
            throw;
        }
    }

    private static bool CheckIfSubmoduleCanBeLoadablePrefix(object __0, ref bool __result)
    {
        string classType = null;
        try
        {
            classType = __0?.GetType()
                .GetProperty("SubModuleClassTypeName", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(__0) as string;
        }
        catch { }

        if (!ShouldForceDedicatedWorkshopSubModule(classType)) return true;

        __result = true;
        LogResolutionOnce(
            "dedicated-module-force:" + classType,
            "[module-filter] enabling audited dedicated Workshop submodule " + classType);
        return false;
    }

    private static bool ShouldForceDedicatedWorkshopSubModule(string classType)
    {
        switch (classType)
        {
            case "ImprovedGarrisons.Main":
            case "DismembermentPlus.Main":
            case "Fourberie.Main":
            case "UnblockableThrust.UnblockableThrustSubmodule":
            case "RebellionsAndDemographics.SubModule":
                return true;
            default:
                return false;
        }
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
                if (!ShouldProbeModuleBins(simpleName))
                    return null;
                // The Linux dedicated engine intentionally omits presentation assemblies that the
                // audited Workshop runtimes still reference. Resolve only this pinned closure from
                // deterministic support locations; never broaden TaleWorlds/SandBox probing.
                if (ClientOnlyEngineAssemblies.Contains(simpleName))
                {
                    var supportPath = ClientOnlySupportPath(simpleName);
                    return LoadCandidate(simpleName, supportPath, args.RequestingAssembly);
                }
                // Serilog is needed by both Coop and ButterLib; only one identity can load in this
                // process. Resolve Serilog* from the release-paired Coop server bin first so the
                // dedicated host shares the exact Serilog 2.x cross-build selected for that release.
                if (simpleName.StartsWith("Serilog", StringComparison.Ordinal))
                {
                    var coopBin = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")), "Modules", "Coop", "bin", "Win64_Shipping_Server");
                    var cp = Path.Combine(coopBin, simpleName + ".dll");
                    if (File.Exists(cp))
                    {
                        var loaded = LoadCandidate(simpleName, cp, args.RequestingAssembly);
                        if (loaded != null) return loaded;
                    }
                }
                foreach (var dir in BinDirs())
                {
                    var p = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(p))
                    {
                        var loaded = LoadCandidate(simpleName, p, args.RequestingAssembly);
                        if (loaded != null) return loaded;
                    }
                }
                LogResolutionOnce(
                    "miss:" + simpleName,
                    "[resolve] MISS " + simpleName + " requested by " + Requester(args.RequestingAssembly));
            }
        }
        catch (Exception exception)
        {
            LogResolutionOnce(
                "resolver:" + args.Name,
                "[resolve] ERROR " + args.Name + ": " + exception.GetType().FullName + ": " + exception.Message);
        }
        return null;
    }

    private static bool ShouldProbeModuleBins(string simpleName)
    {
        if (ClientOnlyEngineAssemblies.Contains(simpleName)) return true;
        return !simpleName.StartsWith("TaleWorlds.", StringComparison.Ordinal) &&
               !simpleName.StartsWith("SandBox", StringComparison.Ordinal) &&
               !simpleName.StartsWith("StoryMode", StringComparison.Ordinal) &&
               !simpleName.StartsWith("Coop", StringComparison.Ordinal) &&
               simpleName != "GameInterface" && simpleName != "Missions" && simpleName != "Common";
    }

    private static string ClientOnlySupportPath(string simpleName)
    {
        var engineRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        if (string.Equals(simpleName, "StoryMode", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(engineRoot, "Modules", "StoryMode", "bin", "Win64_Shipping_Client", simpleName + ".dll");
        return Path.Combine(engineRoot, "Modules", "Coop", "bin", "Win64_Shipping_Server", simpleName + ".dll");
    }

    private static Assembly LoadCandidate(string simpleName, string path, Assembly requester)
    {
        if (!File.Exists(path))
        {
            LogResolutionOnce(
                "absent:" + simpleName + ":" + path,
                "[resolve] ABSENT " + simpleName + " at " + path + " requested by " + Requester(requester));
            return null;
        }
        try
        {
            var assembly = Assembly.LoadFrom(path);
            Cache[simpleName] = assembly;
            LogResolutionOnce(
                "loaded:" + simpleName,
                "[resolve] loaded " + assembly.FullName + " from " + path + " requested by " + Requester(requester));
            return assembly;
        }
        catch (Exception exception)
        {
            LogResolutionOnce(
                "load-error:" + simpleName + ":" + path,
                "[resolve] LOAD ERROR " + simpleName + " from " + path + ": " +
                exception.GetType().FullName + ": " + exception.Message);
            return null;
        }
    }

    private static string Requester(Assembly requester)
        => requester == null ? "<unknown>" : requester.FullName;

    private static string SafeLocation(Assembly assembly)
    {
        try { return string.IsNullOrEmpty(assembly?.Location) ? "<no location>" : assembly.Location; }
        catch { return "<location unavailable>"; }
    }

    private static void LogResolutionOnce(string key, string message)
    {
        try
        {
            if (!LoggedResolutionEvents.Add(key)) return;
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= 400000) return;
            File.AppendAllText(LogPath, message + "\n");
        }
        catch { }
    }
}
