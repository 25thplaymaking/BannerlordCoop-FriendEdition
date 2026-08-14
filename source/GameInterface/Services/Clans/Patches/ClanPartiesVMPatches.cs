using Common.Logging;
using Common;
using Common.Messaging;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Players.Messages;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement;
using TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Clans.Patches;

[HarmonyPatch(typeof(ClanPartiesVM))]
internal class ClanPartiesVMPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanPartiesVMPatches>();

    private const string PlayerHeroRejectedMessage = "Failed to change clan party leader because hero is a player.";

    [HarmonyPatch(nameof(ClanPartiesVM.CreateNewClanParty))]
    [HarmonyPrefix]
    public static bool CreateNewClanPartyPrefix(ClanPartiesVM __instance, Hero newLeader, int partyGoldLowerThreshold)
    {
        if (newLeader == Hero.MainHero && TryGetLocalPlayer(out var localPlayer) &&
            localPlayer.ClanMembershipMode == PlayerClanMembershipMode.Embedded)
        {
            ShowJoinedPlayerActions();
            return false;
        }

        // Reject forming a new party with a player hero
        if (newLeader != null && newLeader.IsPlayerHero())
        {
            Logger.Error($"Rejecting new clan mobile party because newLeader is a player hero ({newLeader.StringId}).");

            // Inform client of rejected clan party leader change
            InformationManager.DisplayMessage(new InformationMessage(PlayerHeroRejectedMessage));
            return false;
        }

        if (newLeader.PartyBelongedTo == MobileParty.MainParty)
        {
            __instance._openPartyAsManage(newLeader);
            __instance.RefreshPartiesList();
            return false;
        }

        // Create and manage the new mobile party on the server
        var message = new NewClanPartyCreated(Hero.MainHero, newLeader, __instance._faction, partyGoldLowerThreshold);
        MessageBroker.Instance.Publish(__instance, message);

        __instance._onRefresh();

        return false;
    }

    /// <summary>
    /// The party the change-leader popup was opened for. Save to use in OnPartyLeaderChangedPrefix.
    /// Any incoming refresh messages can change ClanPartiesVM.CurrentSelectedParty to the player's party.
    /// </summary>
    private static MobileParty popupParty;

    [HarmonyPatch(nameof(ClanPartiesVM.OnShowChangeLeaderPopup))]
    [HarmonyPrefix]
    public static void OnShowChangeLeaderPopupPrefix(ClanPartiesVM __instance)
    {
        popupParty = __instance.CurrentSelectedParty?.Party?.MobileParty;
    }

    [HarmonyPatch(nameof(ClanPartiesVM.OnPartyLeaderChanged))]
    [HarmonyPrefix]
    public static bool OnPartyLeaderChangedPrefix(ClanPartiesVM __instance, Hero newLeader)
    {
        // Use popupParty instead of the CurrentSelectedParty that can change from any incoming refresh messages
        var selectedParty = popupParty ?? __instance.CurrentSelectedParty?.Party?.MobileParty;
        popupParty = null;

        if (selectedParty == null) return false;

        var oldLeader = selectedParty.Party?.LeaderHero;
        if (oldLeader != null && oldLeader.IsPlayerHero())
        {
            Logger.Error($"Rejecting change of leader in clan mobile party because oldLeader is a player hero ({oldLeader.StringId}).");

            // Inform client of rejected clan party leader change
            InformationManager.DisplayMessage(new InformationMessage(PlayerHeroRejectedMessage));
            return false;
        }

        if (newLeader != null && newLeader.IsPlayerHero())
        {
            Logger.Error($"Rejecting change of leader in clan mobile party because newLeader is a player hero ({newLeader.StringId}).");

            // Inform client of rejected clan party leader change
            InformationManager.DisplayMessage(new InformationMessage(PlayerHeroRejectedMessage));
            return false;
        }

        // Change clan party leader on the server
        var message = new ClanPartyLeaderChanged(Hero.MainHero, newLeader, selectedParty, MobileParty.MainParty);
        MessageBroker.Instance.Publish(__instance, message);

        return false;
    }

    [HarmonyPatch(nameof(ClanPartiesVM.OnDisbandCurrentParty))]
    [HarmonyPrefix]
    public static bool OnDisbandCurrentPartyPrefix(ClanPartiesVM __instance)
    {
        if (__instance.CurrentSelectedParty?.Party?.LeaderHero == Hero.MainHero &&
            TryGetLocalPlayer(out var localPlayer) &&
            localPlayer.ClanMembershipMode == PlayerClanMembershipMode.IndependentParty)
        {
            InformationManager.ShowInquiry(new InquiryData(
                "Leave clan",
                "Return to your personal clan? Assets transferred when you joined will remain with this clan.",
                true,
                true,
                "Leave Clan",
                "Cancel",
                () => MessageBroker.Instance.Publish(null, new LeavePlayerClanSelected()),
                null));
        }

        // Block and implement as part of OnPartyLeaderChanged to use correct party
        // instead of currently selected (which can switch back to the player's party)
        return false;
    }

    [HarmonyPatch(nameof(ClanPartiesVM.OnFinalize))]
    [HarmonyPostfix]
    public static void OnFinalizePostfix()
    {
        popupParty = null;
    }

    [HarmonyPatch(nameof(ClanPartiesVM.GetNewPartyLeaderCandidates))]
    [HarmonyPostfix]
    public static void GetNewPartyLeaderCandidatesPostfix(ref IEnumerable<ClanCardSelectionItemInfo> __result)
    {
        // Remove player heroes from card selection
        __result = WithoutPlayerHeroes(__result, allowLocalEmbeddedPlayer: true);
    }

    [HarmonyPatch(nameof(ClanPartiesVM.GetChangeLeaderCandidates))]
    [HarmonyPostfix]
    public static void GetChangeLeaderCandidatesPostfix(ref IEnumerable<ClanCardSelectionItemInfo> __result)
    {
        // Remove player heroes from card selection
        __result = WithoutPlayerHeroes(__result, allowLocalEmbeddedPlayer: false);
    }

    [HarmonyPatch(nameof(ClanPartiesVM.GetCanDisbandParty))]
    [HarmonyPostfix]
    public static void GetCanDisbandPartyPostfix(ClanPartiesVM __instance, ref bool __result)
    {
        if (__instance.CurrentSelectedParty?.Party?.LeaderHero == Hero.MainHero &&
            TryGetLocalPlayer(out var localPlayer) &&
            localPlayer.ClanMembershipMode == PlayerClanMembershipMode.IndependentParty)
            __result = true;
    }

    private static IEnumerable<ClanCardSelectionItemInfo> WithoutPlayerHeroes(
        IEnumerable<ClanCardSelectionItemInfo> candidates,
        bool allowLocalEmbeddedPlayer)
    {
        if (candidates == null) return candidates;

        return candidates
            .Where(candidate => !(candidate.Identifier is Hero hero && hero.IsPlayerHero()) ||
                                allowLocalEmbeddedPlayer && hero == Hero.MainHero && IsLocalEmbeddedPlayer())
            .ToList();
    }

    private static bool IsLocalEmbeddedPlayer() =>
        TryGetLocalPlayer(out var player) &&
        player.ClanMembershipMode == PlayerClanMembershipMode.Embedded;

    private static bool TryGetLocalPlayer(out Player player)
    {
        player = null;
        if (Hero.MainHero == null ||
            !ContainerProvider.TryResolve<IPlayerManager>(out var playerManager) ||
            !ContainerProvider.TryResolve<IObjectManager>(out var objectManager) ||
            !objectManager.TryGetId(Hero.MainHero, out var heroId))
            return false;

        player = playerManager.Players.SingleOrDefault(candidate => candidate.HeroId == heroId);
        return player != null;
    }

    private static void ShowJoinedPlayerActions()
    {
        var choices = new List<InquiryElement>
        {
            new InquiryElement("party", "Request an independent party", null),
            new InquiryElement("leave", "Leave the clan", null),
        };
        MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
            "Clan membership",
            "Choose how you want to continue.",
            choices,
            true,
            1,
            1,
            "Continue",
            "Cancel",
            selected =>
            {
                if ((string)selected.Single().Identifier == "party")
                    MessageBroker.Instance.Publish(null, new IndependentPlayerPartySelected());
                else
                    InformationManager.ShowInquiry(new InquiryData(
                        "Leave clan",
                        "Return to your personal clan? Transferred assets will not be returned.",
                        true,
                        true,
                        "Leave Clan",
                        "Cancel",
                        () => MessageBroker.Instance.Publish(null, new LeavePlayerClanSelected()),
                        null));
            },
            null),
            pauseGameActiveState: true);
    }
}
