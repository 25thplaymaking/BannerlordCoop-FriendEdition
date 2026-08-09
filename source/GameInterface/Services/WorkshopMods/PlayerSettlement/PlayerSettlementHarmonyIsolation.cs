using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

/// <summary>
/// Player Settlement and its fixes DLL both install Harmony patches during module load, before the
/// Coop service container exists. Feature-blocking only the public construction methods is not
/// sufficient: several of those earlier patches mutate vanilla campaign behaviors directly. This
/// boundary removes every patch whose implementation method comes from either exact validated DLL,
/// then verifies the global Harmony inventory before Coop-owned guards are installed.
/// </summary>
internal static class PlayerSettlementHarmonyIsolation
{
    internal const string MainHarmonyOwner = "com.b0tlanner.bannerlord.bannerlordplayersettlement";
    internal const string FixesHarmonyOwner = "com.b0tlanner.bannerlord.playersettlement.fixes";
    internal const string AdapterHarmonyOwner =
        "Bannerlord.Coop.WorkshopMods.PlayerSettlement.Guard.v1";

    internal static int RemoveModulePatches(
        Assembly mainAssembly,
        Assembly fixesAssembly,
        Harmony unpatcher)
    {
        if (unpatcher == null)
            throw new ArgumentNullException(nameof(unpatcher));

        var removed = 0;
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;

            foreach (var patch in Enumerate(patchInfo).ToArray())
            {
                if (!IsModulePatch(patch, mainAssembly, fixesAssembly)) continue;
                unpatcher.Unpatch(original, patch.PatchMethod);
                removed++;
            }
        }

