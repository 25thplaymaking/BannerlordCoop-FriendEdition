using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieSchemeAuthorityTests
{
    [Theory]
    [InlineData(7, 1, 5_000, 310, 1, 1)]
    [InlineData(4, 3, 0, 310, 1, 3)]
    [InlineData(8, 3, 120_000, 310, 2, 8)]
    [InlineData(1, 2, 70_000, 310, 1, 6)]
    [InlineData(6, 3, 60_000, 320, 2, 8)]
    [InlineData(3, 1, 65_000, 320, 1, 6)]
    public void Plan_UsesPinnedServerCostAgentPoolAndDuration(
        int scheme,
        int targetRank,
        int expectedFee,
        int expectedPool,
        int expectedAgents,
        int expectedDuration)
    {
        Assert.True(FourberieSchemeAuthority.TryPlan(
            scheme, targetRank, randomOffset: 1, out var plan, out var failure), failure);

        Assert.Equal(expectedFee, plan.Fee);
        Assert.Equal(expectedPool, plan.AgentPoolKey);
        Assert.Equal(expectedAgents, plan.AgentCost);
        Assert.Equal(expectedDuration, plan.DurationDays);
    }

    [Theory]
    [InlineData(1, false, true, true, true, 30, false)]
    [InlineData(1, true, true, true, true, 30, true)]
    [InlineData(3, true, false, true, true, 30, false)]
    [InlineData(4, true, true, false, true, 30, false)]
    [InlineData(5, true, true, true, false, 30, false)]
    [InlineData(6, true, true, true, true, 30, false)]
    [InlineData(7, true, true, true, true, 29, false)]
    [InlineData(8, true, true, true, true, 30, true)]
    public void Eligibility_RevalidatesTargetFactsOnTheServer(
        int scheme,
        bool influenceAboveMinimum,
        bool canDie,
        bool clanLeader,
        bool partyLeader,
        int network,
        bool expected)
    {
        bool actual = FourberieSchemeAuthority.IsTargetEligible(
            scheme,
            influenceAboveMinimum,
            canDie,
            clanLeader,
            partyLeader,
            atWarWithActor: false,
            network);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StartAndOutcome_ConsumeServerResourcesAndCreateOneCanonicalResult()
    {
        IDictionary crime = new Hashtable { [7] = 6, [320] = 2 };
        Assert.True(FourberieSchemeAuthority.TryPlan(
            scheme: 6, targetRank: 3, randomOffset: 0, out var plan, out var planFailure), planFailure);

        Assert.True(FourberieSchemeAuthority.TryStart(
            crime, slot: 7, actorGold: 60_000, plan, out var failure), failure);
        Assert.Equal(0, crime[320]);
        Assert.Equal(1, crime[74]);
        Assert.Equal(7, crime[740]);

        FourberieSchemeAuthority.SetOutcome(crime, slot: 7, scheme: 6, success: false, detected: true);
        Assert.Equal(6, crime[73]);
        Assert.False(crime.Contains(71));
        Assert.False(crime.Contains(72));
    }

    [Fact]
    public void AbortAndClear_RequireTheMatchingServerLifecycleState()
    {
        IDictionary active = new Hashtable { [7] = 5, [74] = 1, [740] = 1, [710] = 2 };
        IDictionary complete = new Hashtable { [8] = 4, [84] = 1, [841] = 1, [820] = 4 };

        Assert.True(FourberieSchemeAuthority.TryAbort(active, 7, out var abortFailure), abortFailure);
        Assert.Empty(active);
        Assert.True(FourberieSchemeAuthority.TryClearCompleted(complete, 8, out var clearFailure), clearFailure);
        Assert.Empty(complete);
        Assert.False(FourberieSchemeAuthority.TryAbort(complete, 8, out _));
    }

    [Fact]
    public void ClientLifecycleIntent_FollowsOnlyTheCanonicalSlotMarker()
    {
        Assert.Equal(FourberieOperation.StartScheme,
            FourberieAuthorityPatches.SchemeLifecycleOperation(new Hashtable(), 7));
        Assert.Equal(FourberieOperation.AbortScheme,
            FourberieAuthorityPatches.SchemeLifecycleOperation(new Hashtable { [740] = 4 }, 7));
        Assert.Equal(FourberieOperation.ClearCompletedScheme,
            FourberieAuthorityPatches.SchemeLifecycleOperation(new Hashtable { [841] = 1 }, 8));
    }

    [Fact]
    public void StanceChange_AbortsBothSlotsAndKeepsUnrelatedState()
    {
        IDictionary crime = new Hashtable
        {
            [500] = 1,
            [7] = 3,
            [74] = 1,
            [740] = 5,
            [8] = 7,
            [84] = 1,
            [841] = 1,
            [310] = 4,
        };

        Assert.True(FourberieSchemeAuthority.TryChangeStance(
            crime, stance: 2, out var changed, out var failure), failure);
        Assert.True(changed);
        Assert.Equal(2, crime[500]);
        Assert.Equal(4, crime[310]);
        Assert.False(crime.Contains(7));
        Assert.False(crime.Contains(8));
        Assert.False(crime.Contains(740));
        Assert.False(crime.Contains(841));

        Assert.True(FourberieSchemeAuthority.TryChangeStance(
            crime, stance: 2, out changed, out failure), failure);
        Assert.False(changed);
        Assert.False(FourberieSchemeAuthority.TryChangeStance(
            crime, stance: 3, out _, out _));
    }

    [Fact]
    public void ClientFilterSnapshot_RestoresCanonicalSlotsButKeepsPresentationNavigation()
    {
        IDictionary crime = new Hashtable
        {
            [7] = 3,
            [74] = 1,
            [741] = 1,
            [750] = 2,
            [310] = 4,
        };
        IDictionary heroes = new Hashtable
        {
            ["victim7"] = "hero_old",
            ["paymaster"] = "hero_paymaster",
        };
        IDictionary times = new Hashtable { [7] = "time_old" };
        FourberieSchemeSelectionSnapshot snapshot = FourberieSchemeAuthority.CaptureSelection(
            crime, heroes, times);

        crime.Remove(7);
        crime.Remove(750);
        crime[8] = 6;
        crime[310] = 3;
        heroes.Remove("victim7");
        heroes["victim8"] = "hero_transient";
        heroes["paymaster"] = "hero_new";
        times.Remove(7);
        times[8] = "time_transient";
        snapshot.Restore(crime, heroes, times);

        Assert.Equal(3, crime[7]);
        Assert.Equal(2, crime[750]);
        Assert.False(crime.Contains(8));
        Assert.Equal(3, crime[310]);
        Assert.Equal("hero_old", heroes["victim7"]);
        Assert.False(heroes.Contains("victim8"));
        Assert.Equal("hero_new", heroes["paymaster"]);
        Assert.Equal("time_old", times[7]);
        Assert.False(times.Contains(8));
    }

    [Theory]
    [InlineData((int)FourberieOperation.SelectSchemeVictim, "hero_a", 7, true)]
    [InlineData((int)FourberieOperation.SelectSchemeVictim, "", 7, false)]
    [InlineData((int)FourberieOperation.SelectSchemeType, "", 78, true)]
    [InlineData((int)FourberieOperation.SelectSchemeType, "", 79, false)]
    [InlineData((int)FourberieOperation.StartScheme, "", 8, true)]
    [InlineData((int)FourberieOperation.AbortScheme, "", 7, true)]
    [InlineData((int)FourberieOperation.ClearCompletedScheme, "", 9, false)]
    [InlineData((int)FourberieOperation.ChangeSchemeStance, "", 2, true)]
    [InlineData((int)FourberieOperation.ChangeSchemeStance, "", 3, false)]
    public void Protocol_AcceptsOnlyExactSchemeCommandShapes(
        int operation,
        string targetId,
        int value,
        bool expected)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            string.Empty, targetId, value, Array.Empty<FourberieTroopSelection>());

        Assert.Equal(expected, FourberieOperationProtocol.IsRequestShapeValid(request));
    }
}
