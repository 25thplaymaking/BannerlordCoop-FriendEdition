using Common.Messaging;
using Common.Util;
using GameInterface.Services.Caravans.Messages;
using GameInterface.Services.ItemRosters.Interfaces;
using HarmonyLib;
using Helpers;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.Caravans.Patches;

[HarmonyPatch(typeof(CaravansCampaignBehavior))]
internal class CaravansConversationsPatches
{

    [HarmonyPatch(nameof(CaravansCampaignBehavior.caravan_companion_ask_change_home_settlement_4_on_consequence))]
    [HarmonyPrefix]
    public static bool CaravanCompanionAskChangeHomeSettlement4OnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        Settlement settlement = ConversationSentence.SelectedRepeatObject as Settlement;
        var conversationParty = MobileParty.ConversationParty;
        if (settlement == null || conversationParty?.CaravanPartyComponent == null)
            return AbortInvalidInteraction("the caravan or its destination is no longer available");

        StringHelpers.SetSettlementProperties("SETTLEMENT", settlement, null, false);

        // Update locally to update dialogue properly before being managed by the server to update all other clients
        using (new AllowedThread())
        {
            conversationParty.CaravanPartyComponent.ChangeHomeSettlement(settlement);
        }

        var message = new ChangeCaravanHomeSettlement(conversationParty, settlement);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.caravan_companion_prohibit_kingdoms_selected_2_on_consequence))]
    [HarmonyPrefix]
    public static bool CaravanCompanionProhibitKingdomsSelected2OnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        Kingdom kingdom = ConversationSentence.SelectedRepeatObject as Kingdom;
        if (kingdom == null || Hero.MainHero == null)
            return AbortInvalidInteraction("the selected kingdom or player is no longer available");

        bool kingdomAlreadyProhibited = __instance._prohibitedKingdomsForPlayerCaravans.Contains(kingdom);

        // Send message to server to update CoopSession
        var message = new ToggleProhibitedKingdom(Hero.MainHero, kingdom, kingdomAlreadyProhibited);
        MessageBroker.Instance.Publish(__instance, message);

        // Modifying local instance of _prohibitedKingdomsForPlayerCaravans on client is needed
        return true;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.conversation_caravan_fight_forced_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationCaravanFightForcedOnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        if (!TryGetInteractionParties(out var mainHero, out var mainParty, out var conversationParty))
            return AbortInvalidInteraction("one of the encounter parties is no longer available");

        // Update last interaction with caravan locally
        __instance.SetPlayerInteraction(conversationParty, CaravansCampaignBehavior.PlayerInteraction.Hostile);

        // Send message to server to update CoopSession and run BeHostileAction.ApplyEncounterHostileAction
        var message = new ApplyHostileCaravanInteraction(mainHero, mainParty, conversationParty);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.caravan_start_talk_on_condition))]
    [HarmonyPostfix]
    public static void CaravanStartTalkOnConditionPostfix(ref CaravansCampaignBehavior __instance, ref bool __result)
    {
        // Conditions are evaluated speculatively while the dialogue tree is assembled. Publishing a Friendly
        // interaction when vanilla rejected the condition can overwrite an existing Hostile state merely by
        // looking at an unavailable dialogue branch.
        if (!__result || Hero.MainHero == null || MobileParty.ConversationParty?.IsCaravan != true)
        {
            return;
        }

        // Local update is needed so separately publish message to server in postfix to store change in CoopSession
        var message = new SetPlayerCaravanInteraction(Hero.MainHero, MobileParty.ConversationParty, CaravansCampaignBehavior.PlayerInteraction.Friendly);
        MessageBroker.Instance.Publish(__instance, message);
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.caravan_loot_on_clickable_condition))]
    [HarmonyPrefix]
    public static bool CaravanLootOnClickableConditionPrefix(ref CaravansCampaignBehavior __instance, ref bool __result, out TextObject explanation)
    {
        // Replacement message for vanilla's "You just looted this party." message to be ambiguous for more than one player
        explanation = new TextObject("");
        var conversationParty = MobileParty.ConversationParty;
        if (conversationParty?.IsCaravan != true)
        {
            explanation = new TextObject("{=!}This caravan is no longer available.", null);
            __result = false;
            return false;
        }

        if (__instance._lootedCaravans.ContainsKey(conversationParty))
        {
            explanation = new TextObject("{=!}This caravan has been looted recently.", null);
            __result = false;
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.caravan_ask_trade_rumors_on_consequence))]
    [HarmonyPostfix]
    public static void CaravanAskTradeRumorsOnConsequencePostfix(ref CaravansCampaignBehavior __instance)
    {
        if (Hero.MainHero == null || __instance._tradeRumorTakenCaravans == null)
            return;

        // Send data to server to update CoopSession
        var message = new UpdateTradeRumorTakenCaravans(Hero.MainHero, __instance._tradeRumorTakenCaravans);
        MessageBroker.Instance.Publish(__instance, message);
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.conversation_caravan_fight_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationCaravanFightOnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        if (!TryGetInteractionParties(out var mainHero, out var mainParty, out var conversationParty))
            return AbortInvalidInteraction("one of the encounter parties is no longer available");

        // Update last interaction with caravan locally
        __instance.SetPlayerInteraction(conversationParty, CaravansCampaignBehavior.PlayerInteraction.Hostile);

        // Send message to server to update CoopSession and run BeHostileAction.ApplyEncounterHostileAction
        var message = new ApplyHostileCaravanInteraction(mainHero, mainParty, conversationParty);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.conversation_caravan_looted_leave_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationCaravanLootedLeaveOnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        if (!TryGetInteractionParties(out var mainHero, out var mainParty, out var conversationParty))
            return AbortInvalidInteraction("one of the encounter parties is no longer available");

        // Locally calculate bribe amount with allowed thread added by AllowItemRostersInGUI to handle itemRoster
        __instance.BribeAmount(conversationParty.Party, out int amount, out ItemRoster itemRoster);

        // Locally set player interaction, and then save in CoopSession on server
        __instance.SetPlayerInteraction(conversationParty, CaravansCampaignBehavior.PlayerInteraction.Hostile);

        PlayerEncounter.LeaveEncounter = true;

        var message = new CaravanLootedLeaveOnConsequence(
            mainHero, mainParty, conversationParty, itemRoster?._data ?? Array.Empty<ItemRosterElement>(), amount);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.conversation_caravan_surrender_leave_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationCaravanSurrenderLeaveOnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        if (!TryGetInteractionParties(out var mainHero, out var mainParty, out var conversationParty))
            return AbortInvalidInteraction("one of the encounter parties is no longer available");

        // Call helper function to implement vanilla open loot screen logic
        if (!ContainerProvider.TryResolve<IItemRosterInterface>(out var itemRosterInterface))
            return AbortInvalidInteraction("the loot service is unavailable");

        itemRosterInterface.OpenPartyLootScreen(conversationParty, out var caravanHasItems, out var itemRosterElements);

        // Locally set player interaction, and then save in CoopSession on server
        __instance.SetPlayerInteraction(conversationParty, CaravansCampaignBehavior.PlayerInteraction.Hostile);

        PlayerEncounter.LeaveEncounter = true;

        var message = new CaravanSurrenderLeaveOnConsequence(
            mainHero, mainParty, conversationParty, caravanHasItems, itemRosterElements);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(CaravansCampaignBehavior.conversation_caravan_took_prisoner_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationCaravanTookPrisonerOnConsequencePrefix(ref CaravansCampaignBehavior __instance)
    {
        MobileParty encounteredMobileParty = PlayerEncounter.EncounteredMobileParty;
        if (Hero.MainHero == null || MobileParty.MainParty?.Party == null ||
            encounteredMobileParty?.IsCaravan != true || encounteredMobileParty.Party == null)
        {
            return AbortInvalidInteraction("one of the encounter parties is no longer available");
        }

        // Locally set player interaction, and then save in CoopSession on server
        __instance.SetPlayerInteraction(encounteredMobileParty, CaravansCampaignBehavior.PlayerInteraction.Hostile);

        // Call helper function to implement vanilla open loot screen logic
        if (!ContainerProvider.TryResolve<IItemRosterInterface>(out var itemRosterInterface))
            return AbortInvalidInteraction("the loot service is unavailable");

        itemRosterInterface.OpenPartyLootScreen(encounteredMobileParty, out var caravanHasItems, out var itemRosterElements);

        // Open prisoner transfer screen
        using (new AllowedThread())
        {
            TroopRoster troopRoster = TroopRoster.CreateDummyTroopRoster();
            foreach (TroopRosterElement troopRosterElement in encounteredMobileParty.MemberRoster.GetTroopRoster())
            {
                troopRoster.AddToCounts(troopRosterElement.Character, troopRosterElement.Number, false, 0, 0, true, -1);
            }
            PartyScreenHelper.OpenScreenAsLoot(TroopRoster.CreateDummyTroopRoster(), troopRoster, encounteredMobileParty.Name, troopRoster.TotalManCount, null);
        }

        var message = new CaravanTookPrisonerOnConsequence(Hero.MainHero, MobileParty.MainParty, encounteredMobileParty, caravanHasItems, itemRosterElements);
        MessageBroker.Instance.Publish(__instance, message);

        PlayerEncounter.LeaveEncounter = true;

        return false;
    }

    private static bool TryGetInteractionParties(
        out Hero mainHero,
        out MobileParty mainParty,
        out MobileParty conversationParty)
    {
        mainHero = Hero.MainHero;
        mainParty = MobileParty.MainParty;
        conversationParty = MobileParty.ConversationParty;
        return mainHero != null && mainParty?.Party != null &&
               conversationParty?.IsCaravan == true && conversationParty.Party != null;
    }

    private static bool AbortInvalidInteraction(string reason)
    {
        InformationManager.DisplayMessage(new InformationMessage($"Caravan interaction cancelled: {reason}."));
        if (PlayerEncounter.Current != null)
            PlayerEncounter.LeaveEncounter = true;
        return false;
    }
}
