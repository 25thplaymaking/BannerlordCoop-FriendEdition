using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Verifies that Fourberie's pinned decorator models are either not installed yet (pre-campaign) or
/// are installed as one complete, exact set. The decorators feed the server-owned campaign actions
/// and the rendered clients' read-only calculations; partial registration would make UI policy and
/// authoritative results disagree.
/// </summary>
internal static class FourberieRuntimeSurface
{
    private static readonly HashSet<string> ExpectedModels = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fourberie.FModelDamage",
        "Fourberie.FModelDeath",
        "Fourberie.FModelClanFinance",
        "Fourberie.FModelCrime",
        "Fourberie.FModelLoyalty",
        "Fourberie.FModelSecurity",
        "Fourberie.FModelMobileFood",
        "Fourberie.FModelAccess",
        "Fourberie.FModelDonation",
        "Fourberie.FModelDiplo",
        "Fourberie.FModelPower",
        "Fourberie.FModelMapSpeed",
        "Fourberie.FModelPrice",
        "Fourberie.FModelPartyTransition",
    };

    internal static bool TryAssertNoActiveCampaignSurface(Assembly fourberieAssembly, out string failure)
    {
        failure = null;
        if (fourberieAssembly == null)
        {
            failure = "Fourberie runtime validation has no exact assembly identity";
            return false;
        }

        var campaign = Campaign.Current;
        var models = campaign?.Models;
        if (models == null) return true;

        try
        {
            var active = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in models.GetType().GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null) continue;
                var value = property.GetValue(models);
                if (value?.GetType().Assembly != null &&
                    ReferenceEquals(value.GetType().Assembly, fourberieAssembly))
                    active[value.GetType().FullName] = property.Name;
            }

            if (active.Count == 0) return true;
            var unexpected = active.Keys.Except(ExpectedModels, StringComparer.Ordinal).ToArray();
            var missing = ExpectedModels.Except(active.Keys, StringComparer.Ordinal).ToArray();
            if (unexpected.Length == 0 && missing.Length == 0) return true;
            failure =
                "Fourberie model composition was incomplete (missing: " +
                string.Join(", ", missing) + "; unexpected: " + string.Join(", ", unexpected) +
                "). Coop startup was aborted before calculations could diverge.";
            return false;
        }
        catch (Exception exception)
        {
            failure = "Fourberie active-model validation failed: " +
                      exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }
}
