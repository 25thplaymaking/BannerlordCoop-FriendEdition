using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

/// <summary>
/// Improved Garrisons can install Harmony detours during module load, before Coop's service
/// container exists. Remove every detour implemented by the exact approved assembly, then assert
/// that neither those methods nor an unidentifiable Improved-Garrisons-like owner remain.
/// </summary>
internal static class ImprovedGarrisonsHarmonyIsolation
{
    internal static int RemoveModulePatches(
        Assembly moduleAssembly,
        Harmony unpatcher,
        string allowedAdapterOwner = null)
    {
        if (moduleAssembly == null) throw new ArgumentNullException(nameof(moduleAssembly));
        if (unpatcher == null) throw new ArgumentNullException(nameof(unpatcher));

        var removed = 0;
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;

            foreach (var patch in Enumerate(patchInfo).ToArray())
            {
                if (IsModulePatch(patch, moduleAssembly))
                {
                    unpatcher.Unpatch(original, patch.PatchMethod);
                    removed++;
                    continue;
                }

                if (!string.Equals(patch?.owner, allowedAdapterOwner, StringComparison.Ordinal) &&
                    LooksLikeUnknownModuleOwner(patch?.owner))
                {
                    throw new InvalidOperationException(
                        $"Improved Garrisons failed closed: patch owner '{patch.owner}' does not resolve to the approved module assembly " +
                        $"({patch.PatchMethod?.DeclaringType?.Assembly?.FullName ?? "missing assembly"})");
                }
            }
        }

        var remaining = DescribeModulePatches(moduleAssembly).ToArray();
        if (remaining.Length != 0)
        {
            throw new InvalidOperationException(
                "Improved Garrisons failed closed: module Harmony patches remain after isolation: " +
                string.Join("; ", remaining));
        }

        AssertNoUnexpectedPatchTargets(
            moduleAssembly,
            Array.Empty<MethodInfo>(),
            allowedAdapterOwner,
            allowAdapterTargets: true);

        return removed;
    }

    internal static bool HasOwnerPatchesTargetingAssembly(Assembly assembly, string owner)
    {
        if (assembly == null || string.IsNullOrWhiteSpace(owner)) return false;
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            if (!ReferenceEquals(original?.DeclaringType?.Assembly, assembly)) continue;
            var patches = Harmony.GetPatchInfo(original);
            if (patches != null && Enumerate(patches).Any(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    internal static void AssertOnlyAdapterGuards(
        IEnumerable<(MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)> expected,
        string adapterOwner)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (string.IsNullOrWhiteSpace(adapterOwner))
            throw new ArgumentException("Adapter owner is required", nameof(adapterOwner));

        foreach (var guard in expected)
        {
            var patches = Harmony.GetPatchInfo(guard.Original);
            if (patches == null ||
                !IsExactList(patches.Prefixes, guard.Prefix, adapterOwner) ||
                !IsExactList(patches.Postfixes, guard.Postfix, adapterOwner) ||
                (patches.Transpilers?.Count ?? 0) != 0 ||
                (patches.Finalizers?.Count ?? 0) != 0)
            {
                throw new InvalidOperationException(
                    "Improved Garrisons failed closed: audited guard inventory does not exactly match the dedicated Coop adapter on " +
                    guard.Original.DeclaringType?.FullName + "." + guard.Original.Name);
            }
        }
    }

    internal static void AssertNoUnexpectedPatchTargets(
        Assembly moduleAssembly,
        IEnumerable<MethodInfo> expectedOriginals,
        string adapterOwner,
        bool allowAdapterTargets = false)
    {
        if (moduleAssembly == null) throw new ArgumentNullException(nameof(moduleAssembly));
        var expected = new HashSet<MethodBase>(expectedOriginals ?? Array.Empty<MethodInfo>());
        var unexpected = new List<string>();

        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            if (!ReferenceEquals(original?.DeclaringType?.Assembly, moduleAssembly)) continue;
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;

            foreach (var patch in Enumerate(patches))
            {
                var ownedByAdapter = string.Equals(patch.owner, adapterOwner, StringComparison.Ordinal);
                if (expected.Contains(original) && ownedByAdapter) continue;
                if (allowAdapterTargets && ownedByAdapter) continue;
                unexpected.Add(Describe(original, patch));
            }
        }

        if (unexpected.Count != 0)
        {
            throw new InvalidOperationException(
                "Improved Garrisons failed closed: an unknown Harmony patch overlaps its runtime methods: " +
                string.Join("; ", unexpected));
        }
    }

    internal static bool LooksLikeUnknownModuleOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        var normalized = new string(owner.Where(char.IsLetterOrDigit).ToArray());
        return normalized.IndexOf("improvedgarrison", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static bool IsModulePatch(Patch patch, Assembly moduleAssembly) =>
        patch?.PatchMethod?.DeclaringType?.Assembly != null &&
        ReferenceEquals(patch.PatchMethod.DeclaringType.Assembly, moduleAssembly);

    internal static IEnumerable<string> DescribeModulePatches(Assembly moduleAssembly)
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;
            foreach (var patch in Enumerate(patchInfo))
            {
                if (!IsModulePatch(patch, moduleAssembly)) continue;
                yield return $"{patch.owner}:{patch.PatchMethod?.DeclaringType?.FullName}.{patch.PatchMethod?.Name}" +
                             $"->{original.DeclaringType?.FullName}.{original.Name}";
            }
        }
    }

    private static bool IsExactList(IReadOnlyCollection<Patch> patches, MethodInfo expected, string owner)
    {
        var count = patches?.Count ?? 0;
        if (expected == null) return count == 0;
        return count == 1 &&
               patches.Single().PatchMethod == expected &&
               string.Equals(patches.Single().owner, owner, StringComparison.Ordinal);
    }

    private static string Describe(MethodBase original, Patch patch) =>
        (patch?.owner ?? "<null-owner>") + ":" +
        (patch?.PatchMethod?.DeclaringType?.FullName ?? "<null-type>") + "." +
        (patch?.PatchMethod?.Name ?? "<null-method>") + "->" +
        (original?.DeclaringType?.FullName ?? "<null-original-type>") + "." +
        (original?.Name ?? "<null-original>");

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
