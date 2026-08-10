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
/// before it calls any OnSubModuleLoad method. The second phase runs from Coop's OnSubModuleLoad,
/// after all earlier framework modules completed their load hooks: it byte-verifies the complete
/// active cohort against the audited manifest and then lets it run unmodified. The group runs the
/// full modded experience, so an active exact cohort is the expected production state; only
/// identity/fingerprint drift or a partial cohort aborts startup.
/// </summary>
public static class FrameworkCompatibilityBootstrap
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Assembly> ValidatedAssemblies =
        new(StringComparer.Ordinal);

    private static bool prepared;
    private static bool completed;
    private static FrameworkActivationState activationState;

    /// <summary>
    /// Validates the canonical Harmony provider. If any optional framework is active, validates the
    /// complete base cohort. A fully staged/inactive cohort is equally valid.
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
            prepared = true;
        }
    }

    /// <summary>
    /// Byte-verifies an explicitly active framework cohort after its load hooks have run. Exact
    /// implementation drift or incomplete loader output aborts hardened Coop startup; a verified
    /// cohort runs unmodified.
    /// </summary>
    public static void CompleteAfterOptionalModuleLoad()
    {
        lock (Sync)
        {
            if (!prepared)
                throw new InvalidOperationException(
                    "Framework compatibility boundary was not prepared from the Coop constructor.");

            if (activationState == FrameworkActivationState.StagedInactive || completed) return;

            var loaded = GetLoadedAssemblies();
            ValidateExpectedAssemblies(
                loaded,
                FrameworkCompatibilityManifest.Assemblies.Where(expectation => expectation.OptionalFramework),
                ValidatedAssemblies);
            completed = true;
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

        return FrameworkActivationState.ActiveExact;
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

/// <summary>
/// MCM values remain local presentation state on each peer. No MCM value is admitted into Friend
/// Edition's authoritative gameplay fingerprint — authoritative configuration always comes from
/// the host mod-config handshake, never from a client's local settings screens.
/// </summary>
internal static class FrameworkSettingsAuthority
{
    private static readonly string GameplayFingerprint = ComputeGameplayFingerprint();

    internal static string AuthoritativeGameplayFingerprint => GameplayFingerprint;
    internal static bool AllowsLocalMcmMutation => false;

    internal static string FingerprintAfterLocalMcmView(
        IEnumerable<KeyValuePair<string, string>> localMcmValues)
    {
        // Deliberately do not fold local values into authoritative state. This method makes the
        // zero-consumer rule executable and testable without loading MCM into the test process.
        _ = localMcmValues;
        return GameplayFingerprint;
    }

    private static string ComputeGameplayFingerprint()
    {
        string canonical = string.Join("|", new[]
        {
            FrameworkCompatibilityManifest.PolicyRevision,
            "mcm-authoritative-consumers=0",
            "mcm-values=local-presentation-only",
            "framework-runtime=active-audited",
        });
        using var sha = SHA256.Create();
        return FrameworkCompatibilityBootstrap.ToHex(
            sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
}
