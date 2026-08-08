using HarmonyLib;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Patches;

/// <summary>
/// Bannerlord assumes every active riderless mount has a CommonAIComponent while searching for a horse.
/// Coop controller transitions can temporarily remove that component, so repair candidates immediately
/// before vanilla reads ReservedRiderAgentIndex.
/// </summary>
[HarmonyPatch(typeof(HumanAIComponent), "FindClosestMountAvailable")]
[HarmonyPatchCategory(MissionModule.MountAiSafetyPatchCategory)]
internal static class HumanAIMountSearchSafetyPatch
{
    [HarmonyPrefix]
    private static void EnsureSearchCandidatesHaveCommonAi()
    {
        Mission mission = Mission.Current;
        if (mission == null)
            return;

        foreach (KeyValuePair<Agent, MissionTime> entry in mission.MountsWithoutRiders)
            PuppetMountStateRepairer.EnsureMountSearchInvariant(entry.Key);
    }
}
