using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.ViewModelCollection.KingdomManagement.Diplomacy;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Final fail-closed boundary before native Kingdom diplomacy rows instantiate Diplomacy's
/// UIExtender mixins. The join handshake normally makes this a validation-only check; retaining it
/// here also repairs a singleton reset from the trusted snapshot instead of allowing a black screen.
/// </summary>
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
[HarmonyPatch(typeof(KingdomDiplomacyVM), nameof(KingdomDiplomacyVM.RefreshValues))]
internal static class DiplomacyKingdomUiReadinessPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyKingdomUiReadinessPatch));
    private static string lastFailure;

    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!ModInformation.IsClient) return true;

        if (!ContainerProvider.TryResolve<DiplomacyCompatibilityHandler>(out var handler))
            return Block("The Diplomacy compatibility handler was unavailable.");
        if (handler.TryEnsureClientUiReady(out var failure))
        {
            lastFailure = null;
            return true;
        }

        return Block(failure);
    }

    private static bool Block(string failure)
    {
        if (!string.Equals(lastFailure, failure, System.StringComparison.Ordinal))
        {
            lastFailure = failure;
            Logger.Error(
                "Blocked Kingdom diplomacy refresh because authoritative Diplomacy UI state was not ready: {Failure}",
                failure);
        }
        return false;
    }
}
