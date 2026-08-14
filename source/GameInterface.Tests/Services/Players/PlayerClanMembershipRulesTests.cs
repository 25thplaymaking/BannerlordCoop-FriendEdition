using GameInterface.Services.Players;
using Xunit;

namespace GameInterface.Tests.Services.Players;

public class PlayerClanMembershipRulesTests
{
    [Fact]
    public void VoluntaryParty_AtLimit_IsRejected()
    {
        Assert.False(PlayerClanMembershipRules.CanCreateIndependentParty(3, 3, emergency: false));
    }

    [Fact]
    public void EmergencyParty_AtLimit_IsAllowed()
    {
        Assert.True(PlayerClanMembershipRules.CanCreateIndependentParty(3, 3, emergency: true));
    }
}
