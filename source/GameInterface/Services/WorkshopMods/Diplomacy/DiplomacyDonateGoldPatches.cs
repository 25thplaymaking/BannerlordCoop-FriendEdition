using Common;
using Common.Logging;
using Common.Messaging;
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
/// Routes Diplomacy's "Donate Gold" dialog through the server — the first Workshop mod action to
/// get the four-part intent shape instead of a block. Vanilla's <c>ExecutePropose</c> takes the
/// gold from <c>Hero.MainHero</c>, distributes it across the clan's lords, and applies relation
/// and trait gains — all local writes that replicate nothing. The client now only announces the
/// intent (ids and the chosen amount) and closes its dialog; the server validates ownership and
/// the amount against its own books, runs the mod's own hero-parameterised apply, and the results
/// arrive back as ordinary gold/relation deltas through Coop's existing funnels.
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
        // The hosting player's own donation runs vanilla: MainHero is the right hero there, and
        // every write it makes replicates through the funnels Coop already owns.
        if (ModInformation.IsServer) return true;

        var clan = AccessTools.Field(__instance.GetType(), "_clan")?.GetValue(__instance) as Clan;
        var amount = AccessTools.Property(__instance.GetType(), "IntValue")?.GetValue(__instance) as int? ?? 0;
        var giver = Hero.MainHero;

        if (giver != null && clan != null && amount > 0)
        {
            MessageBroker.Instance.Publish(__instance, new DiplomacyGoldDonationAttempted(giver, clan, amount));
        }
        else if (giver == null || clan == null)
        {
            Logger.Warning("Suppressed a Diplomacy gold donation with no resolvable giver or clan.");
        }

        // Close the dialog either way — vanilla's only UI consequence. A zero amount routes
        // nothing, matching vanilla's no-op donation of 0.
        (AccessTools.Field(__instance.GetType(), "_onFinalize")?.GetValue(__instance) as Action)?.Invoke();
        return false;
    }
}
