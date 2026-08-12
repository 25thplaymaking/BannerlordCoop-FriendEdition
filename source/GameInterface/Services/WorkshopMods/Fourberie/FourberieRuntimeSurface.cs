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
            // CampaignSystem.GameModels exposes campaign calculation models as properties, but
            // mission-scoped models (notably AgentApplyDamageModel/FModelDamage) only appear in
            // the underlying GameModelsManager collection. Validate that complete collection so
            // an installed FModelDamage is not falsely reported missing on every loaded save.
            var active = models.GetGameModels()
                .Where(value => value?.GetType().Assembly != null &&
                                ReferenceEquals(value.GetType().Assembly, fourberieAssembly))
                .Select(value => value.GetType().FullName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return TryAssertExactActiveModelNames(active, out failure);
        }
        catch (Exception exception)
        {
            failure = "Fourberie active-model validation failed: " +
                      exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    internal static bool TryAssertExactActiveModelNames(
        IEnumerable<string> activeModelNames,
        out string failure)
    {
        failure = null;
        var active = new HashSet<string>(
            activeModelNames ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        if (active.Count == 0) return true;

        var unexpected = active.Except(ExpectedModels, StringComparer.Ordinal).ToArray();
        var missing = ExpectedModels.Except(active, StringComparer.Ordinal).ToArray();
        if (unexpected.Length == 0 && missing.Length == 0) return true;

        failure =
            "Fourberie model composition was incomplete (missing: " +
            string.Join(", ", missing) + "; unexpected: " + string.Join(", ", unexpected) +
            "). Coop startup was aborted before calculations could diverge.";
        return false;
    }
}
