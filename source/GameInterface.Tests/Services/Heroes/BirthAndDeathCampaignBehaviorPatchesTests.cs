using Common;
using Common.Util;
using GameInterface.Services.Actions.Patches;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes;
using GameInterface.Services.Heroes.Patches.Disable;
using GameInterface.Services.Players;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using Xunit;

namespace GameInterface.Tests.Services.Heroes;

[Collection(ModInformationRoleCollection.Name)]
public class BirthAndDeathCampaignBehaviorPatchesTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly ConditionalWeakTable<object, ControlledObjectInfo> playerObjects =
        (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools
            .Field(typeof(PlayerManager), "PlayerObjects")
            .GetValue(null)!;

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AgingEvents_RunOnlyOnAuthoritativeServer(bool isServer, bool expected)
    {
        ModInformation.IsServer = isServer;

        Assert.Equal(expected, DisableAgingCampaignBehavior.RegisterEventsPrefix());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PregnancyEvents_RunOnlyOnAuthoritativeServer(bool isServer, bool expected)
    {
        ModInformation.IsServer = isServer;

        Assert.Equal(expected, DisablePregnancyCampaignBehavior.RegisterEventsPrefix());
    }

    [Fact]
    public void AgingDailyTick_ServerAllowsNonPlayerHero()
    {
        ModInformation.IsServer = true;
        var hero = ObjectHelper.SkipConstructor<Hero>();

        Assert.True(DisableAgingCampaignBehavior.DailyTickHeroPrefix(hero));
    }

    [Fact]
    public void AgingDailyTick_ServerProtectsPlayerHeroFromSinglePlayerDeathFlow()
    {
        ModInformation.IsServer = true;
        var hero = ObjectHelper.SkipConstructor<Hero>();
        playerObjects.Add(hero, new ControlledObjectInfo("PlayerOne", new ControllerIdProvider()));

        try
        {
            Assert.False(DisableAgingCampaignBehavior.DailyTickHeroPrefix(hero));
        }
        finally
        {
            playerObjects.Remove(hero);
        }
    }

    [Fact]
    public void AgingDailyTick_ClientNeverRunsLifecycle()
    {
        ModInformation.IsServer = false;

        Assert.False(DisableAgingCampaignBehavior.DailyTickHeroPrefix(ObjectHelper.SkipConstructor<Hero>()));
    }

    [Fact]
    public void KillCharacter_ClientNeverAppliesDeaths()
    {
        ModInformation.IsServer = false;
        KillCharacterActionPatches.SuccessionContext state = null;

        Assert.False(KillCharacterActionPatches.Prefix(
            ObjectHelper.SkipConstructor<Hero>(),
            KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge,
            ref state));
        Assert.Null(state);
    }

    [Fact]
    public void KillCharacter_ServerAppliesNpcDeath()
    {
        ModInformation.IsServer = true;
        KillCharacterActionPatches.SuccessionContext state = null;

        Assert.True(KillCharacterActionPatches.Prefix(
            ObjectHelper.SkipConstructor<Hero>(),
            KillCharacterAction.KillCharacterActionDetail.DiedInLabor,
            ref state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData(KillCharacterAction.KillCharacterActionDetail.DiedInLabor)]
    [InlineData(KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge)]
    [InlineData(KillCharacterAction.KillCharacterActionDetail.Murdered)]
    [InlineData(KillCharacterAction.KillCharacterActionDetail.Lost)]
    public void KillCharacter_ServerBlocksLethalPathsForPlayerHeroWithoutSuccessor(
        KillCharacterAction.KillCharacterActionDetail detail)
    {
        ModInformation.IsServer = true;
        // A bare hero has no children and no clan, so the bloodline gate must hold the death.
        var hero = ObjectHelper.SkipConstructor<Hero>();
        playerObjects.Add(hero, new ControlledObjectInfo("PlayerOne", new ControllerIdProvider()));
        KillCharacterActionPatches.SuccessionContext state = null;

        try
        {
            Assert.False(KillCharacterActionPatches.Prefix(hero, detail, ref state));
            Assert.Null(state);
        }
        finally
        {
            playerObjects.Remove(hero);
        }
    }

    [Fact]
    public void Succession_LivingChildGate()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        Assert.False(PlayerSuccessionRules.HasLivingChild(hero));

        var deadChild = ObjectHelper.SkipConstructor<Hero>();
        var aliveChild = ObjectHelper.SkipConstructor<Hero>();
        AccessTools.Field(typeof(Hero), nameof(Hero._heroState)).SetValue(deadChild, Hero.CharacterStates.Dead);
        AccessTools.Field(typeof(Hero), nameof(Hero._heroState)).SetValue(aliveChild, Hero.CharacterStates.Active);
        AccessTools.Field(typeof(Hero), nameof(Hero._children))
            .SetValue(hero, new TaleWorlds.Library.MBList<Hero> { deadChild });
        Assert.False(PlayerSuccessionRules.HasLivingChild(hero));

        AccessTools.Field(typeof(Hero), nameof(Hero._children))
            .SetValue(hero, new TaleWorlds.Library.MBList<Hero> { deadChild, aliveChild });
        Assert.True(PlayerSuccessionRules.HasLivingChild(hero));
    }
}
