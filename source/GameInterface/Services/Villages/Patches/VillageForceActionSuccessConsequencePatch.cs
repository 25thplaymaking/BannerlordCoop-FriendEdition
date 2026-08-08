using Common;
using GameInterface.Policies;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;

namespace GameInterface.Services.Villages.Patches;

[HarmonyPatch]
internal class VillageForceActionSuccessConsequencePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "village_force_supplies_ended_successfully_on_consequence");
        yield return AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "village_force_volunteers_ended_successfully_on_consequence");
    }

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod, MenuCallbackArgs args)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        // Rewards, village damage, and cooldowns are applied authoritatively by
        // VillageHostileActionInterface.ApplyForceActionOutcome. Suppressing the whole native
        // consequence also suppressed its client-only tail, though, leaving Continue on the result
        // menu with nowhere to go. Reproduce only that presentation/finalize tail here.
        if (ModInformation.IsServer) return false;

        args.optionLeaveType = GameMenuOption.LeaveType.Leave;
        GameMenu.SwitchToMenu("village");

        var encounter = PlayerEncounter.Current;
        if (encounter == null) return false;

        if (__originalMethod?.Name == "village_force_supplies_ended_successfully_on_consequence")
            encounter.ForceSupplies = false;
        else if (__originalMethod?.Name == "village_force_volunteers_ended_successfully_on_consequence")
            encounter.ForceVolunteers = false;

        // On a client FinalizeBattle reaches the existing MapEvent.FinalizeEventAux patch, which
        // asks the server to finalize instead of mutating the shared event locally.
        encounter.FinalizeBattle();

        return false;
    }
}
