using GameInterface.Services.Separatism;
using Xunit;

namespace GameInterface.Tests.Services.Separatism;

public sealed class SeparatismCompatibilityPolicyTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void SettlementRebellion_IsEnabledOnlyByTheAuthoritativeServerOption(
        bool localIsServer,
        bool settlementRebellionsEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            SeparatismCompatibilityPolicy.AllowConfiguredSettlementRebellion(
                localIsServer,
                settlementRebellionsEnabled));
    }

    [Theory]
    [InlineData(20, 20, false)]
    [InlineData(21, 20, true)]
    [InlineData(-20, -20, false)]
    public void FriendThreshold_IsStrictlyGreaterThanConfiguredRelation(
        int relation,
        int threshold,
        bool expected)
    {
        Assert.Equal(expected, SeparatismCompatibilityPolicy.IsFriend(relation, threshold));
    }

    [Theory]
    [InlineData(-20, -20, false)]
    [InlineData(-21, -20, true)]
    [InlineData(20, 20, false)]
    public void EnemyThreshold_IsStrictlyLessThanConfiguredRelation(
        int relation,
        int threshold,
        bool expected)
    {
        Assert.Equal(expected, SeparatismCompatibilityPolicy.IsEnemy(relation, threshold));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ClanJoin_IsSuppressedOnlyForAnEnabledEnemyLeaderPair(
        bool enabled,
        bool leadersExist,
        bool leadersAreEnemies,
        bool expected)
    {
        Assert.Equal(
            expected,
            SeparatismCompatibilityPolicy.AllowClanJoin(
                enabled,
                leadersExist,
                leadersAreEnemies));
    }

    [Theory]
    [InlineData(false, true, true, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, true, false, true, true, true)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(true, true, true, false, true, false)]
    [InlineData(true, true, true, false, false, true)]
    public void ClanLeave_UsesTheRecoveredRulerFiefAndRelationRules(
        bool enabled,
        bool hasKingdom,
        bool leaderExists,
        bool leaderIsRuler,
        bool hasSettlements,
        bool expected)
    {
        Assert.Equal(
            expected,
            SeparatismCompatibilityPolicy.AllowClanLeave(
                enabled,
                hasKingdom,
                leaderExists,
                leaderIsRuler,
                hasSettlements,
                hasGoodRulerRelation: true));
    }

    [Fact]
    public void Defection_BlocksRulersSameKingdomAndHostileOrDistantDestinations()
    {
        Assert.False(SeparatismCompatibilityPolicy.AllowDefection(
            enabled: true,
            hasCurrentKingdom: true,
            leadersExist: true,
            leaderIsRuler: true,
            sameKingdom: false,
            hasSettlements: false,
            hasGoodRulerRelation: false,
            enemyOfDestination: false,
            destinationIsClose: true,
            currentKingdomHasSettlements: true,
            currentNonMercenaryClanCount: 3));
        Assert.False(SeparatismCompatibilityPolicy.AllowDefection(
            true, true, true, false, true, false, false, false, true, true, 3));
        Assert.False(SeparatismCompatibilityPolicy.AllowDefection(
            true, true, true, false, false, true, false, true, true, true, 3));
        Assert.False(SeparatismCompatibilityPolicy.AllowDefection(
            true, true, true, false, false, true, false, false, false, true, 3));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    public void Defection_FinalRule_AllowsWeakOrHostileSourceKingdoms(
        bool goodRulerRelation,
        bool currentKingdomHasSettlements,
        bool enoughRemainingClans,
        bool expected)
    {
        Assert.Equal(expected, SeparatismCompatibilityPolicy.AllowDefection(
            enabled: true,
            hasCurrentKingdom: true,
            leadersExist: true,
            leaderIsRuler: false,
            sameKingdom: false,
            hasSettlements: false,
            hasGoodRulerRelation: goodRulerRelation,
            enemyOfDestination: false,
            destinationIsClose: true,
            currentKingdomHasSettlements: currentKingdomHasSettlements,
            currentNonMercenaryClanCount: enoughRemainingClans ? 3 : 2));
    }
}
