using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieSafehouseEstablishmentAuthorityTests
{
    [Theory]
    [InlineData("hideout_a", "hideout_a", true, 25, null, false, true)]
    [InlineData("hideout_a", "hideout_b", true, 25, null, false, false)]
    [InlineData("hideout_a", "hideout_a", false, 25, null, false, false)]
    [InlineData("hideout_a", "hideout_a", true, 24, null, false, false)]
    [InlineData("hideout_a", "hideout_a", true, 25, "hideout_b", true, false)]
    [InlineData("hideout_a", "hideout_a", true, 25, "town_a", false, true)]
    public void Access_RequiresCurrentHideoutRelationAndNoExistingSafehouse(
        string requested,
        string currentSettlement,
        bool isHideout,
        int cultureRelation,
        string currentBase,
        bool currentBaseIsHideout,
        bool expected)
    {
        Assert.Equal(expected, FourberieSafehouseEstablishmentAuthority.CanEstablish(
            requested,
            currentSettlement,
            isHideout,
            cultureRelation,
            currentBase,
            currentBaseIsHideout,
            out _));
    }

    [Fact]
    public void FirstBaseCommit_InitializesSafehouseAndClearsOnlyBaseUpgrades()
    {
        IDictionary crime = new Hashtable
        {
            [11] = 2, [61] = 4, [7] = 3, [310] = 9,
        };
        IDictionary heroes = new Hashtable
        {
            ["paymaster"] = "hero_paymaster",
            ["enforcer"] = "hero_enforcer",
            ["victim7"] = "hero_victim",
        };

        FourberieSafehouseEstablishmentAuthority.Commit(
            crime, heroes, firstBase: true, relicLocation: 4);

        Assert.Equal(1, crime[560]);
        Assert.Equal(0, crime[561]);
        Assert.Equal(1, crime[500]);
        Assert.Equal(0, crime[1500]);
        Assert.Equal(0, crime[1000]);
        Assert.Equal(0, crime[1001]);
        Assert.Equal(4, crime[557]);
        Assert.False(crime.Contains(11));
        Assert.False(crime.Contains(61));
        Assert.Equal(3, crime[7]);
        Assert.Equal(9, crime[310]);
        Assert.False(heroes.Contains("paymaster"));
        Assert.False(heroes.Contains("enforcer"));
        Assert.Equal("hero_victim", heroes["victim7"]);
    }

    [Fact]
    public void TownMigration_PreservesPartyStateSchemeAndExistingRelicChoice()
    {
        IDictionary crime = new Hashtable
        {
            [500] = 5, [1500] = 40, [557] = 3,
            [12] = 2, [7] = 4, [1000] = 70, [1001] = 20,
        };
        IDictionary heroes = new Hashtable { ["paymaster"] = "hero", ["victim7"] = "victim" };

        FourberieSafehouseEstablishmentAuthority.Commit(
            crime, heroes, firstBase: false, relicLocation: 1);

        Assert.Equal(5, crime[500]);
        Assert.Equal(40, crime[1500]);
        Assert.Equal(3, crime[557]);
        Assert.Equal(70, crime[1000]);
        Assert.Equal(20, crime[1001]);
        Assert.Equal(4, crime[7]);
        Assert.False(crime.Contains(12));
        Assert.False(heroes.Contains("paymaster"));
        Assert.Equal("victim", heroes["victim7"]);
    }

    [Fact]
    public void Protocol_RequiresOnlyTheSelectedHideout()
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 8, 4,
            FourberieOperation.EstablishSafehouse,
            "hideout_a", string.Empty, 0, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            request.SessionId, 9, request.ExpectedRevision, request.Operation,
            string.Empty, request.TargetId, request.IntValue, request.Troops)));
    }
}
