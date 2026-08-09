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
/// server. A campaign where any Fourberie model is already active must therefore be rejected; on
/// a fresh campaign the initializer itself is blocked before it can install them.
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

            var getBehavior = typeof(Campaign).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(method => method.Name == nameof(Campaign.GetCampaignBehavior) &&
                                  method.IsGenericMethodDefinition &&
                                  method.GetParameters().Length == 0);
            foreach (var typeName in FourberieCompatibilityManifest.BehaviorTypeNames)
            {
                var behaviorType = fourberieAssembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (behaviorType == null) continue;
                var behavior = getBehavior.MakeGenericMethod(behaviorType).Invoke(campaign, null);
                if (behavior != null) active.Add("Behavior=" + typeName);
            }

            if (active.Count == 0) return true;
            failure =
                "Fourberie campaign behaviors/models were already installed before Coop's guard activated (" +
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
