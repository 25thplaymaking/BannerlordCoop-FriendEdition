using GameInterface.Configuration;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Decides which declared Workshop modules this process actually participates in. Everything
/// downstream — patch categories, sync registration, handlers — keys off these two decisions, so an
/// absent, mismatched or disabled module contributes nothing at all.
///
/// <para>
/// The two questions are separated because they are answerable at different times, and conflating
/// them would break one of them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="ResolveInstalledModules"/> — "is the pinned build loaded?" — is answerable the moment
/// the container is built, which is when Harmony categories have to be registered
/// (<c>GameInterface.PatchAll</c> runs from the container's AutoActivate). It is also already
/// symmetric across peers: <c>WorkshopManifestValidator</c> refuses a session whose members do not
/// carry the same components at the same versions.
/// </description></item>
/// <item><description>
/// <see cref="ResolveLiveModules"/> — "is the pinned build loaded AND does the host want it
/// integrated?" — additionally consults the operator's configuration, which is server-authoritative
/// and therefore only known once <c>ModConfigAuthority</c> has installed it at CampaignReady, long
/// after patching. It is the decision for consumers that run inside a live campaign.
/// </description></item>
/// </list>
/// </summary>
public static class WorkshopModuleRegistrar
{
    /// <summary>
    /// The declared modules whose pinned assembly is loaded, byte for byte. This is the only
    /// question that may gate Harmony patch-category registration: a category applied for a module
    /// that is not really there throws out of <c>Harmony.PatchCategory</c> and aborts every
    /// remaining Coop patch.
    /// </summary>
    public static IReadOnlyList<IWorkshopModule> ResolveInstalledModules(
        IEnumerable<IWorkshopModule> modules)
    {
        var installed = new List<IWorkshopModule>();
        if (modules == null) return installed;

        foreach (var module in modules)
        {
            if (module?.Fingerprint == null) continue;
            if (!module.Fingerprint.Matches(module.ResolveInstalledSha256())) continue;

            installed.Add(module);
        }

        return installed;
    }

    /// <summary>
    /// The installed modules the host has not switched off. Enablement travels inside
    /// <see cref="ModOptions"/>, so a client cannot opt itself in or out of a module the host
    /// decided about.
    /// </summary>
    public static IReadOnlyList<IWorkshopModule> ResolveLiveModules(
        IEnumerable<IWorkshopModule> modules,
        ModOptions options)
    {
        var live = new List<IWorkshopModule>();

        foreach (var module in ResolveInstalledModules(modules))
        {
            if (!options.IsWorkshopModuleEnabled(module.ModuleId)) continue;

            live.Add(module);
        }

        return live;
    }
}
