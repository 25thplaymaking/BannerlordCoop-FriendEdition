using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieSafehouseReturnAuthorityTests
{
    [Theory]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, 1, true)]
    [InlineData("hideout_a", "hideout_b", "hideout_a", false, 1, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_b", false, 1, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", true, 1, false)]
    [InlineData("hideout_a", "hideout_a", "hideout_a", false, 0, false)]
    public void Access_RequiresPendingReturnAtThePinnedSafehouse(
        string requested,
        string currentBase,
        string currentSettlement,
        bool isTown,
        int marker,
        bool expected)
    {
        IDictionary crime = new Hashtable { [550] = marker };

        Assert.Equal(expected, FourberieSafehouseReturnAuthority.CanComplete(
            requested, currentBase, currentSettlement, isTown, crime, out _));
    }

    [Fact]
    public void Commit_RemovesOnlyThePendingReturnMarker()
    {
        IDictionary crime = new Hashtable { [7] = 3, [550] = 1 };

        FourberieSafehouseReturnAuthority.Commit(crime);

        Assert.False(crime.Contains(550));
        Assert.Equal(3, crime[7]);
    }

    [Fact]
    public void Protocol_RequiresOnlyTheCurrentSafehouse()
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 11, 7,
            FourberieOperation.CompleteSafehouseReturn,
            "hideout_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
    }
}
