using Common;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal interface IPlayerSettlementPatchRuntime
{
    void NotifyFeatureBlocked(string method);
    void AddPersistenceBehavior(object campaignGameStarter);
    void ValidateEmptyObjectRegistration(bool isSavedCampaign);
}

internal static class PlayerSettlementPatchRuntime
{
    internal static IPlayerSettlementPatchRuntime Current { get; set; }
}

/// <summary>
/// Player Settlement 7.5.0 performs construction as a single local UI operation. It chooses a
/// random template and ID, reads MainHero/MainParty, loads generated XML into MBObjectManager,
/// mutates many campaign behaviors, and finally saves/reloads the game. Running any part of that
/// sequence on a client or trying to replay only its tail on the host is not atomic. The adapter
/// therefore keeps host persistence readable but denies the feature entry points on both roles.
/// </summary>
internal static class PlayerSettlementAuthorityPatches
{
    internal static bool BootstrapPersistenceBehaviorPrefix(object __0)
    {
        if (ModInformation.IsServer)
        {
            var runtime = PlayerSettlementPatchRuntime.Current ??
                throw new System.InvalidOperationException(
                    "Player Settlement compatibility runtime is unavailable during behavior bootstrap");
            runtime.AddPersistenceBehavior(__0);
        }

        // The original method also adds optional compatibility behaviors whose ticks and random
        // state have not been audited for Coop. Only the reflection-created persistence behavior
        // above is admitted on the host.
        return false;
    }

    internal static bool GuardedObjectRegistrationPrefix(bool __0)
    {
        if (ModInformation.IsServer)
        {
            var runtime = PlayerSettlementPatchRuntime.Current ??
                throw new System.InvalidOperationException(
                    "Player Settlement compatibility runtime is unavailable during object registration");
            runtime.ValidateEmptyObjectRegistration(__0);
        }

        // Never invoke Player Settlement's original RegisterSubModuleObjects: it calls LoadXml and
        // creates a dynamic object graph before Coop registries exist. ValidateEmptyObjectRegistration
        // reads only the save metadata and aborts if that graph would be needed.
        return false;
    }

    internal static bool ServerPersistencePrefix() => ModInformation.IsServer;

    internal static bool BlockedPrefix(MethodBase __originalMethod)
    {
        PlayerSettlementPatchRuntime.Current?.NotifyFeatureBlocked(
            $"{__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}");
        return false;
    }
}
