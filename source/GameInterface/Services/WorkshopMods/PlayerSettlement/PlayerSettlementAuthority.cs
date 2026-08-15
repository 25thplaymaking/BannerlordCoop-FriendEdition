using Common;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal interface IPlayerSettlementPatchRuntime
{
    void NotifyFeatureBlocked(string method);
    void AddBehavior(object campaignGameStarter);
    void ValidateObjectRegistration(bool isSavedCampaign);
}

internal static class PlayerSettlementPatchRuntime
{
    internal static IPlayerSettlementPatchRuntime Current { get; set; }
}

/// <summary>
/// Exact role boundary for Player Settlement 7.5.0. The behavior and client UI remain live, while
/// XML registration, persistence, completion, and future construction commits are host-owned.
/// </summary>
internal static class PlayerSettlementAuthorityPatches
{
    internal static bool BootstrapPersistenceBehaviorPrefix(object __0)
    {
        var runtime = PlayerSettlementPatchRuntime.Current ??
            throw new System.InvalidOperationException(
                "Player Settlement compatibility runtime is unavailable during behavior bootstrap");
        runtime.AddBehavior(__0);

        // The original also adds optional compatibility behaviors with unaudited authority. The
        // exact PlayerSettlementBehaviour above is the only admitted behavior on either role.
        return false;
    }

    internal static bool GuardedObjectRegistrationPrefix(bool __0)
    {
        if (ModInformation.IsServer)
        {
            var runtime = PlayerSettlementPatchRuntime.Current ??
                throw new System.InvalidOperationException(
                    "Player Settlement compatibility runtime is unavailable during object registration");
            runtime.ValidateObjectRegistration(__0);
        }

        // On the host, Player Settlement loads the validated generated XML before Coop's registry
        // enumeration. RegisterAllObjects then captures the complete resulting object graph. A
        // client never loads XML from disk; it receives that graph from the authoritative host.
        return ModInformation.IsServer;
    }

    internal static bool ServerPersistencePrefix() => ModInformation.IsServer;
    internal static bool ServerLifecyclePrefix() => ModInformation.IsServer;
    internal static bool RoleLifecyclePrefix() => true;
    internal static bool ClientPresentationPrefix() => ModInformation.IsClient;

    internal static bool BlockedPrefix(MethodBase __originalMethod)
    {
        PlayerSettlementPatchRuntime.Current?.NotifyFeatureBlocked(
            $"{__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}");
        return false;
    }
}
