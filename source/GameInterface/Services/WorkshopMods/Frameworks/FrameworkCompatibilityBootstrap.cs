using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TWDebug = TaleWorlds.Library.Debug;

namespace GameInterface.Services.WorkshopMods.Frameworks;

/// <summary>
/// Applies before the normal Coop container is built. Bannerlord constructs every active submodule
/// before it calls any OnSubModuleLoad method, so this is the only safe window in which to block
/// MCM's filesystem-migration load hook. The second phase runs from Coop's OnSubModuleLoad, after
/// all earlier framework modules completed their load hooks and before campaign lifecycle methods.
/// </summary>
public static class FrameworkCompatibilityBootstrap
{
    private static readonly object Sync = new();
    private static readonly Harmony BoundaryHarmony = new(FrameworkCompatibilityManifest.AdapterHarmonyId);
    private static readonly Dictionary<string, Assembly> ValidatedAssemblies =
        new(StringComparer.Ordinal);

    private static bool prepared;
    private static bool completed;
    private static FrameworkActivationState activationState;
    private static IReadOnlyList<string> activeContainmentFindings = Array.Empty<string>();

    /// <summary>
    /// Validates the canonical Harmony provider. If any optional framework is active, validates the
    /// complete base cohort and installs the pre-load MCM guard. A fully staged/inactive cohort is valid.
    /// </summary>
    public static void PrepareBeforeOptionalModuleLoad()
    {
        lock (Sync)
        {
            if (prepared) return;

            var loaded = GetLoadedAssemblies();
            ValidateExpectedAssemblies(
                loaded,
                FrameworkCompatibilityManifest.Assemblies.Where(expectation =>
                    expectation.RequiredBeforeModuleLoad && !expectation.OptionalFramework),
                ValidatedAssemblies);

            FrameworkAssemblyExpectation[] optionalFrameworks = FrameworkCompatibilityManifest.Assemblies
                .Where(expectation => expectation.OptionalFramework)
                .ToArray();
            FrameworkAssemblyExpectation[] baseFrameworks = optionalFrameworks
                .Where(expectation => expectation.RequiredBeforeModuleLoad)
                .ToArray();
            activationState = DetermineActivationState(
                loaded.Keys,
                baseFrameworks.Select(x => x.AssemblyName),
                optionalFrameworks.Select(x => x.AssemblyName));
            if (activationState == FrameworkActivationState.StagedInactive)
            {
                prepared = true;
                return;
            }

            ValidateExpectedAssemblies(loaded, baseFrameworks, ValidatedAssemblies);

            FrameworkMethodExpectation earlyGuard = FrameworkCompatibilityManifest.GuardedMethods[0];
            MethodInfo earlyMethod = ResolveExactMethod(ValidatedAssemblies, earlyGuard);
            PatchDenyOriginal(earlyMethod);
            prepared = true;
        }
    }

    /// <summary>
    /// Contains and blocks an explicitly active framework cohort. Exact implementation drift,
    /// incomplete loader output, changed method shape, cleanup failure, or any activation itself
    /// aborts hardened Coop startup; the optional modules are receipts/dependency payloads only.
    /// </summary>
    public static void CompleteAfterOptionalModuleLoad()
    {
        lock (Sync)
        {
            if (!prepared)
                throw new InvalidOperationException(
                    "Framework compatibility boundary was not prepared from the Coop constructor.");

            if (activationState == FrameworkActivationState.StagedInactive) return;
            if (completed)
            {
                FrameworkHarmonyIsolation.AssertNoOriginalFrameworkPatches(
                    ValidatedAssemblies.Values);
                throw CreateActiveFrameworkBlock(activeContainmentFindings);
            }

            var loaded = GetLoadedAssemblies();
            ValidateExpectedAssemblies(
                loaded,
                FrameworkCompatibilityManifest.Assemblies.Where(expectation => expectation.OptionalFramework),
                ValidatedAssemblies);

            // Resolve every target before mutating runtime state. A changed overload or signature is
            // unsupported and fails before partial guard installation.
            MethodInfo[] guardedMethods = FrameworkCompatibilityManifest.GuardedMethods
                .Select(expectation => ResolveExactMethod(ValidatedAssemblies, expectation))
                .ToArray();

            var findings = new List<string>
            {
                // ExceptionHandlerSubSystem.Enable calls TaleWorlds.Engine.Utilities.DetachWatchdog.
                // The exact v1.4.7 engine exposes no inverse Attach API, so restoration cannot be proven.
                "ButterLib may have detached the TaleWorlds watchdog before Coop's constructor ran",
            };

            MethodInfo[] subsystemEnablers = Array.Empty<MethodInfo>();
            TryContain(
                "UIExtender deregistration",
                DisableAndDeregisterUIExtenders,
                findings);
            TryContain(
                "ButterLib subsystem shutdown",
                () =>
                {
                    subsystemEnablers = DisableAndResolveButterSubsystemEnablers(
                        out string[] nonDisableableSubsystems);
                    findings.AddRange(nonDisableableSubsystems);
                },
                findings);
            TryContain(
                "ButterLib DebugManager restoration",
                RestoreButterDebugManager,
                findings);
            TryContain(
                "ButterLib trace-state restoration",
                RemoveButterTraceListeners,
                findings);
            TryContain(
                "original framework Harmony purge",
                () => FrameworkHarmonyIsolation.RemoveOriginalFrameworkPatches(
                    ValidatedAssemblies.Values,
                    BoundaryHarmony),
                findings);
            TryContain(
                "post-purge Harmony inventory assertion",
                () => FrameworkHarmonyIsolation.AssertNoOriginalFrameworkPatches(
                    ValidatedAssemblies.Values),
                findings);

            foreach (MethodInfo method in guardedMethods.Concat(subsystemEnablers))
            {
                TryContain(
                    $"deny guard for {method.DeclaringType?.FullName}.{method.Name}",
                    () => PatchDenyOriginal(method),
                    findings);
            }

            TryContain(
                "final Harmony inventory assertion",
                () => FrameworkHarmonyIsolation.AssertNoOriginalFrameworkPatches(
                    ValidatedAssemblies.Values),
                findings);
            activeContainmentFindings = findings.ToArray();
            completed = true;
            throw CreateActiveFrameworkBlock(activeContainmentFindings);
        }
    }

