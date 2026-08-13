using Common;
using GameInterface.Configuration;
using HarmonyLib;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CampaignBehaviors.BarterBehaviors;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Separatism;

[HarmonyPatch]
internal static class SeparatismCompatibilityPatches
{
    [HarmonyPatch(typeof(Hero), nameof(Hero.IsFriend))]
    [HarmonyPrefix]
    private static bool IsFriendPrefix(Hero __instance, Hero otherHero, ref bool __result)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled) return true;
        // This replaces the global Hero.IsFriend; a null otherHero (which vanilla tolerates) would NRE
        // GetHeroRelation, so defer to the original for that case.
        if (otherHero == null) return true;
        __result = SeparatismCompatibilityPolicy.IsFriend(
            CharacterRelationManager.GetHeroRelation(__instance, otherHero),
            options.FriendThreshold);
        return false;
    }

    [HarmonyPatch(typeof(Hero), nameof(Hero.IsEnemy))]
    [HarmonyPrefix]
    private static bool IsEnemyPrefix(Hero __instance, Hero otherHero, ref bool __result)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled) return true;
        if (otherHero == null) return true;
        __result = SeparatismCompatibilityPolicy.IsEnemy(
            CharacterRelationManager.GetHeroRelation(__instance, otherHero),
            options.EnemyThreshold);
        return false;
    }

    [HarmonyPatch(typeof(RebellionsCampaignBehavior), "DailyTickSettlement")]
    [HarmonyPrefix]
    private static void SettlementRebellionPrefix(RebellionsCampaignBehavior __instance)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled) return;
        AccessTools.Field(typeof(RebellionsCampaignBehavior), "_rebellionEnabled")
            ?.SetValue(
                __instance,
                SeparatismCompatibilityPolicy.AllowConfiguredSettlementRebellion(
                    ModInformation.IsServer,
                    options.SettlementRebellionsEnabled));
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderClanJoin")]
    [HarmonyPrefix]
    private static bool ConsiderClanJoinPrefix(Clan clan, Kingdom kingdom)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        bool leadersExist = clan?.Leader != null && kingdom?.Leader != null;
        return SeparatismCompatibilityPolicy.AllowClanJoin(
            options.Enabled,
            leadersExist,
            leadersExist && clan.Leader.IsEnemy(kingdom.Leader));
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderClanLeaveKingdom")]
    [HarmonyPrefix]
    private static bool ConsiderClanLeavePrefix(Clan clan)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        bool hasKingdom = clan?.Kingdom != null;
        bool leaderExists = clan?.Leader != null;
        return SeparatismCompatibilityPolicy.AllowClanLeave(
            options.Enabled,
            hasKingdom,
            leaderExists,
            hasKingdom && leaderExists && clan.Leader == clan.Kingdom.Leader,
            clan?.Settlements?.Any() == true,
            hasKingdom && leaderExists && clan.Leader.HasGoodRelationWith(clan.Kingdom.Leader));
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderDefection")]
    [HarmonyPrefix]
    private static bool ConsiderDefectionPrefix(Clan clan1, Kingdom kingdom)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        bool hasCurrentKingdom = clan1?.Kingdom != null;
        bool leadersExist = clan1?.Leader != null && kingdom?.Leader != null;
        bool hasSettlements = clan1?.Settlements?.Any() == true;
        bool goodRulerRelation = hasCurrentKingdom && leadersExist &&
            clan1.Leader.HasGoodRelationWith(clan1.Kingdom.Leader);
        return SeparatismCompatibilityPolicy.AllowDefection(
            options.Enabled,
            hasCurrentKingdom,
            leadersExist,
            hasCurrentKingdom && leadersExist && clan1.Leader == clan1.Kingdom.Leader,
            hasCurrentKingdom && clan1.Kingdom == kingdom,
            hasSettlements,
            goodRulerRelation,
            leadersExist && clan1.Leader.IsEnemy(kingdom.Leader),
            !hasSettlements || (hasCurrentKingdom &&
                SeparatismCampaignService.GetCloseKingdoms(clan1).Contains(kingdom)),
            hasCurrentKingdom && clan1.Kingdom.Settlements.Any(),
            hasCurrentKingdom
                ? clan1.Kingdom.Clans.Count(candidate => !candidate.IsUnderMercenaryService)
                : 0);
    }
}

internal static class SeparatismCompatibilityPolicy
{
    internal static bool AllowConfiguredSettlementRebellion(
        bool localIsServer,
        bool settlementRebellionsEnabled) =>
        localIsServer && settlementRebellionsEnabled;

    internal static bool IsFriend(int relation, int threshold) => relation > threshold;
    internal static bool IsEnemy(int relation, int threshold) => relation < threshold;

    internal static bool AllowClanJoin(bool enabled, bool leadersExist, bool leadersAreEnemies) =>
        !enabled || !leadersExist || !leadersAreEnemies;

    internal static bool AllowClanLeave(
        bool enabled,
        bool hasKingdom,
        bool leaderExists,
        bool leaderIsRuler,
        bool hasSettlements,
        bool hasGoodRulerRelation)
    {
        if (!enabled || !hasKingdom || !leaderExists) return true;
        if (leaderIsRuler) return false;
        return !hasSettlements || !hasGoodRulerRelation;
    }

    internal static bool AllowDefection(
        bool enabled,
        bool hasCurrentKingdom,
        bool leadersExist,
        bool leaderIsRuler,
        bool sameKingdom,
        bool hasSettlements,
        bool hasGoodRulerRelation,
        bool enemyOfDestination,
        bool destinationIsClose,
        bool currentKingdomHasSettlements,
        int currentNonMercenaryClanCount)
    {
        if (!enabled || !hasCurrentKingdom || !leadersExist) return true;
        if (leaderIsRuler || sameKingdom) return false;
        if (hasSettlements &&
            (hasGoodRulerRelation || enemyOfDestination || !destinationIsClose))
            return false;

        return !hasGoodRulerRelation ||
               !currentKingdomHasSettlements ||
               currentNonMercenaryClanCount > 2;
    }
}
