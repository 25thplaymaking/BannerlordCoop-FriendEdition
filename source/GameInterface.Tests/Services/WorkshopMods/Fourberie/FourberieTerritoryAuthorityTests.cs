using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
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
}
