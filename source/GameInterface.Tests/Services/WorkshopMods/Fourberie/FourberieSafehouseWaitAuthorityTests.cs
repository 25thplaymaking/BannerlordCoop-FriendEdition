using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieSafehouseWaitAuthorityTests
{
    [Theory]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, true, true)]
    [InlineData("hideout_a", "hideout_b", "hideout_a", false, true, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_b", false, true, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", true, true, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, false, false)]
    public void Access_RequiresControllerAtInitializedPinnedSafehouse(
        string requested,
        string currentBase,
        string currentSettlement,
        bool isTown,
        bool initialized,
        bool expected)
    {
        IDictionary crime = new Hashtable();
        if (initialized) crime[550] = 1;

        Assert.Equal(expected, FourberieSafehouseWaitAuthority.CanChangeWaitState(
            requested, currentBase, currentSettlement, isTown, crime, out _));
    }

    [Fact]
    public void Commit_StartsAndStopsOnlyTheCanonicalWaitMarker()
    {
        IDictionary crime = new Hashtable { [7] = 3, [9] = 8 };

        FourberieSafehouseWaitAuthority.Commit(crime, waiting: true);
        Assert.Equal(1, crime[9]);
        Assert.Equal(3, crime[7]);

        FourberieSafehouseWaitAuthority.Commit(crime, waiting: false);
        Assert.False(crime.Contains(9));
        Assert.Equal(3, crime[7]);
    }

    [Theory]
    [InlineData((int)FourberieOperation.StartSafehouseWait)]
    [InlineData((int)FourberieOperation.StopSafehouseWait)]
    public void Protocol_RequiresOnlyTheCurrentSafehouse(int operation)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 10, 6,
            (FourberieOperation)operation,
            "hideout_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
    }

    [Theory]
    [InlineData("<MenuSafeHouse>b__13_5", (int)FourberieOperation.StartSafehouseWait)]
    [InlineData("<MenuSafeHouse>b__13_7", (int)FourberieOperation.StopSafehouseWait)]
    public void ConsequenceMap_RoutesBothWaitActions(string method, int operation) =>
        Assert.Equal((FourberieOperation)operation,
            FourberieAuthorityPatches.SafehouseWaitOperationForMethod(method));
}
