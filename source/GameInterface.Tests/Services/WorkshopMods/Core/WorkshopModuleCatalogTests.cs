using GameInterface.Services.WorkshopMods.Core;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public class WorkshopModuleCatalogTests
{
    [Fact]
    public void Catalog_ContainsCanonicalPrivateSuite()
    {
        var catalog = new FriendEditionWorkshopModuleCatalog();

        Assert.Equal(14, catalog.Modules.Count);
        Assert.Collection(catalog.Modules,
            module => AssertModule(module, "Bannerlord.Harmony", "2859188632", "v2.4.2.248"),
            module => AssertModule(module, "Bannerlord.ButterLib", "2859232415", "v2.11.1"),
            module => AssertModule(module, "Bannerlord.UIExtenderEx", "2859222409", "v2.13.3"),
            module => AssertModule(module, "Bannerlord.MBOptionScreen", "2859238197", "v5.12.2"),
            module => AssertModule(module, "OpenSourceSaddlery", "3010990914", "v2.0.0"),
            module => AssertModule(module, "OpenSourceWeaponry", "3010984416", "v2.0.1"),
            module => AssertModule(module, "OpenSourceArmory", "3011479883", "v2.0.0"),
            module => AssertModule(module, "ImprovedGarrisons", "2859265386", "v4.2.0.7"),
            module => AssertModule(module, "DismembermentPlus", "2875093027", "v2.0.8.8"),
            module => AssertModule(module, "Fourberie", "2875710877", "v1.4.7.6"),
            module => AssertModule(module, "Bannerlord.Diplomacy", "2881380744", "v1.4.7"),
            module => AssertModule(module, "UnblockableThrust", "3614435151", "v1.1.3.1"),
            module => AssertModule(module, "PlayerSettlement", "3720376888", "v7.5.0"),
            module => AssertModule(module, "RebellionsAndDemographics", "3644127631", "v3.0.1"));
    }

    [Fact]
    public void Catalog_ClassifiesAuthorityAndSynchronizationProfiles()
    {
        var modules = new FriendEditionWorkshopModuleCatalog().Modules;

        Assert.Equal(4, modules.Count(module => module.Role == WorkshopModuleRole.Framework));
        Assert.Single(modules.Where(module =>
            module.Profile == WorkshopCompatibilityProfile.DeterministicMission));
        Assert.Equal(
            new[]
            {
                "ImprovedGarrisons",
                "Fourberie",
                "Bannerlord.Diplomacy",
                "PlayerSettlement",
                "RebellionsAndDemographics",
            },
            modules
                .Where(module => module.Profile == WorkshopCompatibilityProfile.ServerAuthoritativeCampaign)
                .Select(module => module.ModuleId));

        // RBM is not in the package at all — different from the holds below, which stay packaged
        // and receipt-verified so the suite receipt keeps validating 1:1 against this catalog.
        Assert.DoesNotContain(modules, module => module.ModuleId == "RBM");
        WorkshopModuleExpectation held = Assert.Single(modules.Where(module =>
            module.ModuleId == "RebellionsAndDemographics"));
        Assert.False(held.FeatureActiveExpectedOnServer);
        Assert.False(held.FeatureActiveExpectedOnClient);

        Assert.Collection(
            modules.Where(module => module.ModuleId.StartsWith("OpenSource")),
            module => AssertHeldAllPeersExactGearModule(module, "OpenSourceSaddlery"),
            module => AssertHeldAllPeersExactGearModule(module, "OpenSourceWeaponry"),
            module => AssertHeldAllPeersExactGearModule(module, "OpenSourceArmory"));

        // PlayerSettlement and the asset-only Open Source modules must load before Coop. The
        // former re-guards load-time patches; the latter supply the canonical item definitions.
        Assert.True(Assert.Single(modules.Where(m => m.ModuleId == "PlayerSettlement")).LoadsBeforeCoop);

        // Held inactive for the Europe 1100 conversion: still packaged and hash-verified, but not
        // launched. FeatureActiveExpected* is an equality policy, so both roles must read false or
        // the join is refused for a module nobody activates.
        WorkshopModuleExpectation playerSettlement =
            Assert.Single(modules.Where(m => m.ModuleId == "PlayerSettlement"));
        Assert.False(playerSettlement.FeatureActiveExpectedOnServer);
        Assert.False(playerSettlement.FeatureActiveExpectedOnClient);
        Assert.All(modules.Where(module => module.ModuleId.StartsWith("OpenSource")), module =>
            Assert.True(module.LoadsBeforeCoop));
        Assert.All(
            modules.Where(module => module.Role != WorkshopModuleRole.Framework &&
                                    module.ModuleId != "PlayerSettlement" &&
                                    module.ModuleId != "RebellionsAndDemographics" &&
                                    !module.ModuleId.StartsWith("OpenSource")),
            module => Assert.False(module.LoadsBeforeCoop));
    }

    [Fact]
    public void Catalog_ExcludesOptionalFourberieCrossModAddOns()
    {
        var modules = new FriendEditionWorkshopModuleCatalog().Modules;

        Assert.DoesNotContain(modules, module => module.ModuleId == "HomesteadsReloaded");
        Assert.DoesNotContain(modules, module => module.ModuleId == "BellumCivile");
    }

    private static void AssertModule(
        WorkshopModuleExpectation module,
        string id,
        string workshopId,
        string version)
    {
        Assert.Equal(id, module.ModuleId);
        Assert.Equal(workshopId, module.WorkshopId);
        Assert.Equal(version, module.Version);
    }

    /// <summary>
    /// The gear modules keep their identity, profile and pre-Coop slot so re-enabling one is a
    /// two-flag change, but they are held inactive for the Europe 1100 conversion, which supplies
    /// its own item set.
    /// </summary>
    private static void AssertHeldAllPeersExactGearModule(
        WorkshopModuleExpectation module,
        string moduleId)
    {
        Assert.Equal(moduleId, module.ModuleId);
        Assert.Equal(WorkshopModuleRole.Presentation, module.Role);
        Assert.Equal(WorkshopCompatibilityProfile.AllPeersExact, module.Profile);
        Assert.False(module.FeatureActiveExpectedOnServer);
        Assert.False(module.FeatureActiveExpectedOnClient);
        Assert.True(module.LoadsBeforeCoop);
    }
}
