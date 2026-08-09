using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Harmony surface audited from the exact Fourberie 1.4.7.5 creator binary. That DLL has no
/// 0Harmony assembly reference, Harmony attributes, patch classes, PatchAll call, or declared
/// owner id. Consequently any runtime patch implemented by that exact assembly is an unknown
/// surface: remove it by exact assembly identity, verify removal, then fail startup rather than
/// silently accepting behavior absent from the approved digest audit.
/// </summary>
internal static class FourberieHarmonyIsolation
{
    internal static readonly IReadOnlyCollection<string> AuditedOwnerIds = Array.Empty<string>();
    internal static readonly IReadOnlyCollection<string> AuditedPatchIdentities = Array.Empty<string>();
    private static string rejectedSurface;

    internal static int PurgeAndAssertAuditedSurface(Assembly fourberieAssembly, Harmony unpatcher)
    {
        if (fourberieAssembly == null) throw new ArgumentNullException(nameof(fourberieAssembly));
        if (unpatcher == null) throw new ArgumentNullException(nameof(unpatcher));
        if (rejectedSurface != null) throw new InvalidOperationException(rejectedSurface);

        var unknown = new List<string>();
        var removed = 0;
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;

            foreach (var item in Enumerate(patches).ToArray())
            {
                var patch = item.Patch;
                if (!IsAttributableToApprovedModule(patch, fourberieAssembly)) continue;

                var identity = Describe(original, item.Kind, patch);
                // The approved binary declares no Harmony surface. Retain the explicit catalog so
                // a later supported digest must name every owner/original/patch method before it
                // can be accepted here.
                if (!AuditedPatchIdentities.Contains(identity, StringComparer.Ordinal) ||
                    !AuditedOwnerIds.Contains(patch.owner, StringComparer.Ordinal))
                    unknown.Add(identity);

                unpatcher.Unpatch(original, patch.PatchMethod);
                removed++;
            }
        }

        var remaining = DescribeAssemblyPatches(fourberieAssembly).ToArray();
        if (remaining.Length != 0)
        {
            rejectedSurface =
                "Fourberie failed closed: assembly-owned Harmony patches remain after purge: " +
                string.Join("; ", remaining);
            throw new InvalidOperationException(rejectedSurface);
        }

        if (unknown.Count != 0)
        {
            rejectedSurface =
                "Fourberie failed closed after removing an unknown Harmony surface not declared by the approved binary: " +
                string.Join("; ", unknown) + ". Restart is required; this process remains tainted.";
            throw new InvalidOperationException(rejectedSurface);
        }

        return removed;
    }

    internal static void AssertOnlyAdapterGuards(
        IEnumerable<(MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)> expected,
        string adapterOwner)
    {
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        if (string.IsNullOrWhiteSpace(adapterOwner)) throw new ArgumentException("Adapter owner is required", nameof(adapterOwner));

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
                    "Fourberie failed closed: audited guard inventory does not exactly match the Coop adapter on " +
                    guard.Original.DeclaringType?.FullName + "." + guard.Original.Name);
            }
        }
    }

    internal static bool IsImplementedBy(Patch patch, Assembly assembly) =>
        IsImplementedBy(patch?.PatchMethod, assembly);

    internal static bool IsImplementedBy(MethodInfo patchMethod, Assembly assembly) =>
        patchMethod?.DeclaringType?.Assembly != null &&
        ReferenceEquals(patchMethod.DeclaringType.Assembly, assembly);

    internal static bool IsAttributableToApprovedModule(Patch patch, Assembly assembly) =>
        IsImplementedBy(patch, assembly) ||
        (patch?.owner != null && AuditedOwnerIds.Contains(patch.owner, StringComparer.Ordinal));

    internal static IEnumerable<string> DescribeAssemblyPatches(Assembly assembly)
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;
            foreach (var item in Enumerate(patches))
            {
                if (IsAttributableToApprovedModule(item.Patch, assembly))
                    yield return Describe(original, item.Kind, item.Patch);
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

    private static string Describe(MethodBase original, string kind, Patch patch) =>
        (patch?.owner ?? "<null-owner>") + ":" + kind + ":" +
        (patch?.PatchMethod?.DeclaringType?.FullName ?? "<null-type>") + "." +
        (patch?.PatchMethod?.Name ?? "<null-method>") + "->" +
        (original?.DeclaringType?.FullName ?? "<null-original-type>") + "." +
        (original?.Name ?? "<null-original>");

    private static IEnumerable<(string Kind, Patch Patch)> Enumerate(Patches patches)
    {
        if (patches.Prefixes != null)
            foreach (var patch in patches.Prefixes) yield return ("prefix", patch);
        if (patches.Postfixes != null)
            foreach (var patch in patches.Postfixes) yield return ("postfix", patch);
        if (patches.Transpilers != null)
            foreach (var patch in patches.Transpilers) yield return ("transpiler", patch);
        if (patches.Finalizers != null)
            foreach (var patch in patches.Finalizers) yield return ("finalizer", patch);
    }
}
