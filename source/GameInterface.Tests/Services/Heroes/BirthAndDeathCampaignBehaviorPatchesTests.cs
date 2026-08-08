using Common;
using Common.Util;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Patches.Disable;
using GameInterface.Services.Players;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
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
}
