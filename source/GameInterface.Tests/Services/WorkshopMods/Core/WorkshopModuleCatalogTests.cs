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

        Assert.Equal(10, catalog.Modules.Count);
        Assert.Collection(catalog.Modules,
            module => AssertModule(module, "Bannerlord.Harmony", "2859188632", "v2.4.2.248"),
            module => AssertModule(module, "Bannerlord.ButterLib", "2859232415", "v2.11.1"),
            module => AssertModule(module, "Bannerlord.UIExtenderEx", "2859222409", "v2.13.3"),
            module => AssertModule(module, "Bannerlord.MBOptionScreen", "2859238197", "v5.12.2"),
            module => AssertModule(module, "ImprovedGarrisons", "2859265386", "v4.2.0.7"),
            module => AssertModule(module, "DismembermentPlus", "2875093027", "v2.0.8.7"),
            module => AssertModule(module, "Fourberie", "2875710877", "v1.4.7.5"),
            module => AssertModule(module, "Bannerlord.Diplomacy", "2881380744", "v1.4.7"),
            module => AssertModule(module, "UnblockableThrust", "3614435151", "v1.1.3.1"),
            module => AssertModule(module, "PlayerSettlement", "3720376888", "v7.5.0"));
    }

    [Fact]
    public void Catalog_ClassifiesAuthorityAndSynchronizationProfiles()
    {
        var modules = new FriendEditionWorkshopModuleCatalog().Modules;

        Assert.Equal(4, modules.Count(module => module.Role == WorkshopModuleRole.Framework));
        Assert.Single(modules.Where(module =>
            module.Profile == WorkshopCompatibilityProfile.DeterministicMission));
        Assert.Single(modules.Where(module =>
            module.Profile == WorkshopCompatibilityProfile.ClientPresentation));
        Assert.Equal(4, modules.Count(module =>
            module.Profile == WorkshopCompatibilityProfile.ServerAuthoritativeCampaign));

        Assert.DoesNotContain(modules, module => module.ModuleId == "RBM");
        Assert.All(modules, module =>
        {
            Assert.True(module.FeatureActiveExpectedOnServer);
            Assert.True(module.FeatureActiveExpectedOnClient);
        });

        // PlayerSettlement is the one component that must be activated before Coop whenever it is
        // activated at all: its adapter re-guards the module's load-time Harmony patches.
        Assert.True(Assert.Single(modules.Where(m => m.ModuleId == "PlayerSettlement")).LoadsBeforeCoop);
        Assert.All(
            modules.Where(module => module.Role != WorkshopModuleRole.Framework &&
                                    module.ModuleId != "PlayerSettlement"),
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
}
