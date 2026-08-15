using Common;
using GameInterface.Services.Issues.Patches;
using Xunit;

namespace GameInterface.Tests.Services.Issues;

[Collection(ModInformationRoleCollection.Name)]
public sealed class IssueManagerDisablePatchesTests
{
    [Fact]
    public void SettlementOwnerChanged_ClientSkipsVanillaIssueListener()
    {
        var wasServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = false;
            Assert.False(IssueManagerDisablePatches.DisableClientSettlementOwnerChanged());
        }
        finally
        {
            ModInformation.IsServer = wasServer;
        }
    }

    [Fact]
    public void SettlementOwnerChanged_ServerRunsVanillaIssueListener()
    {
        var wasServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = true;
            Assert.True(IssueManagerDisablePatches.DisableClientSettlementOwnerChanged());
        }
        finally
        {
            ModInformation.IsServer = wasServer;
        }
    }
}
