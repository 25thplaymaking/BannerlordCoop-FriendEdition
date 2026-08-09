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

        Assert.Equal(11, catalog.Modules.Count);
        Assert.Collection(catalog.Modules,
            module => AssertModule(module, "Bannerlord.Harmony", "2859188632", "v2.4.2.248"),
            module => AssertModule(module, "Bannerlord.ButterLib", "2859232415", "v2.11.1"),
            module => AssertModule(module, "Bannerlord.UIExtenderEx", "2859222409", "v2.13.3"),
            module => AssertModule(module, "Bannerlord.MBOptionScreen", "2859238197", "v5.12.2"),
            module => AssertModule(module, "RBM", "2859251492", "v4.3.4"),
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
        Assert.Equal(2, modules.Count(module =>
            module.Profile == WorkshopCompatibilityProfile.DeterministicMission));
        Assert.Single(modules.Where(module =>
            module.Profile == WorkshopCompatibilityProfile.ClientPresentation));
        Assert.Equal(4, modules.Count(module =>
            module.Profile == WorkshopCompatibilityProfile.ServerAuthoritativeCampaign));

        // The group runs the full modded experience: every bundled component is expected active
        // on both peers, and the handshake byte-verifies each one every session.
        Assert.All(modules, module => Assert.True(module.FeatureActiveExpectedOnServer));
        Assert.All(modules, module => Assert.True(module.FeatureActiveExpectedOnClient));
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
