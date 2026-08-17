using GameInterface.Services.WorkshopMods.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class DedicatedServerWorkshopSubModuleLoadGuardTests
{
    [Theory]
    [InlineData("ImprovedGarrisons.Main")]
    [InlineData("DismembermentPlus.Main")]
    [InlineData("Fourberie.Main")]
    [InlineData("UnblockableThrust.UnblockableThrustSubmodule")]
    [InlineData("RebellionsAndDemographics.SubModule")]
    public void DedicatedHost_ForcesOnlyAuditedRuntimeSubModules(string classType)
    {
        Assert.True(DedicatedServerWorkshopSubModuleLoadGuard.ShouldForceLoad(
            DedicatedServerType.Custom,
            classType));
    }

    [Theory]
    [InlineData(DedicatedServerType.None, "ImprovedGarrisons.Main")]
    [InlineData(DedicatedServerType.Custom, "Unrelated.Mod.Entry")]
    public void ClientOrUnknownSubModule_UsesBannerlordDefault(
        DedicatedServerType serverType,
        string classType)
    {
        Assert.False(DedicatedServerWorkshopSubModuleLoadGuard.ShouldForceLoad(serverType, classType));
    }

    [Fact]
    public void MissingSubModuleType_UsesBannerlordDefault()
    {
        Assert.False(DedicatedServerWorkshopSubModuleLoadGuard.ShouldForceLoad(
            DedicatedServerType.Custom,
            null));
    }
}
