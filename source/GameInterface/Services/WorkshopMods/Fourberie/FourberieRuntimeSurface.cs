using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Fourberie's monolithic initializer replaces healing, death, finance, crime, diplomacy, combat,
/// food, loyalty, security, transition, power, speed, pricing, and access models. Those results
/// overlap Coop-owned authority funnels and cannot be made safe by executing them only on the
/// server. A campaign where any Fourberie MODEL is already active must therefore be rejected. On a
/// fresh campaign the initializer runs Fourberie's gameplay behaviors (its content is available in
/// co-op) but skips the model registrations, so this gate only ever fires if a model slipped in.
/// </summary>
internal static class FourberieRuntimeSurface
{
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
            var active = new List<string>();
            foreach (var property in models.GetType().GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null) continue;
                var value = property.GetValue(models);
                if (value?.GetType().Assembly != null &&
                    ReferenceEquals(value.GetType().Assembly, fourberieAssembly))
                    active.Add(property.Name + "=" + value.GetType().FullName);
            }

            // NOTE: Fourberie's gameplay behaviors are now INTENTIONALLY added by
            // FourberieAuthorityPatches.InitializeBehaviorsOnlyPrefix (content is available in
            // co-op); only its game-MODEL replacements remain forbidden because they overlap
            // Coop-owned authority. So this gate validates active models only — behaviors present
            // are expected, not a bypass.

            if (active.Count == 0) return true;
            failure =
                "Fourberie game-model replacements were active before Coop's guard could suppress them (" +
                string.Join(", ", active.Distinct(StringComparer.Ordinal)) +
                "). Coop startup was aborted; restart with the hardened feature-blocking boundary active before campaign creation.";
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
