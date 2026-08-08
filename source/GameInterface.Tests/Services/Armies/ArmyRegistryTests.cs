using Common.Util;
using GameInterface.Services.Armies;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.Armies;

public class ArmyRegistryTests
{
    [Fact]
    public void TryTrackKingdomFreeArmy_NullArmyIsIgnoredWithoutEnteringSet()
    {
        var registeredArmies = new HashSet<Army>();

        Assert.False(ArmyRegistry.TryTrackKingdomFreeArmy(null, registeredArmies));
        Assert.Empty(registeredArmies);
    }

    [Fact]
    public void TryTrackKingdomFreeArmy_DuplicateIsIgnored()
    {
        var army = ObjectHelper.SkipConstructor<Army>();
        var registeredArmies = new HashSet<Army>();

        Assert.True(ArmyRegistry.TryTrackKingdomFreeArmy(army, registeredArmies));
        Assert.False(ArmyRegistry.TryTrackKingdomFreeArmy(army, registeredArmies));
        Assert.Single(registeredArmies);
    }
}
