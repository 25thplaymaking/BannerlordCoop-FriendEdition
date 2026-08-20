using Common;
using GameInterface.Services.Clans.Extensions;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Banners;

/// <summary>
/// Whether a loaded conversion owns clan banner COLOUR as client-side presentation.
/// </summary>
/// <remarks>
/// Detected by assembly name, the same way <c>ThirdPartyDebugSuppression</c> works, so a loadout
/// without the module is a no-op and nothing here references a third-party type at compile time.
/// <para>
/// Empires of Europe 1100 defines no banner ICONS at all — its <c>banner_icons.xml</c> adds 15 COLOURS
/// (ids 37, 40, 117, 124-145) and zero icons and zero icon groups — so its heraldry is entirely a
/// colour treatment applied at runtime by <c>BannerColorPersistence</c>. That module patches
/// <c>SandBox.View.Map.Visuals.MobilePartyVisual</c>, a view type that does not exist on a headless
/// host, which is why it is tagged <c>DedicatedServerType="none"</c> and is deliberately absent from
/// the dedicated force-load allowlist in <c>tools/CoopServerModKit/StartupHook.cs</c>.
/// </para>
/// </remarks>
internal static class BannerColourPresentation
{
    private const string BannerColourModuleAssembly = "BannerColorPersistence";

    private static bool resolved;
    private static bool present;

    /// <summary>
    /// True when a replicated banner update should apply the code but leave this clan's colours alone.
    /// </summary>
    internal static bool ShouldKeepLocalColours(Clan clan)
    {
        // The server is the one computing the value; it must never skip its own assignment.
        if (ModInformation.IsServer || clan == null) return false;

        // A player's banner edit is a real shared decision and stays authoritative.
        if (clan.IsPlayerClan()) return false;

        return IsBannerColourModulePresent();
    }

    private static bool IsBannerColourModulePresent()
    {
        if (resolved) return present;

        try
        {
            present = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => string.Equals(
                assembly.GetName().Name, BannerColourModuleAssembly, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            present = false;
        }

        resolved = true;
        return present;
    }
}
