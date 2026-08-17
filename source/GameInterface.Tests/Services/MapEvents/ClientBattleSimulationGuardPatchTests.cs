using GameInterface.Services.MapEvents.Patches;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public sealed class ClientBattleSimulationGuardPatchTests
{
    [Fact]
    public void EncounterLeave_AfterServerUnregistersBattle_StillSuppressesClientSimulation()
    {
        Assert.True(ClientBattleSimulationGuardPatch.ShouldSuppressLocalSimulation(
            isInEncounterLeave: true,
            isOriginalAllowed: false,
            isServer: false,
            hasMapEvent: true));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void OtherSimulationPaths_AreNotSuppressed(
        bool isInEncounterLeave,
        bool isOriginalAllowed,
        bool isServer,
        bool hasMapEvent)
    {
        Assert.False(ClientBattleSimulationGuardPatch.ShouldSuppressLocalSimulation(
            isInEncounterLeave,
            isOriginalAllowed,
            isServer,
            hasMapEvent));
    }
}
