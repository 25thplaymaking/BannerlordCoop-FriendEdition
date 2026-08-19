using GameInterface.Services.WorkshopMods.Europe1100;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Europe1100;

public class Europe1100CampaignAuthorityGateTests
{
    /// <summary>
    /// The Empires of Europe 1100 conversion is optional, uncatalogued content. The gate runs from
    /// an AutoActivate DI registration, so a throw here would abort the container build and take
    /// the whole session with it — a far worse outcome than the churn the gate exists to prevent.
    /// This is the path every peer without the conversion installed takes.
    /// </summary>
    [Fact]
    public void WithTheConversionAbsent_TheGateInstallsNothingAndDoesNotThrow()
    {
        var harmony = new Harmony("Bannerlord.Coop.Tests.Europe1100.Absent");
        try
        {
            var gate = new Europe1100CampaignAuthorityGate(harmony);

            Assert.Empty(gate.GatedBehaviors);
            Assert.Empty(harmony.GetPatchedMethods());
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Fact]
    public void EveryGatedBehaviourIsDeclaredUnderTheAssemblyThatShipsIt()
    {
        foreach (KeyValuePair<string, string[]> declaration in
                 Europe1100CampaignAuthorityGate.HostOnlyCampaignBehaviors)
        {
            Assert.False(string.IsNullOrWhiteSpace(declaration.Key));
            Assert.NotEmpty(declaration.Value);

            foreach (string typeName in declaration.Value)
            {
                // A behaviour listed under the wrong assembly silently never resolves, so the gate
                // would report success while the behaviour still ticked on every client.
                Assert.StartsWith(declaration.Key + ".", typeName, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheDeclaredBehaviourSetHasNoDuplicates()
    {
        string[] all = Europe1100CampaignAuthorityGate.HostOnlyCampaignBehaviors
            .SelectMany(entry => entry.Value)
            .ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// These are the campaign behaviours read out of the shipped Europe1100 v1.4.7.3 and
    /// SnowballingKingdoms v1.0.21 binaries. Coop references none of those assemblies, so the list is
    /// the only record of what was audited; a conversion update that adds a behaviour must update this
    /// test in the same change.
    /// </summary>
    /// <remarks>
    /// Re-audited 2026-08-19 directly against the shipped assemblies: exactly four of the eleven
    /// Europe1100 DLLs plus SnowballingKingdoms reference <c>CampaignBehaviorBase</c>, and every
    /// assembly referencing <c>CampaignEvents</c> also declares one — so nothing subscribes to campaign
    /// events outside this set. That audit added <c>EoeCustomBattleCampaignBehavior</c>, which had been
    /// excluded on the grounds of a <c>DedicatedServerType="none"</c> tag that does not exist in the
    /// SubModule.xml.
    /// </remarks>
    [Fact]
    public void TheAuditedConversionBehaviourSetIsPinned()
    {
        string[] expected =
        {
            "BattleArtilleryReworked.BACampaignBehavior",
            "ClansResourceAdder.ResourcesAdderEvents",
            "EOE.CustomBattlePatch.SinglePlayer.EoeCustomBattleCampaignBehavior",
            "SnowballingKingdoms.SnowballEvents",
            "SnowballingKingdoms.SnowballFixesBehavior",
            "WhileThyCome.BehaviorBase.AggresiveBehaviour",
            "WhileThyCome.BehaviorBase.PartyBehaviour",
            "WhileThyCome.BehaviorBase.ProvisionBehaviour",
            "WhileThyCome.BehaviorBase.SharedDataAmongBehaviours",
            "WhileThyCome.BehaviorBase.SpawnBehaviour",
            "WhileThyCome.Patches.WTCDiplomaticBartersBehavior",
        };

        string[] actual = Europe1100CampaignAuthorityGate.HostOnlyCampaignBehaviors
            .SelectMany(entry => entry.Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}
