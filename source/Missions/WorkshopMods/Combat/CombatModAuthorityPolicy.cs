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

    /// <summary>DismembermentPlus's mission behavior is presentation-only and never loads headless.</summary>
    internal static bool AllowDismembermentPresentation(
        bool moduleCompatible,
        bool isServer,
        bool isCoopBattleActive)
    {
        return moduleCompatible && !isServer;
    }

    /// <summary>
    /// In Coop the original randomized RegisterBlow implementation is replaced. Only the peer that
    /// owns the accepted victim blow may select an outcome, and only while the server-published
    /// replicated-presentation capability is enabled.
    /// </summary>
    internal static bool AllowDismembermentAcceptedBlow(
        bool moduleCompatible,
        bool isServer,
        bool isCoopBattleActive,
        bool routeEnabled,
        bool victimIsLocallyAuthoritative)
    {
        if (!moduleCompatible || isServer) return false;
        if (!isCoopBattleActive) return true;
        return routeEnabled && victimIsLocallyAuthoritative;
    }

    internal static bool AllowDismembermentCapability(
        bool guardInitialized,
        bool moduleCompatible,
        bool moduleEnabled) =>
        guardInitialized && moduleCompatible && moduleEnabled;

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
