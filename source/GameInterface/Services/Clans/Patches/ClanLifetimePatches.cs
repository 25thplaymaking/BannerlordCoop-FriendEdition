using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.Clans.Messages.Lifetime;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// Patches for lifecycle of <see cref="Clan"/> objects.
/// </summary>
[HarmonyPatch]
internal class ClanLifetimePatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanLifetimePatches>();

    [HarmonyPatch(typeof(DestroyClanAction), "ApplyInternal")]
    [HarmonyPrefix]
    static bool DestroyPrefix(Clan destroyedClan, int details)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        if (ModInformation.IsServer && IsProtectedPersonalClan(destroyedClan))
        {
            Logger.Information("Keeping dormant personal clan {ClanId} while its player is in another clan", destroyedClan.StringId);
            return false;
        }

        if (ModInformation.IsClient)
        {
            ClientMutationLog.Report(Logger, "created", typeof(Clan));
            return false;
        }

        MessageBroker.Instance.Publish(destroyedClan, new ClanDestroyed(destroyedClan, details));

        return true;
    }

    private static bool IsProtectedPersonalClan(Clan clan)
    {
        if (clan == null ||
            !ContainerProvider.TryResolve<IPlayerManager>(out var playerManager) ||
            !ContainerProvider.TryResolve<IObjectManager>(out var objectManager) ||
            !objectManager.TryGetId(clan, out var clanId))
            return false;

        foreach (var player in playerManager.Players)
        {
            if (player.ClanMembershipMode != PlayerClanMembershipMode.PersonalClan &&
                player.PersonalClanId == clanId)
                return true;
        }

        return false;
    }
}
