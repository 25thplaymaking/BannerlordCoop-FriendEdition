using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieGrudgeAuthorityTests
{
    [Theory]
    [InlineData(100_000, 200_000, 5, 43_456, 10_000)]
    [InlineData(100_000, 200_000, 25, 43_456, 20_000)]
    [InlineData(100_000, 200_000, 50, 43_456, 40_000)]
    [InlineData(1_000_000, 2_000_000, 100, 46_788, 440_364)]
    public void Quote_UsesPinnedServerRandomRangeAndGrudgeMultiplier(
        int actorGold,
        int clanGold,
        int grudge,
        int randomSurcharge,
        int expected)
    {
        Assert.True(FourberieGrudgeAuthority.TryQuote(
            actorGold, clanGold, grudge, randomSurcharge, out int amount, out var failure), failure);
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void Quote_RejectsIneligibleGrudgeAndClientChosenRandomness()
    {
        Assert.False(FourberieGrudgeAuthority.TryQuote(100, 100, 4, 43_456, out _, out _));
        Assert.False(FourberieGrudgeAuthority.TryQuote(100, 100, 5, 43_455, out _, out _));
        Assert.False(FourberieGrudgeAuthority.TryQuote(100, 100, 5, 46_789, out _, out _));
    }

    [Fact]
    public void Settlement_RevalidatesQuoteGoldSpyAndUnchangedGrudgeThenCommitsExactKeys()
    {
        IDictionary grudges = new Hashtable { ["clan_a"] = 50, ["clan_b"] = 10 };
        IDictionary crime = new Hashtable { [310] = 2, [311] = 7 };

        Assert.True(FourberieGrudgeAuthority.CanSettle(
            grudges, crime, "clan_a", quotedGrudge: 50, actorGold: 50_000,
            requestedAmount: 40_000, quotedAmount: 40_000, out var failure), failure);

        FourberieGrudgeAuthority.Commit(grudges, crime, "clan_a");

        Assert.False(grudges.Contains("clan_a"));
        Assert.Equal(10, grudges["clan_b"]);
        Assert.Equal(1, crime[310]);
        Assert.Equal(7, crime[311]);
    }

    [Theory]
    [InlineData(49, 50_000, 40_000, 40_000)]
    [InlineData(50, 39_999, 40_000, 40_000)]
    [InlineData(50, 50_000, 39_999, 40_000)]
    public void Settlement_RejectsStaleOrUnaffordableConfirmation(
        int liveGrudge,
        int actorGold,
        int requestedAmount,
        int quotedAmount)
    {
        IDictionary grudges = new Hashtable { ["clan_a"] = liveGrudge };
        IDictionary crime = new Hashtable { [310] = 1 };

        Assert.False(FourberieGrudgeAuthority.CanSettle(
            grudges, crime, "clan_a", quotedGrudge: 50, actorGold,
            requestedAmount, quotedAmount, out _));
    }

    [Theory]
    [InlineData((int)FourberieOperation.RequestGrudgeQuote, 0, true)]
    [InlineData((int)FourberieOperation.SettleClanGrudge, 440_364, true)]
    [InlineData((int)FourberieOperation.SettleClanGrudge, 440_365, false)]
    public void Protocol_UsesClanTargetOnlyAndBoundsServerQuote(int operation, int amount, bool expected)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            string.Empty, "clan_a", amount, Array.Empty<FourberieTroopSelection>());

        Assert.Equal(expected, FourberieOperationProtocol.IsRequestShapeValid(request));
    }

    [Fact]
    public void Result_CarriesServerQuoteWithoutChangingStatusSemantics()
    {
        var result = new NetworkFourberieOperationResult(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7,
            FourberieOperationStatus.Accepted, 3, 40_000);

        Assert.Equal(40_000, result.IntValue);
        Assert.Equal(FourberieOperationStatus.Accepted, result.Status);
    }
}
