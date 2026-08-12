using Common.Util;
using GameInterface.Services.MobileParties.Patches;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

public class MobilePartyHelperPatchesTests
{
    [Fact]
    public void ShouldUseSettlementSpawn_RequiresHeroToBeInSettlement()
    {
        Assert.False(MobilePartyHelperPatches.ShouldUseSettlementSpawn(null));
        Assert.True(MobilePartyHelperPatches.ShouldUseSettlementSpawn(
            ObjectHelper.SkipConstructor<Settlement>()));
    }
}
