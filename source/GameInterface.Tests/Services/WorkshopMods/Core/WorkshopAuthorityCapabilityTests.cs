using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.WorkshopMods.Diplomacy;
using GameInterface.Services.WorkshopMods.Fourberie;
using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
using GameInterface.Services.WorkshopMods.RebellionsAndDemographics;
using Moq;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopAuthorityCapabilityTests
{
    [Fact]
    public void MatureModGameplayCapabilities_StayDisabledUntilTheirRealCommandRoutesExist()
    {
        var config = new Mock<IModConfig>();
        config.SetupGet(value => value.Data).Returns(new ModConfigData
        {
            ModOptions = new ModOptionsData(),
        });

        AssertUnavailable(new DiplomacyCapabilitySource(config.Object).CaptureCapabilities().Single());
        AssertUnavailable(new FourberieCapabilitySource(config.Object).CaptureCapabilities().Single());
        AssertUnavailable(new ImprovedGarrisonsCapabilitySource(config.Object).CaptureCapabilities().Single());
    }

    [Fact]
    public void RebellionsAndDemographics_AdvertisesTheSourceMigrationHoldInsteadOfAReadyCapability()
    {
        WorkshopCapability capability = new RebellionsAndDemographicsCapabilitySource()
            .CaptureCapabilities().Single();

        Assert.Equal(RebellionsAndDemographicsCapabilitySource.ModuleId, capability.ModuleId);
        Assert.Equal(RebellionsAndDemographicsCapabilitySource.Operation, capability.Operation);
        Assert.False(capability.Enabled);
        Assert.Equal(RebellionsAndDemographicsCapabilitySource.HoldReason, capability.Reason);
    }

    private static void AssertUnavailable(WorkshopCapability capability)
    {
        Assert.False(capability.Enabled);
        Assert.Equal("authority-command-route-unavailable", capability.Reason);
    }
}
