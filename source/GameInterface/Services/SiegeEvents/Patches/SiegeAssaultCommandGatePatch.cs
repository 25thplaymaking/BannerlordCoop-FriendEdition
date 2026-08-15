using HarmonyLib;
using Common;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Localization;

namespace GameInterface.Services.SiegeEvents.Patches;

/// <summary>
/// Restricts starting an assault to the synced BesiegerCamp leader. Participation in an active assault
/// remains on the vanilla encounter flow.
/// </summary>
[HarmonyPatch(typeof(SiegeEventCampaignBehavior))]
internal static class SiegeAssaultCommandGatePatch
{
    [HarmonyPatch(nameof(SiegeEventCampaignBehavior.game_menu_siege_strategies_lead_assault_on_condition))]
    [HarmonyPrefix]
    private static bool LeadAssaultConditionPrefix(MenuCallbackArgs args, ref bool __result)
        => ContinueWhenSiegeLeaderIsAvailable(args, ref __result);

    [HarmonyPatch(nameof(SiegeEventCampaignBehavior.game_menu_siege_strategies_lead_assault_on_condition))]
    [HarmonyPostfix]
    private static void LeadAssaultConditionPostfix(MenuCallbackArgs args, bool __result) => DisableForCoBesieger(args, __result);

    [HarmonyPatch(nameof(SiegeEventCampaignBehavior.game_menu_siege_strategies_order_assault_on_condition))]
    [HarmonyPrefix]
    private static bool OrderAssaultConditionPrefix(MenuCallbackArgs args, ref bool __result)
        => ContinueWhenSiegeLeaderIsAvailable(args, ref __result);

    [HarmonyPatch(nameof(SiegeEventCampaignBehavior.game_menu_siege_strategies_order_assault_on_condition))]
    [HarmonyPostfix]
    private static void OrderAssaultConditionPostfix(MenuCallbackArgs args, bool __result) => DisableForCoBesieger(args, __result);

    /// <summary>
    /// A client can receive the siege menu before the replicated camp leader, or after the leader has left and
    /// before the server's termination prompt reaches it. Vanilla dereferences that missing leader while it
    /// refreshes either assault option, so one stale option prevents the entire menu from accepting any input.
    /// Keep the menu responsive until the authoritative siege state arrives; the server remains unchanged.
    /// </summary>
    private static bool ContinueWhenSiegeLeaderIsAvailable(MenuCallbackArgs args, ref bool result)
    {
        if (ModInformation.IsServer) return true;

        var siegeEvent = MobileParty.MainParty?.BesiegerCamp?.SiegeEvent ??
            MobileParty.MainParty?.BesiegedSettlement?.SiegeEvent;
        if (siegeEvent?.BesiegerCamp?.LeaderParty != null) return true;

        result = false;
        if (args != null)
        {
            args.IsEnabled = false;
            args.Tooltip = new TextObject("{=!}Siege command state is synchronizing.");
        }

        return false;
    }

    private static void DisableForCoBesieger(MenuCallbackArgs args, bool result)
    {
        if (!result) return;

        var settlement = MobileParty.MainParty?.BesiegedSettlement;
        if (settlement == null) return;

        var leader = settlement.SiegeEvent?.BesiegerCamp?.LeaderParty;
        if (leader == MobileParty.MainParty) return;

        args.IsEnabled = false;
        args.Tooltip = new TextObject("{=!}Only the siege leader can command the assault.");
    }
}
