using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.Clans.Patches;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using HarmonyLib;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.Clans;

[Collection(ModInformationRoleCollection.Name)]
public sealed class BattlePartyExpensePatchesTests : IDisposable
{
    private readonly ModOptions previousOptions = ModConfigProvider.ModOptions;
    private readonly ConditionalWeakTable<object, ControlledObjectInfo> playerObjects =
        (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools
            .Field(typeof(PlayerManager), "PlayerObjects")
            .GetValue(null)!;
    private Hero? registeredHero;

    public BattlePartyExpensePatchesTests()
    {
        ModConfigProvider.ModOptions = new ModOptions(new ModOptionsData
        {
            GoldFoodInfluenceChangeInBattles = GoldFoodChangeMode.Disabled,
        });
    }

    public void Dispose()
    {
        if (registeredHero != null) playerObjects.Remove(registeredHero);
        ModConfigProvider.ModOptions = previousOptions;
    }

    [Fact]
    public void InfluenceChange_BattlingClanLeader_ContinuesNormally()
    {
        MobileParty party = CreateParty(inBattle: true);
        registeredHero = ObjectHelper.SkipConstructor<Hero>();
        registeredHero.PartyBelongedTo = party;
        playerObjects.Add(
            registeredHero,
            new ControlledObjectInfo("PlayerOne", new ControllerIdProvider()));

        var clan = ObjectHelper.SkipConstructor<Clan>();
        clan._leader = registeredHero;
        var influenceChange = new ExplainedNumber();

        bool runOriginal = DefaultClanPoliticsModelPatches.CalculateInfluenceChangeInternalPrefix(
            clan,
            ref influenceChange);

        Assert.True(runOriginal);
    }

    [Fact]
    public void AddPartyExpense_BattlingParty_DoesNotChargeItsWages()
    {
        MobileParty party = CreateParty(inBattle: true);
        int result = -1;

        bool runOriginal = DefaultClanFinanceModelPatches.AddPartyExpensePrefix(
            new DefaultClanFinanceModel(),
            ref result,
            party,
            ObjectHelper.SkipConstructor<Clan>(),
            new ExplainedNumber(),
            applyWithdrawals: true);

        Assert.False(runOriginal);
        Assert.Equal(0, result);
    }

    [Fact]
    public void AddPartyExpense_OtherClanParty_IsNotSkipped()
    {
        MobileParty party = CreateParty(inBattle: false);
        MethodInfo policy = AccessTools.Method(
            typeof(DefaultClanFinanceModelPatches),
            "ShouldSkipPartyExpense");

        Assert.NotNull(policy);
        Assert.False((bool)policy.Invoke(null, new object[] { party })!);
    }

    [Fact]
    public void AddLeaderPartyExpense_BattlingLeaderParty_DoesNotChargeItsWages()
    {
        MobileParty party = CreateParty(inBattle: true);
        var leader = ObjectHelper.SkipConstructor<Hero>();
        var clan = ObjectHelper.SkipConstructor<Clan>();
        leader.PartyBelongedTo = party;
        clan._leader = leader;

        object[] arguments =
        {
            new DefaultClanFinanceModel(),
            clan,
            new ExplainedNumber(),
            true,
            -1,
        };

        MethodInfo prefix = AccessTools.Method(
            typeof(DefaultClanFinanceModelPatches),
            "AddExpenseFromLeaderPartyPrefix");
        bool runOriginal = (bool)prefix.Invoke(null, arguments)!;

        Assert.False(runOriginal);
        Assert.Equal(0, (int)arguments[4]);
    }

    private static MobileParty CreateParty(bool inBattle)
    {
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var partyBase = ObjectHelper.SkipConstructor<PartyBase>();
        partyBase.MobileParty = party;
        AccessTools.Field(typeof(MobileParty), "<Party>k__BackingField").SetValue(party, partyBase);

        if (inBattle)
        {
            var side = ObjectHelper.SkipConstructor<MapEventSide>();
            AccessTools.Field(typeof(MapEventSide), "_mapEvent")
                .SetValue(side, ObjectHelper.SkipConstructor<MapEvent>());
            partyBase._mapEventSide = side;
        }

        return party;
    }
}
