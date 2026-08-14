using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Villages.Patches;

[HarmonyPatch]
internal class VillageRaidEndPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "wait_menu_end_raiding_on_consequence");
        yield return AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "wait_menu_end_raiding_at_army_by_leaving_on_consequence");
        yield return AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "wait_menu_end_raiding_at_army_by_abandoning_on_consequence");
    }

    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (ModInformation.IsServer) return true;

        // Only route a finalize request for an event the server can still resolve. When the server
        // already finalized this raid, its replicated destroy unregistered the local event — a
        // request for it can never produce an id, so the click would be silently eaten and the
        // player softlocked on the looting menu with a dead "End raid" button.
        var mapEvent = PlayerEncounter.Battle ?? MapEvent.PlayerMapEvent;
        if (mapEvent != null && TryGetMapEventId(mapEvent))
        {
            MessageBroker.Instance.Publish(mapEvent, new MapEventFinalizeAttempted(mapEvent));
            return false;
        }

        CloseLocalRaidMenu();
        return false;
    }

    private static bool TryGetMapEventId(MapEvent mapEvent)
    {
        return ContainerProvider.TryResolve<IObjectManager>(out var objectManager) &&
               objectManager.TryGetId(mapEvent, out _);
    }

    private static void CloseLocalRaidMenu()
    {
        // Detach the stale event before finishing: the event is already destroyed authoritatively,
        // so Finish must not try to finalize it (the client-side FinalizeEventAux patch would only
        // re-publish an unresolvable finalize request).
        var mainParty = MobileParty.MainParty;
        if (mainParty?.Party != null)
            mainParty.Party._mapEventSide = null;

        var encounter = PlayerEncounter.Current;
        if (encounter != null)
        {
            encounter._mapEvent = null;
            PlayerEncounter.Finish(true);
        }

        GameMenu.ExitToLast();
    }
}