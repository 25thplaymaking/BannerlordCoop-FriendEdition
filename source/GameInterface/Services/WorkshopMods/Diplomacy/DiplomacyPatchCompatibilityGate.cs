using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy applies several unannotated Harmony patches under its own domains before/while a
/// campaign starts. Clients retain none of those patches because even calculation patches can
/// consume mutable local MCM settings. A supported server retains only the explicitly audited
/// clan-politics formula; every other original owner is removed by patch-method identity.
/// </summary>
internal static class DiplomacyPatchCompatibilityGate
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyPatchCompatibilityGate));

    // The server keeps one audited formula patch. It consumes the server's canonical Diplomacy
    // settings and composes with Coop's inner influence calculation. Every other original owner
    // is either a duplicate mutation funnel or headless-only UI work and is removed fail-closed.
    private static readonly HashSet<string> ServerAllowedPatchTypes = new(StringComparer.Ordinal)
    {
        "Diplomacy.Patches.DefaultClanPoliticsModelPatch",
    };

    internal static bool IsConflictingPatchType(string fullName) =>
        fullName != null && !ServerAllowedPatchTypes.Contains(fullName);

    internal static bool IsServerAllowedPatchType(string fullName) =>
        fullName != null && ServerAllowedPatchTypes.Contains(fullName);

    internal static int RemoveConflictingPatches()
        => RemovePatches(removeEveryDiplomacyPatch: false);

    internal static int RemoveAllDiplomacyPatches()
        => RemovePatches(removeEveryDiplomacyPatch: true);

    /// <summary>
    /// True if a forbidden Diplomacy-owned patch is currently installed. Callers that need this to be
    /// false (post-cleanup verification, and the one test that checks it directly) get the benefit of
    /// HarmonyPatchInfoStabilizer automatically: a scan that finds something forbidden is retried
    /// before being trusted, since a stale/corrupted GetPatchInfo read is indistinguishable from a real
    /// forbidden patch until it is re-checked. A genuinely-forbidden patch reports true on every retry.
    /// </summary>
    internal static bool HasForbiddenPatches(bool removeEveryDiplomacyPatch) =>
        !HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(
            () => !ScanForForbiddenPatches(removeEveryDiplomacyPatch));

    private static bool ScanForForbiddenPatches(bool removeEveryDiplomacyPatch)
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var patch in EnumeratePatches(info))
            {
                if (patch?.PatchMethod == null || patch.owner == null ||
                    !patch.owner.StartsWith("bannerlord.diplomacy", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (removeEveryDiplomacyPatch ||
                    !IsServerAllowedPatchType(patch.PatchMethod.DeclaringType?.FullName))
                    return true;
            }
        }

        return false;
    }

    internal static void RemoveAndAssert(bool removeEveryDiplomacyPatch)
    {
        // See HarmonyPatchInfoStabilizer for why this retries the whole remove-then-verify cycle, not
        // only the verification: RemovePatches itself unpatches by the PatchMethod HarmonyLib hands
        // back from GetPatchInfo, and that value has been observed to transiently deserialize to the
        // wrong MethodInfo. Unpatching the wrong (unrelated) method leaves the real target patched, so
        // only re-reading and re-attempting the removal — not just re-checking — recovers from it.
        var clean = HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(() =>
        {
            RemovePatches(removeEveryDiplomacyPatch);
            return !HasForbiddenPatches(removeEveryDiplomacyPatch);
        });
        if (clean) return;

        throw new InvalidOperationException(
            removeEveryDiplomacyPatch
                ? "Unsupported Diplomacy Harmony patches remained after cleanup."
                : "Conflicting Diplomacy Harmony patches remained after cleanup.");
    }

    internal static bool ShouldRemoveEveryDiplomacyPatch(bool implementationSupported) =>
        ModInformation.IsClient || !implementationSupported;

    private static int RemovePatches(bool removeEveryDiplomacyPatch)
    {
        int removed = 0;
        var harmony = new Harmony(GameInterfaceModule.HarmonyId);

        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;

            foreach (var patch in EnumeratePatches(info).ToArray())
            {
                if (patch?.PatchMethod == null ||
                    patch.owner == null ||
                    !patch.owner.StartsWith("bannerlord.diplomacy", StringComparison.OrdinalIgnoreCase) ||
                    (!removeEveryDiplomacyPatch &&
                     IsServerAllowedPatchType(patch.PatchMethod.DeclaringType?.FullName)))
                {
                    continue;
                }

                harmony.Unpatch(original, patch.PatchMethod);
                removed++;
            }
        }

        if (removed > 0)
            Logger.Information(
                "Removed {Count} {Scope} Diplomacy Harmony patches; Coop owns those mutation funnels.",
                removed,
                removeEveryDiplomacyPatch ? "unsupported" : "conflicting");
        return removed;
    }

    private static IEnumerable<Patch> EnumeratePatches(Patches patches)
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

/// <summary>Remove Diplomacy's main-domain conflicts already installed at module load.</summary>
[HarmonyPatch]
internal static class DiplomacyMainPatchCleanup
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool supported = DiplomacyCompatibilityPolicy.TryResolveSupportedAssembly(out _, out _);
        bool removeAll = DiplomacyPatchCompatibilityGate.ShouldRemoveEveryDiplomacyPatch(supported);
        DiplomacyPatchCompatibilityGate.RemoveAndAssert(removeAll);
        return false;
    }
}

/// <summary>
/// A DLL with the expected filename but a different digest or patch surface is never allowed to
/// install campaign behaviors. Its module-load UI may already exist, but all of its Harmony
/// owners are removed above and campaign startup fails closed here.
/// </summary>
[HarmonyPatch]
internal static class DiplomacyUnsupportedImplementationGate
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var assembly in DiplomacyCompatibilityPolicy.ResolveCandidateAssemblies())
        {
            Type type;
            try
            {
                type = assembly.GetType(
                    DiplomacyCompatibilityPolicy.SubModuleTypeName,
                    throwOnError: false,
                    ignoreCase: false);
            }
            catch (Exception)
            {
                continue;
            }
            if (type == null) continue;

            foreach (var method in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // The unsupported surface is untrusted, so patch every OnGameStart overload
                // instead of assuming the audited arity.
                if (method.Name == "OnGameStart") yield return method;
            }
        }
    }

    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix()
    {
        bool supported = DiplomacyCompatibilityPolicy.TryResolveSupportedAssembly(out _, out var failure);
        if (!supported)
        {
            DiplomacyPatchCompatibilityGate.RemoveAndAssert(removeEveryDiplomacyPatch: true);
            LogManager.GetLogger(typeof(DiplomacyUnsupportedImplementationGate))
                .Error("Blocked unsupported Diplomacy campaign startup: {Failure}", failure);
        }
        return supported;
    }
}

/// <summary>Diplomacy installs its campaign-domain rebel patches from SubModule.OnGameStart.</summary>
[HarmonyPatch]
internal static class DiplomacyCampaignPatchCleanup
{
    [HarmonyPrepare]
    private static bool Prepare() =>
        DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SubModuleTypeName) != null;

    private static MethodBase TargetMethod()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SubModuleTypeName);
        return type == null
            ? null
            : AccessTools.Method(type, "OnGameStart");
    }

    [HarmonyPostfix]
    private static void Postfix() => DiplomacyPatchCompatibilityGate.RemoveAndAssert(
        removeEveryDiplomacyPatch: ModInformation.IsClient);
}
