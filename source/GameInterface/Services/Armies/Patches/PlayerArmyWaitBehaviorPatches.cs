using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;

namespace GameInterface.Services.Armies.Patches;

/// <summary>
/// Describes whether an attached player's army siege graph is absent, incomplete, or ready.
/// </summary>
internal enum AttachedArmySiegeState
{
    None,
    Incomplete,
    Ready,
}

/// <summary>
/// Recovers client army-wait siege transitions and relays player leave or abandon actions.
/// </summary>
[HarmonyPatch]
internal class PlayerArmyWaitBehaviorPatches
{
    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), nameof(PlayerArmyWaitBehavior.OnTick))]
    [HarmonyPrefix]
    private static bool OnTickPrefix()
    {
        if (ModInformation.IsClient)
        {
            TryStartAttachedArmySiege();
        }

        return false;
    }

    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), "ArmyWaitMenuTick")]
    [HarmonyPrefix]
    private static bool ArmyWaitMenuTickPrefix(MenuCallbackArgs args)
    {
        if (ModInformation.IsServer) return false;

        var mainParty = MobileParty.MainParty;
        var siegeState = GetAttachedArmySiegeState(mainParty, out var settlement);
        if (siegeState == AttachedArmySiegeState.Ready)
        {
            StartAttachedArmySiege(settlement);
            return false;
        }

        if (siegeState == AttachedArmySiegeState.Incomplete)
            return false;

        var encounterGameMenuModel = Campaign.Current?.Models?.EncounterGameMenuModel;
        if (encounterGameMenuModel == null)
            return false;

        var genericStateMenu = encounterGameMenuModel.GetGenericStateMenu();
        if (genericStateMenu != "army_wait")
        {
            args?.MenuContext?.GameMenu?.EndWait();
            if (string.IsNullOrEmpty(genericStateMenu))
            {
                GameMenu.ExitToLast();
            }
            else
            {
                GameMenu.SwitchToMenu(genericStateMenu);
            }

            return false;
        }

        return IsStableArmyWait(mainParty);
    }

    private static bool TryStartAttachedArmySiege()
    {
        if (Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId != "army_wait")
            return false;

        if (GetAttachedArmySiegeState(MobileParty.MainParty, out var settlement) != AttachedArmySiegeState.Ready)
            return false;

        StartAttachedArmySiege(settlement);
        return true;
    }

    private static void StartAttachedArmySiege(Settlement settlement)
    {
        using (new AllowedThread())
        {
            PlayerSiege.StartPlayerSiege(BattleSideEnum.Attacker, isSimulation: false, settlement);
            PlayerSiege.StartSiegePreparation();
        }
    }

    internal static AttachedArmySiegeState GetAttachedArmySiegeState(
        MobileParty mainParty,
        out Settlement settlement)
    {
        settlement = null;
        if (mainParty == null)
            return AttachedArmySiegeState.Incomplete;

        var army = mainParty.Army;
        var leaderParty = army?.LeaderParty;
        if (army == null)
        {
            return mainParty.AttachedTo != null || mainParty.BesiegerCamp != null
                ? AttachedArmySiegeState.Incomplete
                : AttachedArmySiegeState.None;
        }

        if (leaderParty == null)
            return AttachedArmySiegeState.Incomplete;

        if (leaderParty == mainParty)
            return AttachedArmySiegeState.None;

        if (mainParty.AttachedTo != leaderParty || leaderParty.Army != army)
            return AttachedArmySiegeState.Incomplete;

        var targetSettlement = army.AiBehaviorObject as Settlement;
        var mainPartyCamp = mainParty.BesiegerCamp;
        var leaderCamp = leaderParty.BesiegerCamp;
        if (targetSettlement == null)
        {
            return mainPartyCamp != null || leaderCamp != null
                ? AttachedArmySiegeState.Incomplete
                : AttachedArmySiegeState.None;
        }

        var siegeEvent = targetSettlement.SiegeEvent;
        if (siegeEvent == null)
        {
            return mainPartyCamp != null || leaderCamp != null
                ? AttachedArmySiegeState.Incomplete
                : AttachedArmySiegeState.None;
        }

        var liveCamp = siegeEvent.BesiegerCamp;
        if (mainPartyCamp == null && leaderCamp == null)
            return AttachedArmySiegeState.None;

        if (liveCamp == null ||
            mainPartyCamp != liveCamp ||
            leaderCamp != liveCamp ||
            liveCamp.SiegeEvent != siegeEvent ||
            siegeEvent.BesiegedSettlement != targetSettlement ||
            leaderParty.BesiegedSettlement != targetSettlement)
        {
            return AttachedArmySiegeState.Incomplete;
        }

        settlement = targetSettlement;
        return AttachedArmySiegeState.Ready;
    }

    internal static bool IsStableArmyWait(MobileParty mainParty)
    {
        var army = mainParty?.Army;
        var leaderParty = army?.LeaderParty;
        if (leaderParty == null)
            return false;

        return leaderParty == mainParty ||
            (mainParty.AttachedTo == leaderParty && leaderParty.Army == army);
    }

    /// <summary>
    /// Client "Leave Army" availability. Native hides the option whenever the main party holds ANY
    /// <c>MapEvent</c> or <c>BesiegedSettlement</c> reference - but on a co-op client those
    /// references can be STALE (a battle concluded and destroyed server-side whose local reference
    /// was never cleared, or siege-camp residue; the Phase I raid softlock was this same class).
    /// A stale reference made the option vanish forever: the player could join an army but never
    /// leave it. Only a LIVE blocking state (resolvable, unfinalized event; camp matching the
    /// settlement's live siege) hides the option here.
    /// </summary>
    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), nameof(PlayerArmyWaitBehavior.wait_menu_army_leave_on_condition))]
    [HarmonyPrefix]
    private static bool WaitMenuLeaveConditionPrefix(MenuCallbackArgs args, ref bool __result)
    {
        if (ModInformation.IsServer) return true;

        args.optionLeaveType = GameMenuOption.LeaveType.Leave;
        var mainParty = MobileParty.MainParty;
        __result = mainParty?.Army != null &&
                   !HasLiveBlockingMapEvent(mainParty) &&
                   !HasLiveBesiegedSettlement(mainParty);
        return false;
    }

    internal static bool HasLiveBlockingMapEvent(MobileParty mainParty)
    {
        var mapEvent = mainParty.MapEvent;
        if (mapEvent == null) return false;
        if (mapEvent.IsFinalized) return false;

        // An event the object manager cannot resolve was destroyed authoritatively and this local
        // reference is residue - it must not block the player (Phase I precedent).
        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) &&
            !objectManager.TryGetId(mapEvent, out _))
            return false;

        return true;
    }

    internal static bool HasLiveBesiegedSettlement(MobileParty mainParty)
    {
        var settlement = mainParty.BesiegedSettlement;
        if (settlement == null) return false;

        // Only a camp that IS the settlement's live siege blocks leaving; anything else is a stale
        // siege graph (the same residue GetAttachedArmySiegeState classifies as Incomplete).
        var camp = mainParty.BesiegerCamp;
        var siegeEvent = settlement.SiegeEvent;
        return siegeEvent != null && camp != null &&
               camp.SiegeEvent == siegeEvent &&
               siegeEvent.BesiegerCamp == camp;
    }

    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), nameof(PlayerArmyWaitBehavior.wait_menu_army_leave_on_consequence))]
    [HarmonyPrefix]
    private static bool WaitMenuLeavePrefix(PlayerArmyWaitBehavior __instance, MenuCallbackArgs args)
    {
        // Capture BEFORE the menu/encounter teardown below can disturb it.
        var mainParty = MobileParty.MainParty;
        var army = mainParty.Army;

        if (PlayerEncounter.Current != null)
        {
            PlayerEncounter.Finish(true);
        }
        else
        {
            GameMenu.ExitToLast();
        }
        if (Settlement.CurrentSettlement != null)
        {
            MessageBroker.Instance.Publish(mainParty, new EndSettlementEncounterAttempted(mainParty));
            PartyBase.MainParty.SetVisualAsDirty();
        }

        if (army == null) return false; // already out - nothing to route

        // The authoritative route publishes the removal; do not locally mutate a speculative army.
        var message = new MobilePartyInArmyRemoved(army, mainParty, mainParty);
        MessageBroker.Instance.Publish(__instance, message);
        return false;
    }

    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), nameof(PlayerArmyWaitBehavior.wait_menu_army_abandon_on_consequence))]
    [HarmonyPrefix]
    private static bool Prefixwait_menu_army_abandon_on_consequence(PlayerArmyWaitBehavior __instance, MenuCallbackArgs args)
    {
        var mainParty = MobileParty.MainParty;
        var army = mainParty.Army;

        if (PlayerEncounter.Current != null)
        {
            PlayerEncounter.Finish(true);
        }
        else
        {
            GameMenu.ExitToLast();
        }

        if (army == null) return false;

        var message = new MobilePartyInArmyRemoved(army, mainParty, mainParty);
        MessageBroker.Instance.Publish(__instance, message);
        return false;
    }

    /// <summary>
    /// The navigation-incapability kick menu cleared the army with a raw client-local
    /// <c>Army = null</c>, which sync drops - the server kept the party in the army and pulled it
    /// back. Route it like every other leave path.
    /// </summary>
    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), nameof(PlayerArmyWaitBehavior.player_kicked_out_from_army_consequence))]
    [HarmonyPrefix]
    private static bool PlayerKickedOutPrefix(MenuCallbackArgs args)
    {
        if (ModInformation.IsServer) return true;

        var mainParty = MobileParty.MainParty;
        var army = mainParty.Army;
        if (army != null)
        {
            MessageBroker.Instance.Publish(mainParty, new MobilePartyInArmyRemoved(army, mainParty, mainParty));
        }

        PlayerArmyWaitBehavior.army_dispersed_continue_on_consequence(args);
        return false;
    }
}
