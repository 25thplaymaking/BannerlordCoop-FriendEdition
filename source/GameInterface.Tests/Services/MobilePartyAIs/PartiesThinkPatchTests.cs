using GameInterface.Services.MobilePartyAIs.Patches;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.MobilePartyAIs;

public class PartiesThinkPatchTests
{
    [Theory]
    [InlineData(AiBehavior.EscortParty, true)]
    [InlineData(AiBehavior.Hold, false)]
    [InlineData(AiBehavior.GoToSettlement, false)]
    public void ShouldTickClientMainParty_OnlyEscortBehaviorResumesFollowing(
        AiBehavior behavior,
        bool expected)
    {
        Assert.Equal(expected, PartiesThinkPatch.ShouldTickClientMainParty(behavior));
    }
}
