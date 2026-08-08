using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.Library;

namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

/// <summary>
/// Keeps voluntary player travel groups together until a player explicitly leaves instead of applying
/// the political-army cohesion timer.
/// </summary>
[HarmonyPatch(typeof(DefaultArmyManagementCalculationModel))]
internal static class PlayerPartyTravelGroupPatches
{
    [HarmonyPatch(nameof(DefaultArmyManagementCalculationModel.CalculateDailyCohesionChange))]
    [HarmonyPrefix]
    private static bool CalculateDailyCohesionChangePrefix(
        Army army,
        bool includeDescriptions,
        ref ExplainedNumber __result)
    {
        if (!PlayerPartyTravelGroup.IsTravelGroup(army))
            return true;

        __result = new ExplainedNumber(0f, includeDescriptions);
        return false;
    }
}

/// <summary>
/// Native dispersion also checks food, war state, and inactivity independently of cohesion. A voluntary
/// travel group has no kingdom by design, so skip those political-army rules until a player leaves it.
/// </summary>
[HarmonyPatch(typeof(Army), nameof(Army.CheckArmyDispersion))]
internal static class PlayerPartyTravelGroupDispersionPatches
{
    [HarmonyPrefix]
    internal static bool CheckArmyDispersionPrefix(Army __instance)
        => !PlayerPartyTravelGroup.IsTravelGroup(__instance);
}