    internal static FrameworkActivationState DetermineActivationState(
        IEnumerable<string> loadedAssemblyNames,
        IEnumerable<string> requiredFrameworkAssemblyNames,
        IEnumerable<string> recognizedFrameworkAssemblyNames = null)
    {
        var loaded = new HashSet<string>(loadedAssemblyNames ?? Array.Empty<string>(), StringComparer.Ordinal);
        string[] required = (requiredFrameworkAssemblyNames ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] present = required.Where(loaded.Contains).ToArray();

        if (present.Length == 0)
        {
            string[] implementationOnly = (recognizedFrameworkAssemblyNames ?? required)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !required.Contains(name, StringComparer.Ordinal) && loaded.Contains(name))
                .ToArray();
            if (implementationOnly.Length != 0)
                throw new InvalidOperationException(
                    "Partial Workshop framework activation is unsupported. Implementation assemblies " +
                    "were loaded without their audited base cohort: " +
                    string.Join(", ", implementationOnly));
            return FrameworkActivationState.StagedInactive;
        }
        if (present.Length != required.Length)
        {
            string[] missing = required.Where(name => !loaded.Contains(name)).ToArray();
            throw new InvalidOperationException(
                "Partial Workshop framework activation is unsupported. Missing: " +
                string.Join(", ", missing));
        }

