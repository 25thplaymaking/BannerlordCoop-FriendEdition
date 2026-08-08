using Common;
using GameInterface.Services.Armies.Patches;
using Xunit;

namespace GameInterface.Tests.Services.Armies;

/// <summary>
/// Regression coverage for settlement-owner events received while an Army snapshot is incomplete.
/// </summary>
[Collection(ModInformationRoleCollection.Name)]
public sealed class ArmyOwnershipEventPatchesTests
{
    [Fact]
    public void OnSettlementOwnerChanged_ClientSkipsServerOwnedArmyReroute()
    {
        var wasServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = false;

            Assert.False(ArmyPatches.OnSettlementOwnerChangedPrefix());
        }
        finally
        {
            ModInformation.IsServer = wasServer;
        }
    }

    [Fact]
    public void OnSettlementOwnerChanged_ServerRunsVanillaArmyReroute()
    {
        var wasServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = true;

            Assert.True(ArmyPatches.OnSettlementOwnerChangedPrefix());
        }
        finally
        {
            ModInformation.IsServer = wasServer;
        }
    }
}
