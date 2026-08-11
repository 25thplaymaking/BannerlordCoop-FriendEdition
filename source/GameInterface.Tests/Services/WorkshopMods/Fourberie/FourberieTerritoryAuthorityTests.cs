using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieTerritoryAuthorityTests
{
    [Fact]
    public void MainBaseSelection_RequiresPinnedTownTerritory()
    {
        IEnumerable territories = new[] { "town_a", "town_b" };

        Assert.True(FourberieTerritoryAuthority.CanMakeMainBase(
            territories, "town_a", isTown: true, out var failure), failure);
        Assert.False(FourberieTerritoryAuthority.CanMakeMainBase(
            territories, "town_c", isTown: true, out _));
        Assert.False(FourberieTerritoryAuthority.CanMakeMainBase(
            territories, "town_a", isTown: false, out _));
    }

    [Fact]
    public void MainBaseCommit_ResetsRolesAndReplacesTimestampWithoutTouchingOtherState()
    {
        IDictionary times = new Hashtable { [500] = "old", [7] = "scheme" };
        IDictionary heroes = new Hashtable
        {
            ["paymaster"] = "hero_paymaster",
            ["enforcer"] = "hero_enforcer",
            ["victim7"] = "hero_victim",
        };

        FourberieTerritoryAuthority.CommitMainBase(times, heroes, "new");

        Assert.Equal("new", times[500]);
        Assert.Equal("scheme", times[7]);
        Assert.False(heroes.Contains("paymaster"));
        Assert.False(heroes.Contains("enforcer"));
        Assert.Equal("hero_victim", heroes["victim7"]);
    }

    [Fact]
    public void Protocol_RequiresOneStableSettlementAndNoOtherContext()
    {
        var valid = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            FourberieOperation.SetMainCrimeBase,
            "town_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(valid));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            valid.SessionId, 8, valid.ExpectedRevision, valid.Operation,
            string.Empty, valid.TargetId, valid.IntValue, valid.Troops)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            valid.SessionId, 9, valid.ExpectedRevision, valid.Operation,
            valid.SettlementId, "hero_a", valid.IntValue, valid.Troops)));
    }

    [Theory]
    [InlineData("town_b", "town_a", true)]
    [InlineData("town_a", "town_a", false)]
    [InlineData("town_c", "town_a", false)]
    public void NonBaseRemoval_RequiresAListedDifferentTerritory(
        string selected,
        string currentBase,
        bool expected)
    {
        IEnumerable territories = new[] { "town_a", "town_b" };

        Assert.Equal(expected, FourberieTerritoryAuthority.CanRemoveNonBase(
            territories, selected, currentBase, out _));
    }

    [Fact]
    public void BaseAbandonment_ClearsPinnedBaseAndSchemeState()
    {
        IList territories = new ArrayList { "town_a", "town_b" };
        IDictionary crime = new Hashtable
        {
            [1] = 0, [11] = 2, [61] = 3,
            [7] = 4, [74] = 1, [740] = 3, [750] = 2,
            [8] = 6, [841] = 1, [850] = 1,
            [500] = 2, [310] = 5,
        };
        IDictionary heroes = new Hashtable
        {
            ["paymaster"] = "hero_paymaster",
            ["enforcer"] = "hero_enforcer",
            ["victim7"] = "hero_victim_7",
            ["victim8"] = "hero_victim_8",
            ["contractGiver"] = "hero_contract",
        };
        IDictionary times = new Hashtable { [500] = "base", [7] = "scheme7", [8] = "scheme8" };

        Assert.True(FourberieTerritoryAuthority.CanAbandonBase(
            territories, "town_a", "town_a", out var failure), failure);
        FourberieTerritoryAuthority.CommitAbandonBase(
            territories, "town_a", crime, heroes, times);

        Assert.DoesNotContain("town_a", territories.Cast<object>());
        Assert.Contains("town_b", territories.Cast<object>());
        Assert.False(crime.Contains(1));
        Assert.False(crime.Contains(61));
        Assert.False(crime.Contains(7));
        Assert.False(crime.Contains(740));
        Assert.False(crime.Contains(841));
        Assert.False(crime.Contains(500));
        Assert.Equal(5, crime[310]);
        Assert.False(heroes.Contains("paymaster"));
        Assert.False(heroes.Contains("enforcer"));
        Assert.False(heroes.Contains("victim7"));
        Assert.False(heroes.Contains("victim8"));
        Assert.Equal("hero_contract", heroes["contractGiver"]);
        Assert.False(times.Contains(500));
        Assert.False(times.Contains(7));
        Assert.False(times.Contains(8));
    }

    [Theory]
    [InlineData((int)FourberieOperation.RemoveTerritory)]
    [InlineData((int)FourberieOperation.AbandonTownCrimeBase)]
    public void Protocol_RequiresSettlementOnlyForTerritoryRemoval(int operation)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            "town_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
    }
}
