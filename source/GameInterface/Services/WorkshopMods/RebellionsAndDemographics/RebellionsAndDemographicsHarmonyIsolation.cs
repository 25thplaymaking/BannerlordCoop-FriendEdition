using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

internal static class RebellionsAndDemographicsHarmonyIsolation
{
    internal const string UpstreamOwner = "com.rebellions.and.demographics";
    internal const string AdapterOwner = "Bannerlord.Coop.RebellionsAndDemographics.1.4.8";

    internal static void Purge(Assembly assembly, Harmony adapter)
    {
        if (assembly == null || adapter == null) throw new ArgumentNullException(assembly == null ? nameof(assembly) : nameof(adapter));
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var patch in Enumerate(info).ToArray())
            {
                if (string.Equals(patch.owner, UpstreamOwner, StringComparison.Ordinal) ||
                    Equals(patch.PatchMethod?.DeclaringType?.Assembly, assembly))
                    adapter.Unpatch(original, patch.PatchMethod);
            }
        }

        var remaining = Harmony.GetAllPatchedMethods()
            .SelectMany(original =>
            {
                var info = Harmony.GetPatchInfo(original);
                return info == null ? Enumerable.Empty<Patch>() : Enumerate(info);
            })
            .Where(patch => string.Equals(patch.owner, UpstreamOwner, StringComparison.Ordinal) ||
                            Equals(patch.PatchMethod?.DeclaringType?.Assembly, assembly))
            .ToArray();
        if (remaining.Length != 0)
            throw new InvalidOperationException("R&D Harmony isolation failed: upstream patches remain after purge.");
    }

    private static IEnumerable<Patch> Enumerate(Patches patches)
    {
        if (patches.Prefixes != null) foreach (var patch in patches.Prefixes) yield return patch;
        if (patches.Postfixes != null) foreach (var patch in patches.Postfixes) yield return patch;
        if (patches.Transpilers != null) foreach (var patch in patches.Transpilers) yield return patch;
        if (patches.Finalizers != null) foreach (var patch in patches.Finalizers) yield return patch;
    }
}
