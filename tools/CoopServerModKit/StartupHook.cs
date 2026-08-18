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
//  3. Make v1.4.8 save-container registration idempotent before the dedicated engine scans the
//     pinned R&D definitions. The graphical engine tolerated those duplicates; headless exits 84.
internal sealed class StartupHook
{
    private const string LogPath = "/tmp/coop-fce.log";
    private const string MountAndBladeAssemblyName = "TaleWorlds.MountAndBlade";
    private const string SaveSystemAssemblyName = "TaleWorlds.SaveSystem";
    private const string DedicatedModuleFilterHarmonyId = "BannerlordCoop.ServerKit.DedicatedModuleFilter";
    private const string SaveDefinitionCompatibilityHarmonyId = "BannerlordCoop.ServerKit.SaveDefinitionCompatibility";
    private const string EoePatchesAssemblyName = "Bannerlord.EOEPatches";
    private const string EoePresentationLifecycleHarmonyId = "BannerlordCoop.ServerKit.EoePresentationLifecycle";
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
    private static int _eoePresentationLifecycleGuardInstalled;
    private static int _saveDefinitionCompatibilityInstalled;
    private static FieldInfo _saveDefinitionContextField;
    private static MethodInfo _definitionContextHasDefinition;

    public static void Initialize()
    {
        try { File.WriteAllText(LogPath, "[hook] initialized " + DateTime.Now.ToString("O") + "\n"); } catch { }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromModuleBins;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            OnAssemblyLoaded(null, new AssemblyLoadEventArgs(assembly));

        // "1" keeps the curated filter below. "2" additionally logs every exception that is not
        // on the known-noise list, which is what it takes to see a fault raised inside TaleWorlds
        // code: the curated filter keys off our own namespaces and would drop it.
        string diagnosticsLevel = Environment.GetEnvironmentVariable("COOP_SERVER_KIT_DIAGNOSTICS");
        bool verboseDiagnostics = string.Equals(diagnosticsLevel, "2", StringComparison.Ordinal);
        if (string.Equals(diagnosticsLevel, "1", StringComparison.Ordinal) || verboseDiagnostics)
        {
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try
                {
                    var ex = e.Exception;
                    var st = ex.StackTrace ?? "";
                    // Skip the resolver's own probe misses (huge volume, benign).
                    if (ex is FileNotFoundException && st.IndexOf("ResolveFromModuleBins", StringComparison.Ordinal) >= 0) return;
                    // The Seq sink retries a Seq server that is not running on this host, and the
                    // runtime probes api-ms-win-* stubs that carry no managed manifest. Both are
                    // benign, both are high volume, and together they buried the one real fault
                    // this log existed to surface.
                    string exceptionType = ex.GetType().FullName ?? "";
                    if (exceptionType.StartsWith("System.Net.", StringComparison.Ordinal)) return;
                    if (ex is BadImageFormatException &&
                        st.IndexOf("AssemblyName.GetAssemblyName", StringComparison.Ordinal) >= 0) return;
                    // Save loading intentionally probes thousands of assemblies and reflection
                    // shapes. Preserve the finite log for faults capable of terminating the host
                    // or crossing one of Friend Edition's runtime boundaries.
                    // Load-time failures (TypeLoad/MissingMember/BadImageFormat) and reflection
                    // wrappers are how a mod-vs-mod assembly or shape conflict actually surfaces,
                    // and the conversion's own namespaces are added so an EoE fault is not filtered
                    // out as third-party noise. Without these a boot can die leaving no managed
                    // trace at all, which is exactly what the exit-5 EoE failure looked like.
                    if (!verboseDiagnostics &&
                        !(ex is InvalidOperationException) &&
                        !(ex is DivideByZeroException) &&
                        !(ex is TypeLoadException) &&
                        !(ex is MissingMemberException) &&
                        !(ex is TargetInvocationException) &&
                        ex.HResult != unchecked((int)0x80004005) &&
                        st.IndexOf("BannerlordPlayerSettlement.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("PlayerSettlementFixes.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("GameInterface.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("Coop.", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("Europe1100", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("SnowballingKingdoms", StringComparison.Ordinal) < 0 &&
                        st.IndexOf("EOEPatches", StringComparison.Ordinal) < 0)
                        return;
                    if (new FileInfo(LogPath).Length >= 400000) return;
                    string diagnosticStack = ex is DivideByZeroException
                        ? "\nFirst-chance observer stack:\n" + Environment.StackTrace
                        : "";
                    File.AppendAllText(LogPath,
                        "\n=== FIRST-CHANCE " + DateTime.Now.ToString("HH:mm:ss.fff") + " ===\n" +
                        ex.GetType().FullName + ": " + ex.Message + "\n" + st + diagnosticStack + "\n");
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

        if (string.Equals(name, EoePatchesAssemblyName, StringComparison.Ordinal))
            InstallEoePresentationLifecycleGuard(loaded);
        if (string.Equals(name, MountAndBladeAssemblyName, StringComparison.Ordinal))
            InstallDedicatedModuleFilter(loaded);
        if (string.Equals(name, SaveSystemAssemblyName, StringComparison.Ordinal))
            InstallSaveDefinitionCompatibility(loaded);

        if (!name.StartsWith("Bannerlord.Diplomacy", StringComparison.Ordinal)) return;
        LogResolutionOnce(
            "assembly-load:" + loaded.FullName + ":" + SafeLocation(loaded),
            "[assembly-load] " + loaded.FullName + " from " + SafeLocation(loaded));
    }

    private static void InstallSaveDefinitionCompatibility(Assembly saveSystemAssembly)
    {
        if (Interlocked.CompareExchange(ref _saveDefinitionCompatibilityInstalled, 1, 0) != 0) return;

        try
        {
            Type definerType = saveSystemAssembly.GetType(
                "TaleWorlds.SaveSystem.SaveableTypeDefiner",
                throwOnError: true);
            _saveDefinitionContextField = definerType.GetField(
                "_definitionContext", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(definerType.FullName, "_definitionContext");
            _definitionContextHasDefinition = _saveDefinitionContextField.FieldType.GetMethod(
                "HasDefinition", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Type) }, modifiers: null)
                ?? throw new MissingMethodException(_saveDefinitionContextField.FieldType.FullName, "HasDefinition");
            MethodInfo target = definerType.GetMethod(
                "ConstructContainerDefinition", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Type) }, modifiers: null)
                ?? throw new MissingMethodException(definerType.FullName, "ConstructContainerDefinition");
            MethodInfo prefix = AccessTools.Method(
                typeof(StartupHook),
                nameof(ConstructContainerDefinitionPrefix))
                ?? throw new MissingMethodException(typeof(StartupHook).FullName, nameof(ConstructContainerDefinitionPrefix));

            new Harmony(SaveDefinitionCompatibilityHarmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix));
            LogResolutionOnce(
                "save-definition-compatibility-installed",
                "[save-compat] installed preserve-first container registration guard");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _saveDefinitionCompatibilityInstalled, 0);
            LogResolutionOnce(
                "save-definition-compatibility-error",
                "[save-compat] ERROR: " + exception.GetType().FullName + ": " + exception.Message);
            throw;
        }
    }

    private static bool ConstructContainerDefinitionPrefix(object __instance, Type __0)
    {
        try
        {
            object context = _saveDefinitionContextField?.GetValue(__instance);
            if (context == null || __0 == null) return true;
            bool alreadyDefined = (bool)_definitionContextHasDefinition.Invoke(context, new object[] { __0 });
            if (ShouldRunContainerDefinitionOriginal(alreadyDefined)) return true;

            LogResolutionOnce(
                "duplicate-save-container:" + __0.AssemblyQualifiedName,
                "[save-compat] preserving existing container definition " + __0.FullName);
            return false;
        }
        catch (Exception exception)
        {
            LogResolutionOnce(
                "save-definition-prefix-error:" + (__0?.AssemblyQualifiedName ?? "<null>"),
                "[save-compat] PREFIX ERROR: " + exception.GetType().FullName + ": " + exception.Message);
            return true;
        }
    }

    private static bool ShouldRunContainerDefinitionOriginal(bool alreadyDefined) => !alreadyDefined;

    /// <summary>
    /// Skips Empires of Europe 1100's <c>EOEPatches</c> initial-module-screen callback on the
    /// dedicated host.
    /// </summary>
    /// <remarks>
    /// <c>Bannerlord.EOEPatches.SubModule</c> carries no <c>DedicatedServerType</c> tag, so it is
    /// headless-loadable and its gameplay patches are wanted on the host. Only its
    /// <c>OnBeforeInitialModuleScreenSetAsRoot</c> is a problem: there is no rendered initial module
    /// screen on a dedicated host, and it dereferenced that state and threw
    /// NullReferenceException, killing every EoE boot with exit 5 at the exact tick the callback
    /// runs. Blocking the whole submodule instead would silently drop its campaign patches on the
    /// host while clients kept them, so only this one presentation callback is suppressed.
    /// </remarks>
    private static void InstallEoePresentationLifecycleGuard(Assembly eoePatchesAssembly)
    {
        if (Interlocked.CompareExchange(ref _eoePresentationLifecycleGuardInstalled, 1, 0) != 0) return;

        try
        {
            Type subModule = eoePatchesAssembly.GetType(
                "Bannerlord.EOEPatches.SubModule",
                throwOnError: true);
            MethodInfo target = AccessTools.Method(subModule, "OnBeforeInitialModuleScreenSetAsRoot")
                ?? throw new MissingMethodException(subModule.FullName, "OnBeforeInitialModuleScreenSetAsRoot");
            MethodInfo prefix = AccessTools.Method(
                typeof(StartupHook),
                nameof(SkipDedicatedPresentationCallbackPrefix))
                ?? throw new MissingMethodException(
                    typeof(StartupHook).FullName, nameof(SkipDedicatedPresentationCallbackPrefix));

            new Harmony(EoePresentationLifecycleHarmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix));
            LogResolutionOnce(
                "eoe-presentation-lifecycle-installed",
                "[eoe] suppressed EOEPatches.OnBeforeInitialModuleScreenSetAsRoot on the dedicated host");
        }
        catch (Exception exception)
        {
            // Deliberately not rethrown. This guard protects optional conversion content, and the
            // host is already mid-assembly-load here; the boot fails loudly on its own if the
            // callback still throws, and this line says why the guard was not there to stop it.
            Volatile.Write(ref _eoePresentationLifecycleGuardInstalled, 0);
            LogResolutionOnce(
                "eoe-presentation-lifecycle-error",
                "[eoe] ERROR installing the EOEPatches presentation guard: " +
                exception.GetType().FullName + ": " + exception.Message);
        }
    }

    private static bool SkipDedicatedPresentationCallbackPrefix() => false;

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

        if (WithholdEoeGameplaySubModules && classType != null && EoeGameplaySubModules.Contains(classType))
        {
            __result = false;
            LogResolutionOnce(
                "eoe-gameplay-withheld:" + classType,
                "[module-filter] DIAGNOSTIC: withholding EoE gameplay submodule " + classType);
            return false;
        }
        if (ShouldBlockDedicatedPresentationSubModule(classType))
        {
            __result = false;
            LogResolutionOnce(
                "dedicated-module-block:" + classType,
                "[module-filter] blocking audited client-presentation submodule " + classType);
            return false;
        }
        if (!ShouldForceDedicatedWorkshopSubModule(classType)) return true;

        __result = true;
        LogResolutionOnce(
            "dedicated-module-force:" + classType,
            "[module-filter] enabling audited dedicated Workshop submodule " + classType);
        return false;
    }

    /// <summary>
    /// Diagnostic switch. With COOP_SERVER_KIT_NO_EOE_GAMEPLAY=1 the conversion's gameplay
    /// submodules are withheld from the dedicated host while their XML still loads, which
    /// separates "an EoE campaign behaviour breaks save loading" from "EoE's data does".
    /// Unset in production.
    /// </summary>
    private static readonly bool WithholdEoeGameplaySubModules = string.Equals(
        Environment.GetEnvironmentVariable("COOP_SERVER_KIT_NO_EOE_GAMEPLAY"), "1", StringComparison.Ordinal);

    private static readonly HashSet<string> EoeGameplaySubModules = new HashSet<string>(StringComparer.Ordinal)
    {
        "Europe1100.SubModule",
        "Bannerlord.EOEPatches.SubModule",
        "RF_BattleAI.SubModule",
        "BattleArtilleryReworked.SubModule",
        "SnowballingKingdoms.MySubModule",
        "ClansResourceAdder.MySubModule",
        "CustomizableClanTier.SubModule",
    };

    private static bool ShouldForceDedicatedWorkshopSubModule(string classType)
    {
        if (WithholdEoeGameplaySubModules && classType != null && EoeGameplaySubModules.Contains(classType))
            return false;
        switch (classType)
        {
            case "ImprovedGarrisons.Main":
            case "Fourberie.Main":
            case "UnblockableThrust.UnblockableThrustSubmodule":
            case "RebellionsAndDemographics.SubModule":
            // Europe1100 1100 conversion set. SnowballingKingdoms drives kingdom-level war/peace
            // and fief AI, ClansResourceAdder moves clan gold/influence, and CustomizableClanTier
            // changes clan-tier thresholds: all three mutate campaign state the dedicated host
            // owns, so running them on the client alone would produce values the host immediately
            // overwrites. Europe1100.SubModule, Bannerlord.EOEPatches.SubModule, RF_BattleAI and
            // BattleArtilleryReworked carry no DedicatedServerType tag and are already loadable
            // headless (the same reason Bannerlord.Diplomacy needs no entry here), so they are
            // deliberately absent from this list rather than forgotten.
            //
            // RBM is absent from the host entirely — not merely unforced here. Keeping it in
            // the module token as a data-only component was tried and failed: the engine applies
            // its combat-parameter XML with or without RBM.SubModule, and the host died with
            // exit 84 immediately after "Combat parameter overriden: 40 -> dagger_right_leftstance"
            // — the same native fault it was retired for on 2026-08-11. This allowlist cannot
            // protect against a data-driven path, so RBM is out of both module tokens.
            case "SnowballingKingdoms.MySubModule":
            case "ClansResourceAdder.MySubModule":
            case "CustomizableClanTier.SubModule":
                return true;
            default:
                return false;
        }
    }

    private static bool ShouldBlockDedicatedPresentationSubModule(string classType)
    {
        switch (classType)
        {
            case "Bannerlord.UIExtenderEx.SubModule":
            case "MCM.MCMSubModule":
            case "MCM.Internal.MCMImplementationSubModule":
            case "Bannerlord.ModuleLoader.Bannerlord_MBOptionScreen":
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
            var requested = new AssemblyName(args.Name);
            var simpleName = requested.Name;
            if (string.IsNullOrEmpty(simpleName)) return null;
            // Key the cache by the full display name. Keying by simple name made the first
            // resolution of a given name answer every later request for a different version of it.
            var cacheKey = args.Name;
            lock (Sync)
            {
                if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

                // Reuse an already-loaded assembly only when its identity actually satisfies the
                // request. Matching on simple name alone collapsed two different major versions
                // onto whichever module loaded first: ButterLib loads before Coop and brings its
                // own Serilog 2.0.0.0, so Coop's request for Serilog 4.2.0.0 was answered with the
                // 2.x assembly and Common.Logging's static constructor died on a missing
                // Serilog.Core.IBatchedLogEventSink. The client never hits this because .NET
                // Framework keeps both identities side by side; this restores that outcome here.
                Assembly sameNameLoaded = null;
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loaded.IsDynamic) continue;
                    var loadedName = loaded.GetName();
                    if (!string.Equals(loadedName.Name, simpleName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (requested.Version == null || loadedName.Version == requested.Version)
                    {
                        Cache[cacheKey] = loaded;
                        return loaded;
                    }
                    if (sameNameLoaded == null) sameNameLoaded = loaded;
                }

                if (ShouldProbeModuleBins(simpleName))
                {
                    // The Linux dedicated engine intentionally omits presentation assemblies that the
                    // audited Workshop runtimes still reference. Resolve only this pinned closure from
                    // deterministic support locations; never broaden TaleWorlds/SandBox probing.
                    if (ClientOnlyEngineAssemblies.Contains(simpleName))
                    {
                        var support = LoadCandidate(cacheKey, simpleName, ClientOnlySupportPath(simpleName),
                            args.RequestingAssembly);
                        if (support != null) return support;
                    }
                    else
                    {
                        // Serilog ships with both Coop (4.x) and ButterLib (2.x), so the Coop server
                        // bin is probed first for it — but the version the caller asked for still
                        // decides. Each module then binds its own build instead of one being forced
                        // onto the other's, which is what a single shared identity used to do.
                        var candidates = new List<string>();
                        if (simpleName.StartsWith("Serilog", StringComparison.Ordinal))
                        {
                            var coopBin = Path.Combine(
                                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")),
                                "Modules", "Coop", "bin", "Win64_Shipping_Server");
                            candidates.Add(Path.Combine(coopBin, simpleName + ".dll"));
                        }
                        foreach (var dir in BinDirs()) candidates.Add(Path.Combine(dir, simpleName + ".dll"));

                        string firstPresent = null;
                        foreach (var candidate in candidates)
                        {
                            if (!File.Exists(candidate)) continue;
                            if (firstPresent == null) firstPresent = candidate;
                            if (requested.Version == null) break;
                            Version candidateVersion = null;
                            try { candidateVersion = AssemblyName.GetAssemblyName(candidate).Version; }
                            catch { }
                            if (candidateVersion != requested.Version) continue;
                            var exact = LoadCandidate(cacheKey, simpleName, candidate, args.RequestingAssembly);
                            if (exact != null) return exact;
                        }

                        // No exact identity on disk: take the first file that was there, exactly as
                        // this resolver did before versions were considered.
                        if (firstPresent != null)
                        {
                            var loaded = LoadCandidate(cacheKey, simpleName, firstPresent, args.RequestingAssembly);
                            if (loaded != null) return loaded;
                        }
                    }
                }

                // Still nothing. A same-named assembly of another version beats a hard miss, and is
                // what the host returned before this method looked at versions at all.
                if (sameNameLoaded != null)
                {
                    Cache[cacheKey] = sameNameLoaded;
                    return sameNameLoaded;
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

    private static Assembly LoadCandidate(string cacheKey, string simpleName, string path, Assembly requester)
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
            Cache[cacheKey] = assembly;
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
