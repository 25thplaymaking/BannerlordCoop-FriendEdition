using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

using Common;

namespace GameInterface.Services.Heroes.Patches.Disable;

[HarmonyPatch(typeof(PregnancyCampaignBehavior))]
internal class DisablePregnancyCampaignBehavior
{
    [HarmonyPatch(nameof(PregnancyCampaignBehavior.RegisterEvents))]
    [HarmonyPrefix]
    internal static bool RegisterEventsPrefix() => ModInformation.IsServer;
}
