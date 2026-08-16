using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.WorkshopMods.Diplomacy;
using GameInterface.Services.WorkshopMods.Fourberie;
using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
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

    private static void AssertUnavailable(WorkshopCapability capability)
    {
        Assert.False(capability.Enabled);
        Assert.Equal("authority-command-route-unavailable", capability.Reason);
    }
}