        return FrameworkActivationState.ActiveExactBlocked;
    }

    internal static MethodInfo ResolveExactMethod(
        IReadOnlyDictionary<string, Assembly> assemblies,
        FrameworkMethodExpectation expectation)
    {
        if (!assemblies.TryGetValue(expectation.AssemblyName, out Assembly assembly))
            throw new InvalidOperationException(
                $"Framework assembly '{expectation.AssemblyName}' was not validated before shape inspection.");

        Type type = assembly.GetType(expectation.TypeName, throwOnError: false, ignoreCase: false) ??
            throw new InvalidOperationException(
                $"Framework shape drift: missing type {expectation.TypeName} in {expectation.AssemblyName}.");

        MethodInfo[] matches = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == expectation.MethodName)
            .Where(method => method.ReturnType.FullName == expectation.ReturnTypeName)
            .Where(method => ParametersMatch(method, expectation.ParameterTypeNames))
            .ToArray();

        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Framework shape drift: expected exactly one {expectation.Identity} in " +
                $"{expectation.AssemblyName}, found {matches.Length}.");
        return matches[0];
    }

    internal static void ValidateExpectedAssembly(
        Assembly assembly,
        FrameworkAssemblyExpectation expectation,
        Func<string, string> fileHasher = null)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        if (expectation == null) throw new ArgumentNullException(nameof(expectation));

        AssemblyName actualName = assembly.GetName();
        if (!string.Equals(actualName.Name, expectation.AssemblyName, StringComparison.Ordinal) ||
            actualName.Version != expectation.Version)
        {
            throw new InvalidOperationException(
                $"Framework identity drift for {expectation.AssemblyName}: loaded " +
                $"{actualName.Name} {actualName.Version}, expected {expectation.Version}.");
        }

        if (string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location))
            throw new InvalidOperationException(
                $"Framework assembly {expectation.AssemblyName} has no hashable on-disk location.");

        string actualHash = (fileHasher ?? ComputeFileSha256)(assembly.Location);
        if (!string.Equals(actualHash, expectation.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Framework fingerprint drift for {expectation.AssemblyName}: " +
                $"{actualHash}, expected {expectation.Sha256}.");
    }

    private static Dictionary<string, Assembly> GetLoadedAssemblies()
    {
        var result = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var expectedNames = new HashSet<string>(
            FrameworkCompatibilityManifest.Assemblies.Select(x => x.AssemblyName),
            StringComparer.Ordinal);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic))
        {
            string name = assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(name) || !expectedNames.Contains(name)) continue;
            if (result.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Multiple loaded framework assemblies share the simple name '{name}'.");
            result.Add(name, assembly);
        }
        return result;
    }

    private static void ValidateExpectedAssemblies(
        IReadOnlyDictionary<string, Assembly> loaded,
        IEnumerable<FrameworkAssemblyExpectation> expectations,
        IDictionary<string, Assembly> validated)
    {
        foreach (FrameworkAssemblyExpectation expectation in expectations)
        {
            if (!loaded.TryGetValue(expectation.AssemblyName, out Assembly assembly))
                throw new InvalidOperationException(
                    $"Required framework assembly '{expectation.AssemblyName}' is not loaded.");
            ValidateExpectedAssembly(assembly, expectation);
            validated[expectation.AssemblyName] = assembly;
        }
    }

    private static MethodInfo[] DisableAndResolveButterSubsystemEnablers(
        out string[] nonDisableableSubsystems)
    {
        var enablers = new List<MethodInfo>();
        var nonDisableable = new List<string>();
        foreach (string typeName in FrameworkCompatibilityManifest.ButterSubsystemTypes)
        {
            Type type = ValidatedAssemblies.Values
                .Select(assembly => assembly.GetType(typeName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(candidate => candidate != null) ??
                throw new InvalidOperationException(
                    $"Framework shape drift: missing ButterLib subsystem {typeName}.");

            PropertyInfo instanceProperty = type.GetProperty(
                "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    $"Framework shape drift: {typeName}.Instance is missing.");
            PropertyInfo enabledProperty = type.GetProperty(
                "IsEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    $"Framework shape drift: {typeName}.IsEnabled is missing.");
            PropertyInfo canDisableProperty = type.GetProperty(
                "CanBeDisabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    $"Framework shape drift: {typeName}.CanBeDisabled is missing.");
            MethodInfo disable = ExactParameterlessMethod(type, "Disable");
            MethodInfo enable = ExactParameterlessMethod(type, "Enable");
            object instance = instanceProperty.GetValue(null) ??
                throw new InvalidOperationException(
                    $"Framework initialization drift: {typeName}.Instance is null after module load.");

            if (canDisableProperty.GetValue(instance) is not bool canBeDisabled)
                throw new InvalidOperationException(
                    $"Framework shape drift: {typeName}.CanBeDisabled is not Boolean.");
            if (canBeDisabled)
            {
                InvokeUnwrapped(disable, instance);
                if (enabledProperty.GetValue(instance) is not bool isEnabled || isEnabled)
                    throw new InvalidOperationException(
                        $"Framework subsystem {typeName} remained enabled after cleanup.");
            }
            else
            {
                nonDisableable.Add($"{typeName} reports CanBeDisabled=false");
            }
            enablers.Add(enable);
        }
        nonDisableableSubsystems = nonDisableable.ToArray();
        return enablers.ToArray();
    }

    private static void RestoreButterDebugManager()
    {
        Assembly implementation = ValidatedAssemblies["Bannerlord.ButterLib.Implementation.1.4.7"];
        Type wrapperType = implementation.GetType(
            "Bannerlord.ButterLib.Implementation.Logging.DebugManagerWrapper",
            throwOnError: false,
            ignoreCase: false) ??
            throw new InvalidOperationException(
                "Framework shape drift: ButterLib DebugManagerWrapper is missing.");
        object wrapper = TWDebug.DebugManager ??
            throw new InvalidOperationException(
                "Framework cleanup failed: TaleWorlds DebugManager is null.");
        if (wrapper.GetType() != wrapperType)
            throw new InvalidOperationException(
                "Framework cleanup failed: ButterLib did not leave the exact audited DebugManager wrapper.");

        PropertyInfo originalProperty = wrapperType.GetProperty(
            "OriginalDebugManager",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) ??
            throw new InvalidOperationException(
                "Framework shape drift: DebugManagerWrapper.OriginalDebugManager is missing.");
        if (originalProperty.PropertyType != typeof(IDebugManager))
            throw new InvalidOperationException(
                "Framework shape drift: DebugManagerWrapper.OriginalDebugManager changed type.");
        if (originalProperty.GetValue(wrapper) is not IDebugManager original ||
            ReferenceEquals(original, wrapper))
            throw new InvalidOperationException(
                "Framework cleanup failed: ButterLib's original DebugManager is invalid.");

        TWDebug.DebugManager = original;
        if (TWDebug.DebugManager?.GetType() == wrapperType)
            throw new InvalidOperationException(
                "Framework cleanup failed: ButterLib's DebugManager wrapper remained installed.");
    }

    private static void RemoveButterTraceListeners()
    {
        Assembly butter = ValidatedAssemblies["Bannerlord.ButterLib"];
        Type subModuleType = butter.GetType(
            "Bannerlord.ButterLib.ButterLibSubModule",
            throwOnError: false,
            ignoreCase: false) ??
            throw new InvalidOperationException(
                "Framework shape drift: ButterLibSubModule is missing.");
        PropertyInfo instanceProperty = subModuleType.GetProperty(
            "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly) ??
            throw new InvalidOperationException(
                "Framework shape drift: ButterLibSubModule.Instance is missing.");
        object instance = instanceProperty.GetValue(null) ??
            throw new InvalidOperationException(
                "Framework initialization drift: ButterLibSubModule.Instance is null after module load.");
        PropertyInfo temporaryListenerProperty = subModuleType.GetProperty(
            "TextWriterTraceListener",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) ??
            throw new InvalidOperationException(
                "Framework shape drift: ButterLib's temporary trace listener property is missing.");

        if (temporaryListenerProperty.GetValue(instance) is TraceListener temporaryListener)
        {
            if (Trace.Listeners.Contains(temporaryListener))
            {
                Trace.Listeners.Remove(temporaryListener);
                temporaryListener.Dispose();
            }
            temporaryListenerProperty.SetValue(instance, null);
        }

        TraceListener[] ownedListeners = Trace.Listeners
            .Cast<TraceListener>()
            .Where(listener => FrameworkHarmonyIsolation.IsOptionalFrameworkAssembly(
                listener.GetType().Assembly))
            .ToArray();
        foreach (TraceListener listener in ownedListeners)
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        // ButterLib sets this process-wide value unconditionally during its load hook. The exact
        // supported suite starts from the framework default (false), so restore and assert it.
        Trace.AutoFlush = false;
        if (Trace.AutoFlush || Trace.Listeners.Cast<TraceListener>().Any(listener =>
                FrameworkHarmonyIsolation.IsOptionalFrameworkAssembly(listener.GetType().Assembly)))
            throw new InvalidOperationException(
                "Framework cleanup failed: ButterLib trace state remained active.");
    }

    private static void DisableAndDeregisterUIExtenders()
    {
        Assembly assembly = ValidatedAssemblies["Bannerlord.UIExtenderEx"];
        Type type = assembly.GetType(
            "Bannerlord.UIExtenderEx.UIExtender", throwOnError: false, ignoreCase: false) ??
            throw new InvalidOperationException(
                "Framework shape drift: Bannerlord.UIExtenderEx.UIExtender is missing.");
        FieldInfo instancesField = type.GetField(
            "Instances", BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Framework shape drift: UIExtender.Instances is missing.");
        if (instancesField.GetValue(null) is not IDictionary instances)
            throw new InvalidOperationException(
                "Framework shape drift: UIExtender.Instances is not a dictionary.");

        MethodInfo disable = ExactParameterlessMethod(type, "Disable");
        MethodInfo deregister = ExactParameterlessMethod(type, "Deregister");
        object[] registered = instances.Values.Cast<object>().ToArray();
        foreach (object extender in registered)
        {
            InvokeUnwrapped(disable, extender);
            InvokeUnwrapped(deregister, extender);
        }
        if (instances.Count != 0)
            throw new InvalidOperationException(
                "Framework cleanup failed: UIExtender registrations remain after deregistration.");

        // UIExtender's static constructor changes this engine-wide flag before Coop's constructor
        // can run. Restore the vanilla value now that all extension runtimes are disabled.
        UIConfig.DoNotUseGeneratedPrefabs = false;
        if (UIConfig.DoNotUseGeneratedPrefabs)
            throw new InvalidOperationException(
                "Framework cleanup failed: UIExtender's global prefab policy remained active.");
    }

    private static MethodInfo ExactParameterlessMethod(Type type, string methodName)
    {
        MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName &&
                             method.ReturnType == typeof(void) &&
                             method.GetParameters().Length == 0)
            .ToArray();
        if (methods.Length != 1)
            throw new InvalidOperationException(
                $"Framework shape drift: expected exactly one {type.FullName}.{methodName}().");
        return methods[0];
    }

    private static void InvokeUnwrapped(MethodInfo method, object instance)
    {
        try
        {
            method.Invoke(instance, null);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Framework cleanup failed in {method.DeclaringType?.FullName}.{method.Name}.",
                exception.InnerException ?? exception);
        }
    }

    private static void TryContain(
        string operation,
        Action action,
        ICollection<string> findings)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            findings.Add($"{operation} failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static InvalidOperationException CreateActiveFrameworkBlock(
        IEnumerable<string> findings)
    {
        string details = string.Join("; ", (findings ?? Array.Empty<string>())
            .Where(finding => !string.IsNullOrWhiteSpace(finding))
            .Distinct(StringComparer.Ordinal));
        return new InvalidOperationException(
            "The optional ButterLib/UIExtenderEx/MCM cohort is staged for dependency receipts only " +
            "and cannot be activated in hardened Coop. Containment was attempted, but " +
            "safe restoration of every pre-Coop lifecycle mutation cannot be proven. Disable " +
            "Bannerlord.ButterLib, Bannerlord.UIExtenderEx, and Bannerlord.MBOptionScreen, then " +
            "restart the process." +
            (details.Length == 0 ? string.Empty : " Findings: " + details));
    }

    private static bool ParametersMatch(
        MethodInfo method,
        IReadOnlyList<string> expectedParameterTypeNames)
    {
        ParameterInfo[] actual = method.GetParameters();
        if (actual.Length != expectedParameterTypeNames.Count) return false;
        for (int index = 0; index < actual.Length; index++)
        {
            if (!string.Equals(
                    actual[index].ParameterType.FullName,
                    expectedParameterTypeNames[index],
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void PatchDenyOriginal(MethodInfo method)
    {
        Patches patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo?.Prefixes?.Any(patch =>
                patch.owner == FrameworkCompatibilityManifest.AdapterHarmonyId &&
                patch.PatchMethod == FrameworkLifecycleGuards.DenyOriginalMethod) == true)
            return;

        BoundaryHarmony.Patch(
            method,
            prefix: new HarmonyMethod(FrameworkLifecycleGuards.DenyOriginalMethod));
    }

    private static string ComputeFileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return ToHex(sha.ComputeHash(stream));
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }
}

internal static class FrameworkLifecycleGuards
{
    internal static MethodInfo DenyOriginalMethod { get; } =
        typeof(FrameworkLifecycleGuards).GetMethod(
            nameof(DenyOriginal), BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("Framework deny-original guard method is missing.");

    private static bool DenyOriginal(MethodBase __originalMethod)
    {
        FrameworkSettingsAuthority.RecordDeniedMutation(__originalMethod);
        return false;
    }
}

/// <summary>
/// MCM objects are retained solely so staged dependent assemblies can resolve their references.
/// No MCM value is admitted into Friend Edition's authoritative gameplay fingerprint.
/// </summary>
internal static class FrameworkSettingsAuthority
{
    private static readonly string GameplayFingerprint = ComputeGameplayFingerprint();

    internal static string AuthoritativeGameplayFingerprint => GameplayFingerprint;
    internal static bool AllowsLocalMcmMutation => false;
    internal static string LastDeniedMutation { get; private set; } = string.Empty;

    internal static string FingerprintAfterLocalMcmView(
        IEnumerable<KeyValuePair<string, string>> localMcmValues)
    {
        // Deliberately do not fold local values into authoritative state. This method makes the
        // zero-consumer rule executable and testable without loading MCM into the test process.
        _ = localMcmValues;
        return GameplayFingerprint;
    }

    internal static void RecordDeniedMutation(MethodBase method)
    {
        LastDeniedMutation = $"{method?.DeclaringType?.FullName}.{method?.Name}";
    }

    private static string ComputeGameplayFingerprint()
    {
        string canonical = string.Join("|", new[]
        {
            FrameworkCompatibilityManifest.PolicyRevision,
            "mcm-authoritative-consumers=0",
            "mcm-local-writes=denied",
            "butterlib-gameplay=denied",
            "uiextender-runtime=denied",
        });
        using var sha = SHA256.Create();
        return FrameworkCompatibilityBootstrap.ToHex(
            sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
}
