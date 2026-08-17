using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieSafehouseTraderAuthorityTests
{
    [Theory]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, true, 8f, true)]
    [InlineData("hideout_a", "hideout_a", "hideout_b", false, true, 8f, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", true, true, 8f, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, false, 8f, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, true, 6.9f, false)]
    public void Access_RequiresPinnedSafehouseMarkerAndElapsedCooldown(
        string requested,
        string currentBase,
        string currentSettlement,
        bool isTown,
        bool hasSafehouseMarker,
        float elapsedDays,
        bool expected)
    {
        IDictionary crime = new Hashtable { [556] = 2 };
        if (hasSafehouseMarker) crime[550] = 1;

        Assert.Equal(expected, FourberieSafehouseTraderAuthority.CanExecute(
            requested, currentBase, currentSettlement, isTown, crime, elapsedDays, out _));
    }

    [Theory]
    [InlineData((int)FourberieOperation.SellQuarterSlaves, 31, 2_325, 1)]
    [InlineData((int)FourberieOperation.SellHalfSlaves, 62, 4_650, 1)]
    [InlineData((int)FourberieOperation.DeclineCrookedTrader, 0, 0, 3)]
    [InlineData((int)FourberieOperation.RobCrookedTrader, 0, 2_500, 10)]
    public void Plan_DerivesQuantityRewardAndCooldownFromCanonicalState(
        int operation,
        int expectedSlaves,
        int expectedReward,
        int expectedIncrease)
    {
        IDictionary crime = new Hashtable { [1500] = 125, [556] = 2 };

        Assert.True(FourberieSafehouseTraderAuthority.TryCreatePlan(
            crime,
            (FourberieOperation)operation,
            quantity => quantity * 75,
            () => 5_001f,
            out FourberieSafehouseTraderPlan plan,
            out var failure), failure);
        Assert.Equal(expectedSlaves, plan.SlaveReduction);
        Assert.Equal(expectedReward, plan.GoldReward);
        Assert.Equal(expectedIncrease, plan.CooldownIncrease);
    }

    [Fact]
    public void Commit_UpdatesOnlyTraderStateUsingThePinnedPlan()
    {
        IDictionary crime = new Hashtable { [1500] = 125, [556] = 2, [310] = 7 };
        IDictionary times = new Hashtable { [556] = "old", [7] = "scheme" };
        Assert.True(FourberieSafehouseTraderAuthority.TryCreatePlan(
            crime,
            FourberieOperation.SellQuarterSlaves,
            quantity => quantity * 75,
            () => 0,
            out FourberieSafehouseTraderPlan plan,
            out var failure), failure);

        FourberieSafehouseTraderAuthority.Commit(crime, times, plan, "now");

        Assert.Equal(94, crime[1500]);
        Assert.Equal(3, crime[556]);
        Assert.Equal(7, crime[310]);
        Assert.Equal("now", times[556]);
        Assert.Equal("scheme", times[7]);
    }

    [Theory]
    [InlineData((int)FourberieOperation.SellQuarterSlaves)]
    [InlineData((int)FourberieOperation.SellHalfSlaves)]
    [InlineData((int)FourberieOperation.DeclineCrookedTrader)]
    [InlineData((int)FourberieOperation.RobCrookedTrader)]
    public void Protocol_RequiresOnlyTheCurrentSafehouse(int operation)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            "hideout_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            request.SessionId, 8, request.ExpectedRevision, request.Operation,
            string.Empty, request.TargetId, request.IntValue, request.Troops)));
    }

    [Theory]
    [InlineData("<AddDialogsSafeHouse>b__9_18", (int)FourberieOperation.SellQuarterSlaves)]
    [InlineData("<AddDialogsSafeHouse>b__9_20", (int)FourberieOperation.SellHalfSlaves)]
    [InlineData("<AddDialogsSafeHouse>b__9_22", (int)FourberieOperation.DeclineCrookedTrader)]
    [InlineData("<AddDialogsSafeHouse>b__9_24", (int)FourberieOperation.RobCrookedTrader)]
    public void ConsequenceMap_RoutesEveryCrookedTraderAction(string method, int operation) =>
        Assert.Equal((FourberieOperation)operation,
            FourberieAuthorityPatches.SafehouseTraderOperationForMethod(method));
}
