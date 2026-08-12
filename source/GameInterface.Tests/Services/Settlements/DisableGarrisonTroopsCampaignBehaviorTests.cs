using Common.Util;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Patches.Disable;
using HarmonyLib;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.Settlements;

public class DisableGarrisonTroopsCampaignBehaviorTests
{
    [Fact]
    public void OnSettlementEntered_PlayerOwnedSettlement_BlocksAiGarrisonManagement()
    {
        var ownerClan = ObjectHelper.SkipConstructor<Clan>();
        var town = ObjectHelper.SkipConstructor<Town>();
        town._ownerClan = ownerClan;
        var settlement = ObjectHelper.SkipConstructor<Settlement>();
        settlement.Town = town;
        var aiParty = ObjectHelper.SkipConstructor<MobileParty>();

        var playerObjects = (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools
            .Field(typeof(PlayerManager), "PlayerObjects")
            .GetValue(null)!;
        playerObjects.Add(ownerClan, new ControlledObjectInfo("TestPlayer", null!));

        try
        {
            Assert.False(DisableGarrisonTroopsCampaignBehavior.OnSettlementEnteredPrefix(aiParty, settlement));
        }
        finally
        {
            playerObjects.Remove(ownerClan);
        }
    }
}
