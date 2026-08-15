using HarmonyLib;
using TaleWorlds.CampaignSystem.Issues;
using Common;

namespace GameInterface.Services.Issues.Patches;

[HarmonyPatch(typeof(IssueManager))]
internal class IssueManagerDisablePatches
{
    [HarmonyPatch(nameof(IssueManager.DailyTick))]
    [HarmonyPrefix]
    private static bool DisableIssueDailyTick() => false;

    [HarmonyPatch(nameof(IssueManager.HourlyTick))]
    [HarmonyPrefix]
    private static bool DisableIssueHourlyTick() => false;

    /// <summary>
    /// Settlement ownership is replayed on clients for UI and replicated-state listeners, but IssueManager's
    /// vanilla listener assumes a complete local issue graph. A newly captured settlement can reach it while
    /// that graph is only partially replicated, causing a null dereference when the Kingdom screen refreshes.
    /// Issue generation remains server-authoritative, so only the server may run this listener.
    /// </summary>
    [HarmonyPatch(nameof(IssueManager.OnSettlementOwnerChanged))]
    [HarmonyPrefix]
    internal static bool DisableClientSettlementOwnerChanged() => ModInformation.IsServer;
}
