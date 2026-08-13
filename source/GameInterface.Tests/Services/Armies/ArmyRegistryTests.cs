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

    [Fact]
    public void RegistrationKey_IsUniquePerArmyLeader_NotSharedByKingdom()
    {
        var firstLeader = ObjectHelper.SkipConstructor<TaleWorlds.CampaignSystem.Party.MobileParty>();
        firstLeader.StringId = "lord_1_party";
        var secondLeader = ObjectHelper.SkipConstructor<TaleWorlds.CampaignSystem.Party.MobileParty>();
        secondLeader.StringId = "lord_2_party";
        var firstArmy = ObjectHelper.SkipConstructor<Army>();
        firstArmy.LeaderParty = firstLeader;
        var secondArmy = ObjectHelper.SkipConstructor<Army>();
        secondArmy.LeaderParty = secondLeader;

        Assert.True(ArmyRegistry.TryGetRegistrationKey(firstArmy, out var firstKey));
        Assert.True(ArmyRegistry.TryGetRegistrationKey(secondArmy, out var secondKey));
        Assert.Equal("leader_lord_1_party", firstKey);
        Assert.Equal("leader_lord_2_party", secondKey);
        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public void RegistrationKey_RejectsArmyWithoutStableLeaderPartyId()
    {
        var army = ObjectHelper.SkipConstructor<Army>();

        Assert.False(ArmyRegistry.TryGetRegistrationKey(army, out var key));
        Assert.Null(key);
    }
}
