using Missions.WorkshopMods.Combat;
using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.WorkshopMods.Combat;

public sealed class CombatModAuthorityPolicyTests
{
    [Fact]
    public void CoopClient_CannotAuthorDamageForRemoteAttacker()
    {
        Assert.False(CombatModAuthorityPolicy.AllowMissionGameplayDecision(
            isCoopBattleActive: true,
            sourceIsLocallyControlled: false));
    }

    [Fact]
    public void CoopCollisionAuthority_CanAuthorOwnedAttack()
    {
        Assert.True(CombatModAuthorityPolicy.AllowMissionGameplayDecision(
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true));
    }

    [Fact]
    public void UnblockableThrust_RemoteClientCannotChangeCollisionDecision()
    {
        Assert.False(UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible: true,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: false,
            alreadyCrushedThrough: false,
            strikeType: 1,
            collisionResult: 3,
            blockedWithShield: false,
            attackerIsMounted: false));
    }

    [Fact]
    public void UnblockableThrust_LocalCollisionAuthorityAppliesExactlyOneDecision()
    {
        Assert.True(UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible: true,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true,
            alreadyCrushedThrough: false,
            strikeType: 1,
            collisionResult: 3,
            blockedWithShield: false,
            attackerIsMounted: false));

        // Idempotent when another compatible calculation already marked it.
        Assert.True(UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible: true,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true,
            alreadyCrushedThrough: true,
            strikeType: 1,
            collisionResult: 3,
            blockedWithShield: false,
            attackerIsMounted: false));
    }

    [Theory]
    [InlineData(false, (int)StrikeType.Thrust, CombatCollisionResult.Blocked)]
    [InlineData(true, (int)StrikeType.Swing, CombatCollisionResult.Blocked)]
    [InlineData(true, (int)StrikeType.Thrust, CombatCollisionResult.StrikeAgent)]
    public void UnblockableThrust_LeavesUnauditedOrInapplicableCollisionsUnchanged(
        bool moduleCompatible,
        int strikeType,
        CombatCollisionResult collisionResult)
    {
        Assert.False(UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true,
            alreadyCrushedThrough: false,
            strikeType,
            collisionResult: (int)collisionResult,
            blockedWithShield: false,
            attackerIsMounted: false));
    }

    [Theory]
    [InlineData(false, CombatCollisionResult.Blocked, false, true)]
    [InlineData(true, CombatCollisionResult.Blocked, false, true)]
    [InlineData(false, CombatCollisionResult.Blocked, true, false)]
    [InlineData(true, CombatCollisionResult.Blocked, true, false)]
    [InlineData(false, CombatCollisionResult.Parried, false, false)]
    [InlineData(true, CombatCollisionResult.Parried, false, false)]
    [InlineData(false, CombatCollisionResult.ChamberBlocked, false, false)]
    [InlineData(true, CombatCollisionResult.ChamberBlocked, false, false)]
    public void UnblockableThrust_AuditedDefaultsCoverFootMountedShieldParryAndChamber(
        bool attackerIsMounted,
        CombatCollisionResult collisionResult,
        bool blockedWithShield,
        bool expected)
    {
        Assert.Equal(expected, UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible: true,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true,
            alreadyCrushedThrough: false,
            strikeType: (int)StrikeType.Thrust,
            collisionResult: (int)collisionResult,
            blockedWithShield,
            attackerIsMounted));
    }

    [Fact]
    public void CampaignMutation_IsServerOnly()
    {
        Assert.False(CombatModAuthorityPolicy.AllowCampaignMutation(isServer: false));
        Assert.True(CombatModAuthorityPolicy.AllowCampaignMutation(isServer: true));
    }

    [Fact]
    public void DismembermentPresentation_InitializesOnEveryCompatibleClient()
    {
        Assert.True(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: false,
            isCoopBattleActive: true));
        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: true,
            isCoopBattleActive: true));

        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: false,
            isServer: false,
            isCoopBattleActive: true));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void DismembermentAcceptedBlow_RequiresPublishedRouteAndVictimAuthority(
        bool routeEnabled,
        bool victimIsLocallyAuthoritative,
        bool expected)
    {
        Assert.Equal(expected, CombatModAuthorityPolicy.AllowDismembermentAcceptedBlow(
            moduleCompatible: true,
            isServer: false,
            isCoopBattleActive: true,
            routeEnabled,
            victimIsLocallyAuthoritative));
    }

    [Fact]
    public void DismembermentPresentation_RemainsClientOnlyOutsideCoop()
    {
        Assert.True(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: false,
            isCoopBattleActive: false));
        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: true,
            isCoopBattleActive: false));
        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: false,
            isServer: false,
            isCoopBattleActive: false));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void DismembermentCapability_RequiresReadyGuardCompatibleBinaryAndEnabledOption(
        bool guardInitialized,
        bool moduleCompatible,
        bool moduleEnabled,
        bool expected)
    {
        Assert.Equal(expected, CombatModAuthorityPolicy.AllowDismembermentCapability(
            guardInitialized,
            moduleCompatible,
            moduleEnabled));
    }

    [Fact]
    public void RbmMissionBehaviors_AreDeniedOnCoopClientAndAllowedOnServer()
    {
        Assert.False(CombatModAuthorityPolicy.AllowRbmMissionBehaviorInitialization(
            moduleCompatible: true,
            expectedMethodShape: true,
            isServer: false,
            isCoopBattleActive: true));
        Assert.True(CombatModAuthorityPolicy.AllowRbmMissionBehaviorInitialization(
            moduleCompatible: true,
            expectedMethodShape: true,
            isServer: true,
            isCoopBattleActive: true));
    }

    [Fact]
    public void RbmGameInitializationFinished_IsAlwaysServerOnly()
    {
        Assert.False(CombatModAuthorityPolicy.AllowRbmGameInitializationFinished(
            moduleCompatible: true,
            expectedMethodShape: true,
            isServer: false));
        Assert.True(CombatModAuthorityPolicy.AllowRbmGameInitializationFinished(
            moduleCompatible: true,
            expectedMethodShape: true,
            isServer: true));
    }

    [Fact]
    public void RbmLifecycle_RejectsUnknownBinaryOrMethodShape()
    {
        Assert.False(CombatModAuthorityPolicy.AllowRbmMissionBehaviorInitialization(
            moduleCompatible: false,
            expectedMethodShape: true,
            isServer: true,
            isCoopBattleActive: true));
        Assert.False(CombatModAuthorityPolicy.AllowRbmGameInitializationFinished(
            moduleCompatible: true,
            expectedMethodShape: false,
            isServer: true));
    }

    [Fact]
    public void MissingOptionalType_FailsClosedWithoutThrowing()
    {
        Exception exception = Record.Exception(() =>
        {
            Assert.Null(CombatModCompatibilityGuard.ResolveOptionalMethod(
                "Missing.Workshop.Combat.Type",
                "MissingMethod"));
        });

        Assert.Null(exception);
    }
}
