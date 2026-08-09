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
        __result = CharacterRelationManager.GetHeroRelation(__instance, otherHero) > options.FriendThreshold;
        return false;
    }

    [HarmonyPatch(typeof(Hero), nameof(Hero.IsEnemy))]
    [HarmonyPrefix]
    private static bool IsEnemyPrefix(Hero __instance, Hero otherHero, ref bool __result)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled) return true;
        __result = CharacterRelationManager.GetHeroRelation(__instance, otherHero) < options.EnemyThreshold;
        return false;
    }

    [HarmonyPatch(typeof(RebellionsCampaignBehavior), "DailyTickSettlement")]
    [HarmonyPrefix]
    private static void SettlementRebellionPrefix(RebellionsCampaignBehavior __instance)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled) return;
        AccessTools.Field(typeof(RebellionsCampaignBehavior), "_rebellionEnabled")
            ?.SetValue(__instance, options.SettlementRebellionsEnabled);
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderClanJoin")]
    [HarmonyPrefix]
    private static bool ConsiderClanJoinPrefix(Clan clan, Kingdom kingdom)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        return !options.Enabled || clan?.Leader == null || kingdom?.Leader == null || !clan.Leader.IsEnemy(kingdom.Leader);
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderClanLeaveKingdom")]
    [HarmonyPrefix]
    private static bool ConsiderClanLeavePrefix(Clan clan)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled || clan?.Kingdom == null || clan.Leader == null) return true;
        if (clan.Leader == clan.Kingdom.Leader) return false;
        return !clan.Settlements.Any() || !clan.Leader.HasGoodRelationWith(clan.Kingdom.Leader);
    }

    [HarmonyPatch(typeof(DiplomaticBartersBehavior), "ConsiderDefection")]
    [HarmonyPrefix]
    private static bool ConsiderDefectionPrefix(Clan clan1, Kingdom kingdom)
    {
        var options = ModConfigProvider.ModOptions.Separatism;
        if (!options.Enabled || clan1?.Kingdom == null || clan1.Leader == null || kingdom?.Leader == null) return true;
        if (clan1.Leader == clan1.Kingdom.Leader || clan1.Kingdom == kingdom) return false;

        if (clan1.Settlements.Any() &&
            (clan1.Leader.HasGoodRelationWith(clan1.Kingdom.Leader)
             || clan1.Leader.IsEnemy(kingdom.Leader)
             || !SeparatismCampaignService.GetCloseKingdoms(clan1).Contains(kingdom)))
        {
            return false;
        }

        return !clan1.Leader.HasGoodRelationWith(clan1.Kingdom.Leader)
               || !clan1.Kingdom.Settlements.Any()
               || clan1.Kingdom.Clans.Count(clan => !clan.IsUnderMercenaryService) > 2;
    }
}
