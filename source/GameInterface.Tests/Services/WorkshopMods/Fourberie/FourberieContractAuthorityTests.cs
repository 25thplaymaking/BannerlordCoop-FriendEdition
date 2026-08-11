using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieContractAuthorityTests
{
    [Fact]
    public void OfferToggle_EnablesAndDisablesOnlyWhenNoContractIsActive()
    {
        IDictionary crime = new Hashtable();
        IDictionary heroes = new Hashtable();

        Assert.True(FourberieContractAuthority.TrySetOffers(
            crime, heroes, enabled: true, out var failure), failure);
        Assert.Equal(0, crime[203]);
        Assert.True(FourberieContractAuthority.TrySetOffers(
            crime, heroes, enabled: false, out failure), failure);
        Assert.False(crime.Contains(203));

        crime[200] = 1;
        heroes["contractGiver"] = "hero_giver";
        Assert.False(FourberieContractAuthority.TrySetOffers(
            crime, heroes, enabled: false, out _));
        Assert.True(crime.Contains(200));
        Assert.True(heroes.Contains("contractGiver"));
    }

    [Fact]
    public void AbortPlan_RequiresBothCanonicalParticipantsAndRunningMarker()
    {
        IDictionary crime = new Hashtable { [200] = 1, [201] = 25_000 };
        IDictionary heroes = new Hashtable
        {
            ["contractGiver"] = "hero_giver",
            ["contractTarget"] = "hero_target",
        };

        Assert.True(FourberieContractAuthority.TryPlanAbort(
            crime, heroes, out var giverId, out var failure), failure);
        Assert.Equal("hero_giver", giverId);

        heroes.Remove("contractTarget");
        Assert.False(FourberieContractAuthority.TryPlanAbort(
            crime, heroes, out _, out _));
        Assert.True(crime.Contains(200));
    }

    [Fact]
    public void AbortCommit_ClearsOnlyContractStateAndSetsServerCooldown()
    {
        IDictionary crime = new Hashtable
        {
            [200] = 1,
            [201] = 25_000,
            [202] = 0,
            [203] = 0,
            [204] = 2,
            [310] = 4,
        };
        IDictionary heroes = new Hashtable
        {
            ["contractGiver"] = "hero_giver",
            ["contractTarget"] = "hero_target",
            ["paymaster"] = "hero_paymaster",
        };

        FourberieContractAuthority.CommitAbort(crime, heroes, cooldown: 9);

        Assert.False(crime.Contains(200));
        Assert.False(crime.Contains(201));
        Assert.Equal(0, crime[202]);
        Assert.Equal(0, crime[203]);
        Assert.Equal(9, crime[204]);
        Assert.Equal(4, crime[310]);
        Assert.False(heroes.Contains("contractGiver"));
        Assert.False(heroes.Contains("contractTarget"));
        Assert.Equal("hero_paymaster", heroes["paymaster"]);
    }

    [Theory]
    [InlineData((int)FourberieOperation.EnableContractOffers, true)]
    [InlineData((int)FourberieOperation.DisableContractOffers, true)]
    [InlineData((int)FourberieOperation.AbortContract, true)]
    [InlineData((int)FourberieOperation.AbortContract, false, 1)]
    public void Protocol_AcceptsOnlyEmptyContractCommandShapes(
        int operation,
        bool expected,
        int value = 0)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            string.Empty, string.Empty, value, Array.Empty<FourberieTroopSelection>());

        Assert.Equal(expected, FourberieOperationProtocol.IsRequestShapeValid(request));
    }

    [Theory]
    [InlineData("<FContractCom>b__151_0", (int)FourberieOperation.EnableContractOffers)]
    [InlineData("<FContractCom>b__151_3", (int)FourberieOperation.DisableContractOffers)]
    [InlineData("<FContractCom>b__151_5", (int)FourberieOperation.AbortContract)]
    [InlineData("<FContractCom>b__151_4", 0)]
    public void ConsequenceMap_RoutesOnlyStateChangingCallbacks(string method, int expected)
    {
        FourberieOperation? actual = FourberieAuthorityPatches.ContractOperationForMethod(method);

        Assert.Equal(expected == 0 ? null : (FourberieOperation?)expected, actual);
    }
}
