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

    [Fact]
    public void ProposalTick_CountsDownBeforeBecomingReady()
    {
        IDictionary crime = new Hashtable { [203] = 0, [204] = 2 };

        Assert.False(FourberieContractAuthority.AdvanceProposalCooldown(crime));
        Assert.Equal(1, crime[204]);
        Assert.False(FourberieContractAuthority.AdvanceProposalCooldown(crime));
        Assert.Equal(0, crime[204]);
        Assert.True(FourberieContractAuthority.AdvanceProposalCooldown(crime));

        crime[200] = 1;
        Assert.False(FourberieContractAuthority.AdvanceProposalCooldown(crime));
    }

    [Theory]
    [InlineData(0, 70_000, 1_345, 73_345)]
    [InlineData(1, 70_000, 7_788, 59_788)]
    public void Proposal_UsesPinnedServerRewardFormula(int type, int giverGold, int random, int expected)
    {
        Assert.True(FourberieContractAuthority.TryReward(
            type, giverGold, random, out int reward, out var failure), failure);
        Assert.Equal(expected, reward);
    }

    [Fact]
    public void ProposalCommit_AndAcceptPreserveCompleteCanonicalContract()
    {
        IDictionary crime = new Hashtable { [203] = 0, [204] = 0, [310] = 4 };
        IDictionary heroes = new Hashtable { ["enforcer"] = "hero_enforcer" };

        Assert.True(FourberieContractAuthority.TryCommitProposal(
            crime, heroes, "hero_giver", "hero_target", type: 1, reward: 90_000, out var failure), failure);
        Assert.Equal(1, crime[200]);
        Assert.Equal(90_000, crime[201]);
        Assert.Equal(0, crime[202]);
        Assert.Equal("hero_giver", heroes["contractGiver"]);
        Assert.Equal("hero_target", heroes["contractTarget"]);

        Assert.True(FourberieContractAuthority.CanRespondToProposal(crime, heroes, out failure), failure);
        FourberieContractAuthority.CommitAccept(crime, cooldown: 8);

        Assert.False(crime.Contains(202));
        Assert.Equal(8, crime[204]);
        Assert.Equal(1, crime[200]);
        Assert.Equal(90_000, crime[201]);
        Assert.Equal(4, crime[310]);
        Assert.Equal("hero_giver", heroes["contractGiver"]);
        Assert.Equal("hero_target", heroes["contractTarget"]);
    }

    [Fact]
    public void ProposalDecline_ClearsProposalAndContractButKeepsOfferPreference()
    {
        IDictionary crime = new Hashtable { [200] = 0, [201] = 80_000, [202] = 0, [203] = 0 };
        IDictionary heroes = new Hashtable
        {
            ["contractGiver"] = "hero_giver",
            ["contractTarget"] = "hero_target",
        };

        FourberieContractAuthority.CommitDecline(crime, heroes, cooldown: 11);

        Assert.False(crime.Contains(200));
        Assert.False(crime.Contains(201));
        Assert.False(crime.Contains(202));
        Assert.Equal(0, crime[203]);
        Assert.Equal(11, crime[204]);
        Assert.False(heroes.Contains("contractGiver"));
        Assert.False(heroes.Contains("contractTarget"));
    }

    [Theory]
    [InlineData((int)FourberieOperation.EnableContractOffers, true)]
    [InlineData((int)FourberieOperation.DisableContractOffers, true)]
    [InlineData((int)FourberieOperation.AbortContract, true)]
    [InlineData((int)FourberieOperation.AcceptContractProposal, true)]
    [InlineData((int)FourberieOperation.DeclineContractProposal, true)]
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

    [Theory]
    [InlineData("Fourberie.FourbContractBehavior", "HourlyTick", "ContractTickReplacement")]
    [InlineData("Fourberie.FourbContractBehavior+<>c", "<AddGameMenus>b__5_0", "ContractProposalLegacyConsequence")]
    [InlineData("Fourberie.FourbContractBehavior+<>c", "<AddGameMenus>b__5_1", "ContractProposalLegacyConsequence")]
    [InlineData("Fourberie.FourbContractBehavior", "ContractComplete", "ServerOnly")]
    [InlineData("Fourberie.FourbContractBehavior", "fb_contract_hint", "ClientPresentation")]
    public void ProposalLifecycle_HasExplicitCoopOwners(string type, string method, string kind)
    {
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.TypeName == type && spec.MethodName == method && spec.Kind.ToString() == kind);
    }

    [Fact]
    public void ProposalMessage_RequiresPinnedServerFields()
    {
        var valid = new NetworkFourberieContractProposal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 3, "hero_giver", "hero_target", 1, 90_000);

        Assert.True(FourberieOperationProtocol.IsProposalShapeValid(valid));
        Assert.False(FourberieOperationProtocol.IsProposalShapeValid(new NetworkFourberieContractProposal(
            valid.SessionId, valid.Revision, valid.GiverId, valid.GiverId, valid.ContractType, valid.Reward)));
        Assert.False(FourberieOperationProtocol.IsProposalShapeValid(new NetworkFourberieContractProposal(
            valid.SessionId, valid.Revision, valid.GiverId, valid.TargetId, 2, valid.Reward)));
    }
}