        var remaining = DescribeModulePatches(mainAssembly, fixesAssembly).ToArray();
        if (remaining.Length != 0)
        {
            throw new InvalidOperationException(
                "Player Settlement failed closed: original/fixes Harmony patches remain after isolation: " +
                string.Join("; ", remaining));
        }
        return removed;
    }

    internal static bool IsModulePatch(
        Patch patch,
        Assembly mainAssembly,
        Assembly fixesAssembly)
    {
        return patch != null &&
               (IsModulePatchOwner(patch.owner) ||
                IsModulePatchMethod(patch.PatchMethod, mainAssembly, fixesAssembly));
    }

    internal static bool IsModulePatchOwner(string owner) =>
        string.Equals(owner, MainHarmonyOwner, StringComparison.Ordinal) ||
        string.Equals(owner, FixesHarmonyOwner, StringComparison.Ordinal);

    internal static bool IsModulePatchMethod(
        MethodInfo patchMethod,
        Assembly mainAssembly,
        Assembly fixesAssembly)
    {
        var patchAssembly = patchMethod?.DeclaringType?.Assembly;
        return patchAssembly != null &&
               (ReferenceEquals(patchAssembly, mainAssembly) ||
                ReferenceEquals(patchAssembly, fixesAssembly));
    }

    internal static IEnumerable<string> DescribeModulePatches(
        Assembly mainAssembly,
        Assembly fixesAssembly)
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;
            foreach (var patch in Enumerate(patchInfo))
            {
                if (!IsModulePatch(patch, mainAssembly, fixesAssembly)) continue;
                yield return $"{patch.owner}:{patch.PatchMethod?.DeclaringType?.FullName}.{patch.PatchMethod?.Name}->{original.DeclaringType?.FullName}.{original.Name}";
            }
        }
    }

    /// <summary>
    /// The audited optional entry points must be pristine after the module-owned patches are
    /// removed. Silently composing an unknown third-party prefix/postfix/transpiler/finalizer with
    /// a fail-closed guard would make the resulting call order unknowable.
    /// </summary>
    internal static void AssertNoPatchOverlap(IEnumerable<MethodInfo> originals)
    {
        foreach (var original in (originals ?? Array.Empty<MethodInfo>()).Distinct())
        {
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null || !Enumerate(patches).Any()) continue;

            throw new InvalidOperationException(
                "Player Settlement failed closed: an audited entry point already has an unknown Harmony patch: " +
                DescribePatches(original, patches));
        }
    }

    /// <summary>
    /// Proves that every audited original has exactly its expected Coop prefix, that no other
    /// patch category or owner overlaps it, and that the dedicated owner has no extra detours.
    /// This is run after first installation and again whenever a later handler takes the cached
    /// assembly path.
    /// </summary>
    internal static void AssertExactAdapterPatchInventory(
        IEnumerable<(MethodInfo Original, MethodInfo Prefix)> expectedPatches)
    {
        var expected = (expectedPatches ??
                        Array.Empty<(MethodInfo Original, MethodInfo Prefix)>())
            .ToArray();
        if (expected.Length == 0 ||
            expected.Any(pair => pair.Original == null || pair.Prefix == null) ||
            expected.Select(pair => pair.Original).Distinct().Count() != expected.Length)
        {
            throw new InvalidOperationException(
                "Player Settlement failed closed: the expected adapter patch inventory is invalid");
        }

        var expectedOriginals = new HashSet<MethodBase>(
            expected.Select(pair => (MethodBase)pair.Original));
        foreach (var pair in expected)
        {
            var patches = Harmony.GetPatchInfo(pair.Original);
            var prefixes = patches?.Prefixes?.ToArray() ?? Array.Empty<Patch>();
            var postfixes = patches?.Postfixes?.ToArray() ?? Array.Empty<Patch>();
            var transpilers = patches?.Transpilers?.ToArray() ?? Array.Empty<Patch>();
            var finalizers = patches?.Finalizers?.ToArray() ?? Array.Empty<Patch>();
            if (prefixes.Length != 1 ||
                !string.Equals(prefixes[0].owner, AdapterHarmonyOwner, StringComparison.Ordinal) ||
                !Equals(prefixes[0].PatchMethod, pair.Prefix) ||
                postfixes.Length != 0 ||
                transpilers.Length != 0 ||
                finalizers.Length != 0)
            {
                throw new InvalidOperationException(
                    "Player Settlement failed closed: adapter Harmony inventory mismatch for " +
                    pair.Original.DeclaringType?.FullName + "." + pair.Original.Name + ": " +
                    DescribePatches(pair.Original, patches));
            }
        }

        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;
            foreach (var patch in Enumerate(patches))
            {
                if (!string.Equals(
                        patch.owner,
                        AdapterHarmonyOwner,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!expectedOriginals.Contains(original))
                {
                    throw new InvalidOperationException(
                        "Player Settlement failed closed: the dedicated adapter owner has an unexpected detour: " +
                        $"{patch.PatchMethod?.DeclaringType?.FullName}.{patch.PatchMethod?.Name}->" +
                        $"{original.DeclaringType?.FullName}.{original.Name}");
                }
            }
        }
    }

    private static string DescribePatches(MethodBase original, Patches patches)
    {
        if (patches == null) return "no Harmony inventory";
        var descriptions = new List<string>();
        AddDescriptions(descriptions, "prefix", patches.Prefixes);
        AddDescriptions(descriptions, "postfix", patches.Postfixes);
        AddDescriptions(descriptions, "transpiler", patches.Transpilers);
        AddDescriptions(descriptions, "finalizer", patches.Finalizers);
        return descriptions.Count == 0
            ? $"empty inventory on {original.DeclaringType?.FullName}.{original.Name}"
            : string.Join(", ", descriptions);
    }

    private static void AddDescriptions(
        ICollection<string> output,
        string kind,
        IEnumerable<Patch> patches)
    {
        if (patches == null) return;
        foreach (var patch in patches)
        {
            output.Add(
                $"{kind}:{patch.owner}:{patch.PatchMethod?.DeclaringType?.FullName}." +
                patch.PatchMethod?.Name);
        }
    }

    private static IEnumerable<Patch> Enumerate(Patches patches)
    {
        if (patches.Prefixes != null)
            foreach (var patch in patches.Prefixes) yield return patch;
        if (patches.Postfixes != null)
            foreach (var patch in patches.Postfixes) yield return patch;
        if (patches.Transpilers != null)
            foreach (var patch in patches.Transpilers) yield return patch;
        if (patches.Finalizers != null)
            foreach (var patch in patches.Finalizers) yield return patch;
    }
}
