using Common;
using Common.Util;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Players.Patches;

[HarmonyPatch(typeof(Hero), nameof(Hero.ChangeHeroGold))]
internal static class PlayerClanGoldPatches
{
    [ThreadStatic]
    private static bool redirecting;

    [HarmonyPrefix]
    private static bool Prefix(Hero __instance, int changeAmount)
    {
        if (!ModInformation.IsServer || redirecting ||
            !TryResolve(out var playerManager, out var objectManager) ||
            !TryGetPlayer(__instance, playerManager, objectManager, out var player) ||
            player.ClanMembershipMode == PlayerClanMembershipMode.PersonalClan ||
            __instance.Clan?.Leader == null || __instance.Clan.Leader == __instance)
            return true;

        redirecting = true;
        try
        {
            __instance.Clan.Leader.ChangeHeroGold(changeAmount);
            MirrorJoinedGold(__instance.Clan.Leader, playerManager, objectManager);
        }
        finally
        {
            redirecting = false;
        }

        return false;
    }

    [HarmonyPostfix]
    private static void Postfix(Hero __instance)
    {
        if (!ModInformation.IsServer || redirecting ||
            !TryResolve(out var playerManager, out var objectManager))
            return;

        MirrorJoinedGold(__instance, playerManager, objectManager);
    }

    private static void MirrorJoinedGold(
        Hero leader,
        IPlayerManager playerManager,
        IObjectManager objectManager)
    {
        if (leader?.Clan?.Leader != leader || !objectManager.TryGetId(leader.Clan, out var clanId)) return;

        using (new AllowedThread())
        {
            foreach (var member in playerManager.Players.Where(candidate =>
                         candidate.ClanId == clanId &&
                         candidate.ClanMembershipMode != PlayerClanMembershipMode.PersonalClan))
            {
                if (objectManager.TryGetObject(member.HeroId, out Hero memberHero))
                    memberHero.Gold = leader.Gold;
            }
        }
    }

    private static bool TryGetPlayer(
        Hero hero,
        IPlayerManager playerManager,
        IObjectManager objectManager,
        out Player player)
    {
        player = null;
        if (!objectManager.TryGetId(hero, out var heroId)) return false;
        player = playerManager.Players.SingleOrDefault(candidate => candidate.HeroId == heroId);
        return player != null;
    }

    private static bool TryResolve(out IPlayerManager playerManager, out IObjectManager objectManager)
    {
        playerManager = null;
        objectManager = null;
        return ContainerProvider.TryResolve(out playerManager) &&
               ContainerProvider.TryResolve(out objectManager);
    }
}
