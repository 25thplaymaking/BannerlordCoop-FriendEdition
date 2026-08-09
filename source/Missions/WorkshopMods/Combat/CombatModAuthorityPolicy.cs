using Common;

namespace Missions.WorkshopMods.Combat;

/// <summary>
/// The authority rules used by optional combat-mod adapters.  Keep these rules independent of
/// reflection/Harmony so a changed or missing workshop assembly cannot weaken them.
/// </summary>
internal static class CombatModAuthorityPolicy
{
    internal static bool AllowMissionGameplayDecision(
        bool isCoopBattleActive,
        bool sourceIsLocallyControlled)
    {
        return !isCoopBattleActive || sourceIsLocallyControlled;
    }

    internal static bool AllowCampaignMutation(bool isServer)
    {
        return isServer;
    }

    internal static bool AllowClientPresentation(bool isServer)
    {
        return !isServer;
    }

    /// <summary>
    /// DismembermentPlus currently derives the severed limb from a local RegisterBlow callback.
    /// Coop routes that callback only to the victim-authority peer, so allowing it in a live Coop
    /// battle would show different bodies on different clients.  Keep the presentation disabled
    /// until a stable hit id, selected limb and seed are carried by a deduplicated network event.
    /// </summary>
    internal static bool AllowDismembermentPresentation(
        bool moduleCompatible,
        bool isServer,
        bool isCoopBattleActive)
    {
        return moduleCompatible && !isServer && !isCoopBattleActive;
    }

    internal static bool AllowRbmMissionBehaviorInitialization(
        bool moduleCompatible,
        bool expectedMethodShape,
        bool isServer,
        bool isCoopBattleActive)
    {
        return moduleCompatible
            && expectedMethodShape
            && (!isCoopBattleActive || isServer);
    }

    internal static bool AllowRbmGameInitializationFinished(
        bool moduleCompatible,
        bool expectedMethodShape,
        bool isServer)
    {
        return moduleCompatible
            && expectedMethodShape
            && AllowCampaignMutation(isServer);
    }

    internal static bool AllowCampaignMutation() =>
        AllowCampaignMutation(ModInformation.IsServer);

    internal static bool AllowClientPresentation() =>
        AllowClientPresentation(ModInformation.IsServer);
}
