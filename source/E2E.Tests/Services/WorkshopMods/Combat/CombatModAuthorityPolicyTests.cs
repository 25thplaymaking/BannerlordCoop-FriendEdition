using Missions.WorkshopMods.Combat;
using System;
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
            blockedWithShield: false));
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
            blockedWithShield: false));

        // Idempotent when another compatible calculation already marked it.
        Assert.True(UnblockableThrustAuthorityPatch.ResolveCrushThrough(
            moduleCompatible: true,
            isCoopBattleActive: true,
            sourceIsLocallyControlled: true,
            alreadyCrushedThrough: true,
            strikeType: 1,
            collisionResult: 3,
            blockedWithShield: false));
    }

    [Fact]
    public void CampaignMutation_IsServerOnly()
    {
        Assert.False(CombatModAuthorityPolicy.AllowCampaignMutation(isServer: false));
        Assert.True(CombatModAuthorityPolicy.AllowCampaignMutation(isServer: true));
    }

    [Fact]
    public void DismembermentPresentation_IsBlockedInLiveCoopUntilItCanConverge()
    {
        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: false,
            isCoopBattleActive: true));
        Assert.False(CombatModAuthorityPolicy.AllowDismembermentPresentation(
            moduleCompatible: true,
            isServer: true,
            isCoopBattleActive: true));
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
