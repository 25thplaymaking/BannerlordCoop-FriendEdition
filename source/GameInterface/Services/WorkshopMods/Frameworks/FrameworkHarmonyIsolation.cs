using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Frameworks;

/// <summary>
/// Removes the framework cohort's patches by both exact implementation assembly and audited owner
/// domain. The assembly check catches renamed/variable owners; the owner check catches wrapper-owned
/// patches whose helper implementation lives in another bundled assembly.
/// </summary>
internal static class FrameworkHarmonyIsolation
{
    private static readonly string[] OriginalOwnerPrefixes =
    {
        "Bannerlord.ButterLib.",
        "butterlib.",
        "bannerlord.uiextender.ex",
        "Bannerlord.MBOptionScreen",
        "MCM.UI.Adapter.MCMv5",
        "bannerlord.mcm.",
    };

    internal static int RemoveOriginalFrameworkPatches(
        IEnumerable<Assembly> frameworkAssemblies,
        Harmony unpatcher)
    {
        if (unpatcher == null) throw new ArgumentNullException(nameof(unpatcher));
        var assemblies = new HashSet<Assembly>(
            (frameworkAssemblies ?? Array.Empty<Assembly>())
                .Where(IsOptionalFrameworkAssembly));

        // See HarmonyPatchInfoStabilizer for why this retries the whole scan-then-unpatch pass, not
        // just a verification afterward: HarmonyLib hands back the PatchMethod to unpatch from
        // GetPatchInfo, and that value has been observed to transiently deserialize to the wrong
        // MethodInfo. Unpatching the wrong (unrelated) method leaves the real target patched, so only
        // re-scanning and re-attempting the removal recovers from it.
        int removed = 0;
        HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(() =>
        {
            var passRemoved = 0;
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
            {
                Patches patchInfo = Harmony.GetPatchInfo(original);
                if (patchInfo == null) continue;
                foreach (Patch patch in Enumerate(patchInfo).ToArray())
                {
                    if (!IsOriginalFrameworkPatch(patch, assemblies)) continue;
                    unpatcher.Unpatch(original, patch.PatchMethod);
                    passRemoved++;
                }
            }

            removed += passRemoved;
            return !DescribeOriginalFrameworkPatches(frameworkAssemblies).Any();
        });

        return removed;
    }

    internal static void AssertNoOriginalFrameworkPatches(IEnumerable<Assembly> frameworkAssemblies)
    {
        string[] remaining = DescribeOriginalFrameworkPatches(frameworkAssemblies).ToArray();
        if (remaining.Length == 0) return;
        throw new InvalidOperationException(
            "Workshop framework isolation failed closed; original patches remain: " +
            string.Join("; ", remaining));
    }

    internal static IEnumerable<string> DescribeOriginalFrameworkPatches(
        IEnumerable<Assembly> frameworkAssemblies)
    {
        var assemblies = new HashSet<Assembly>(
            (frameworkAssemblies ?? Array.Empty<Assembly>())
                .Where(IsOptionalFrameworkAssembly));
        foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
        {
            Patches patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;
            foreach (Patch patch in Enumerate(patchInfo))
            {
                if (!IsOriginalFrameworkPatch(patch, assemblies)) continue;
                yield return
                    $"{patch.owner}:{patch.PatchMethod?.DeclaringType?.FullName}." +
                    $"{patch.PatchMethod?.Name}->{original.DeclaringType?.FullName}.{original.Name}";
            }
        }
    }

    internal static bool IsOriginalFrameworkPatch(Patch patch, ISet<Assembly> frameworkAssemblies)
    {
        if (patch == null) return false;
        Assembly patchAssembly = patch.PatchMethod?.DeclaringType?.Assembly;
        return IsOriginalOwner(patch.owner) ||
               (patchAssembly != null && frameworkAssemblies?.Contains(patchAssembly) == true);
    }

    internal static bool IsOriginalOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) ||
            string.Equals(
                owner,
                FrameworkCompatibilityManifest.AdapterHarmonyId,
                StringComparison.Ordinal))
            return false;

        return OriginalOwnerPrefixes.Any(prefix =>
            owner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsOptionalFrameworkAssembly(Assembly assembly)
    {
        string name = assembly?.GetName().Name;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return FrameworkCompatibilityManifest.Assemblies.Any(expectation =>
            expectation.OptionalFramework &&
            string.Equals(expectation.AssemblyName, name, StringComparison.Ordinal));
    }

    private static IEnumerable<Patch> Enumerate(Patches patches)
    {
        if (patches.Prefixes != null)
            foreach (Patch patch in patches.Prefixes) yield return patch;
        if (patches.Postfixes != null)
            foreach (Patch patch in patches.Postfixes) yield return patch;
        if (patches.Transpilers != null)
            foreach (Patch patch in patches.Transpilers) yield return patch;
        if (patches.Finalizers != null)
            foreach (Patch patch in patches.Finalizers) yield return patch;
    }
}
