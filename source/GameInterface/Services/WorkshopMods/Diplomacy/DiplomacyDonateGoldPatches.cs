using Common;
using Common.Logging;
using GameInterface.Policies;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Routes Diplomacy's donation dialog through the same revisioned, replay-safe command protocol as
/// every other Diplomacy player consequence. The server reproduces gold, relation, and both trait
/// effects under the authenticated controller's explicit player context.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyDonateGoldRoutingPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyDonateGoldRoutingPatch));

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.ViewModel.DonateGoldVM");
        var method = type == null ? null : AccessTools.Method(type, "ExecutePropose");
        if (method != null) yield return method;
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(object __instance)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (!ModInformation.IsClient) return false;

        var clan = AccessTools.Field(__instance.GetType(), "_clan")?.GetValue(__instance) as Clan;
        var amount = AccessTools.Property(__instance.GetType(), "IntValue")?.GetValue(__instance) as int? ?? 0;
        if (clan != null && amount > 0)
            DiplomacyPatchRuntime.Current?.TrySubmit(new DiplomacyLocalOperation(
                DiplomacyOperation.DonateGold, clan, intValue: amount));
        else if (clan == null)
        {
            Logger.Warning("Suppressed a Diplomacy gold donation with no resolvable clan.");
        }

        // Close the dialog either way — vanilla's only UI consequence. A zero amount routes
        // nothing, matching vanilla's no-op donation of 0.
        (AccessTools.Field(__instance.GetType(), "_onFinalize")?.GetValue(__instance) as Action)?.Invoke();
        return false;
    }
}
