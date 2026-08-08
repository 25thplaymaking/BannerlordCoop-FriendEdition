using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

using Common;
using GameInterface.Services.Heroes.Extensions;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Heroes.Patches.Disable;

[HarmonyPatch(typeof(AgingCampaignBehavior))]
internal class DisableAgingCampaignBehavior
{
    [HarmonyPatch(nameof(AgingCampaignBehavior.RegisterEvents))]
    [HarmonyPrefix]
    internal static bool RegisterEventsPrefix() => ModInformation.IsServer;

    // Natural death ultimately enters the single-player heir-selection flow, which assumes one
    // Hero.MainHero and one local UI. Keep the world lifecycle authoritative on the server while
    // protecting every registered co-op player hero until that transition has a networked flow.
    [HarmonyPatch("DailyTickHero")]
    [HarmonyPrefix]
    internal static bool DailyTickHeroPrefix(Hero hero)
        => ModInformation.IsServer && (hero == null || !hero.IsPlayerHero());
}
