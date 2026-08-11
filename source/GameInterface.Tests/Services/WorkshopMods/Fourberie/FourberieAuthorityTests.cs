using Common;
using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieAuthorityTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private enum EventDetail
    {
        Default = 0,
        Forced = 1,
    }

    private sealed class StableArgument
    {
        public StableArgument(string stringId) => StringId = stringId;
        public string StringId { get; }
    }

    private sealed class FirstBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents() { }
        public override void SyncData(IDataStore dataStore) { }
    }

    private sealed class SecondBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents() { }
        public override void SyncData(IDataStore dataStore) { }
    }

    private sealed class CaptureRuntime : IFourberiePatchRuntime
    {
        public FourberieLocalOperation LastOperation { get; private set; }
        public int SubmissionCount { get; private set; }

        public void PublishIfChanged()
        {
        }

        public bool TrySubmit(FourberieLocalOperation operation)
        {
            LastOperation = operation;
            SubmissionCount++;
            return true;
        }

        public void RunContractTick()
        {
        }
    }

    [Fact]
    public void TickLedger_BlocksDuplicateAndOlderExecution_ButSeparatesCampaignAndSubject()
    {
        var ledger = new FourberieTickLedger();

        Assert.True(ledger.TryEnter("campaign-a", "DailyTick", "town-a", 100));
        Assert.False(ledger.TryEnter("campaign-a", "DailyTick", "town-a", 100));
        Assert.False(ledger.TryEnter("campaign-a", "DailyTick", "town-a", 99));
        Assert.True(ledger.TryEnter("campaign-a", "DailyTick", "town-a", 101));
        Assert.True(ledger.TryEnter("campaign-a", "DailyTick", "town-b", 100));
        Assert.True(ledger.TryEnter("campaign-b", "DailyTick", "town-a", 100));
    }

    [Fact]
    public void RevisionGate_IsIdempotentAndRejectsStaleOrConflictingSnapshots()
    {
        var gate = new FourberieRevisionGate();

        Assert.Equal(FourberieRevisionDecision.Apply, gate.Evaluate(0, HashA));
        Assert.True(gate.Commit(0, HashA));
        Assert.Equal(FourberieRevisionDecision.AlreadyApplied, gate.Evaluate(0, HashA));
        Assert.Equal(FourberieRevisionDecision.Conflict, gate.Evaluate(0, HashB));
        Assert.Equal(FourberieRevisionDecision.Invalid, gate.Evaluate(-1, HashA));
        Assert.Equal(FourberieRevisionDecision.Apply, gate.Evaluate(1, HashB));
        Assert.True(gate.Commit(1, HashB));
        Assert.Equal(FourberieRevisionDecision.Stale, gate.Evaluate(0, HashA));
        Assert.Equal(1, gate.Revision);
    }

    [Fact]
    public void RevisionGate_DoesNotAdvanceUntilCommit()
    {
        var gate = new FourberieRevisionGate();

        Assert.Equal(FourberieRevisionDecision.Apply, gate.Evaluate(7, HashA));
        Assert.Equal(-1, gate.Revision);
        Assert.Equal(FourberieRevisionDecision.Apply, gate.Evaluate(7, HashA));
    }

    [Fact]
    public void SubjectKey_UsesAllStableAndPrimitiveArgumentsWithoutLosingDuplicateIdentity()
    {
        var first = FourberieAuthorityPatches.SubjectKey(new object[]
        {
            new StableArgument("faction-a"),
            new StableArgument("faction-b"),
            EventDetail.Forced,
            true,
        });
        var duplicate = FourberieAuthorityPatches.SubjectKey(new object[]
        {
            new StableArgument("faction-a"),
            new StableArgument("faction-b"),
            EventDetail.Forced,
            true,
        });
        var differentOpponent = FourberieAuthorityPatches.SubjectKey(new object[]
        {
            new StableArgument("faction-a"),
            new StableArgument("faction-c"),
            EventDetail.Forced,
            true,
        });

        Assert.Equal(first, duplicate);
        Assert.NotEqual(first, differentOpponent);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void OperationProtocol_AcceptsBoundedStableSelectionsAndRejectsMalformedShapes()
    {
        var valid = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            7,
            3,
            FourberieOperation.EnlistAgentsFromParty,
            "town_a",
            string.Empty,
            0,
            new[] { new FourberieTroopSelection("troop_a", 2) });

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(valid));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            valid.SessionId,
            valid.RequestId,
            valid.ExpectedRevision,
            valid.Operation,
            valid.SettlementId,
            valid.TargetId,
            valid.IntValue,
            new[] { new FourberieTroopSelection("troop_a", -1) })));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            valid.SessionId,
            valid.RequestId,
            valid.ExpectedRevision,
            valid.Operation,
            valid.SettlementId,
            valid.TargetId,
            valid.IntValue,
            new FourberieTroopSelection[FourberieOperationProtocol.MaxTroopSelections + 1])));
    }

    [Theory]
    [InlineData((int)FourberieOperation.StartCriminalBusiness, 11, true)]
    [InlineData((int)FourberieOperation.StartCriminalBusiness, 12, false)]
    [InlineData((int)FourberieOperation.UpgradeCriminalBusiness, 32, true)]
    [InlineData((int)FourberieOperation.UpgradeCriminalBusiness, 33, false)]
    [InlineData((int)FourberieOperation.DowngradeCriminalBusiness, 21, true)]
    [InlineData((int)FourberieOperation.UpgradeSchemeBonus, 7, true)]
    [InlineData((int)FourberieOperation.DowngradeSchemeBonus, 8, true)]
    [InlineData((int)FourberieOperation.ResetSchemeBonus, 7, true)]
    [InlineData((int)FourberieOperation.ResetSchemeBonus, 9, false)]
    [InlineData((int)FourberieOperation.CreateAgentParty, 0, true)]
    [InlineData((int)FourberieOperation.RefillAgentParty, 1, false)]
    [InlineData((int)FourberieOperation.ResetCrimeBaseParty, 0, true)]
    public void OperationProtocol_RequiresExactCriminalBusinessShapes(
        int operationValue,
        int businessKey,
        bool expected)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            7,
            3,
            (FourberieOperation)operationValue,
            string.Empty,
            string.Empty,
            businessKey,
            Array.Empty<FourberieTroopSelection>());

        Assert.Equal(expected, FourberieOperationProtocol.IsRequestShapeValid(request));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            request.SessionId,
            request.RequestId,
            request.ExpectedRevision,
            request.Operation,
            "town_a",
            request.TargetId,
            request.IntValue,
            request.Troops)));
    }

    [Theory]
    [InlineData(11, 1, 0)]
    [InlineData(21, 2, 10_000)]
    [InlineData(31, 3, 15_000)]
    public void EnterpriseStart_UsesPinnedMarkerStateAndServerCost(int businessKey, int markerKey, int cost)
    {
        IDictionary state = new Hashtable { [60] = 4 };

        Assert.True(FourberieEnterpriseAuthority.TryStart(
            state, businessKey, cost, out var charged, out var failure), failure);

        Assert.Equal(cost, charged);
        Assert.Equal(0, state[markerKey]);
        Assert.Equal(1, state[businessKey]);
        Assert.Equal(5, state[60]);
    }

    [Fact]
    public void EnterpriseStart_RejectsDuplicateOrUnaffordableRequestsWithoutMutation()
    {
        IDictionary duplicate = new Hashtable { [60] = 4, [2] = 0, [21] = 1 };
        IDictionary unaffordable = new Hashtable { [60] = 4 };

        Assert.False(FourberieEnterpriseAuthority.TryStart(
            duplicate, 21, 20_000, out _, out _));
        Assert.False(FourberieEnterpriseAuthority.TryStart(
            unaffordable, 31, 14_999, out _, out _));

        Assert.Equal(4, duplicate[60]);
        Assert.Equal(1, duplicate[21]);
        Assert.Equal(4, unaffordable[60]);
        Assert.False(unaffordable.Contains(31));
    }

    [Theory]
    [InlineData(11, 5, 2, 1, 1, 6)]
    [InlineData(12, 99, 2, 0, 0, 100)]
    [InlineData(21, 3, 2, 0, 0, 4)]
    [InlineData(22, 99, 2, 0, 0, 100)]
    [InlineData(31, 5, 2, 0, 0, 6)]
    [InlineData(32, 199, 2, 0, 0, 200)]
    public void EnterpriseUpgrade_SpendsOnlyServerPoolAndHonorsPinnedLimit(
        int businessKey,
        int current,
        int pool,
        int partnerships,
        int territories,
        int expected)
    {
        int markerKey = businessKey / 10;
        IDictionary state = new Hashtable
        {
            [6] = pool,
            [markerKey] = 0,
            [businessKey] = current,
            [22] = businessKey == 21 ? 20 : businessKey == 22 ? current : 0,
            [32] = businessKey == 31 ? 30 : businessKey == 32 ? current : 0,
        };

        Assert.True(FourberieEnterpriseAuthority.TryUpgrade(
            state, businessKey, partnerships, territories, out var failure), failure);

        Assert.Equal(pool - 1, state[6]);
        Assert.Equal(expected, state[businessKey]);
        Assert.False(FourberieEnterpriseAuthority.TryUpgrade(
            state, businessKey, partnerships, territories, out _));
    }

    [Fact]
    public void EnterpriseUpgrade_CreatesFirstSecondaryLevelAfterPrimaryBusinessStarts()
    {
        IDictionary state = new Hashtable { [1] = 0, [6] = 2, [11] = 1 };

        Assert.True(FourberieEnterpriseAuthority.TryUpgrade(
            state, 12, 0, 0, out var failure), failure);

        Assert.Equal(1, state[12]);
        Assert.Equal(1, state[6]);
    }

    [Fact]
    public void EnterpriseDowngrade_ReturnsOnePointToServerPoolAndRejectsZeroLevel()
    {
        IDictionary state = new Hashtable { [1] = 0, [6] = 2, [11] = 1 };

        Assert.True(FourberieEnterpriseAuthority.TryDowngrade(state, 11, out var failure), failure);
        Assert.Equal(3, state[6]);
        Assert.Equal(0, state[11]);
        Assert.False(FourberieEnterpriseAuthority.TryDowngrade(state, 11, out _));
    }

    [Fact]
    public void EnterpriseMutationPrefixes_SubmitTypedClientIntentAndNeverRunOriginal()
    {
        bool previousServer = ModInformation.IsServer;
        IFourberiePatchRuntime previousRuntime = FourberiePatchRuntime.Current;
        var runtime = new CaptureRuntime();
        try
        {
            ModInformation.IsServer = false;
            FourberiePatchRuntime.Current = runtime;

            Assert.False(FourberieAuthorityPatches.BusinessUpgradeConsequencePrefix(
                new object[] { 6, 22, 100 }));
            Assert.Equal(FourberieOperation.UpgradeCriminalBusiness, runtime.LastOperation.Operation);
            Assert.Equal(22, runtime.LastOperation.IntValue);
            Assert.Empty(runtime.LastOperation.Troops);

            Assert.False(FourberieAuthorityPatches.BusinessDowngradeConsequencePrefix(
                new object[] { 6, 31 }));
            Assert.Equal(FourberieOperation.DowngradeCriminalBusiness, runtime.LastOperation.Operation);
            Assert.Equal(31, runtime.LastOperation.IntValue);

            ModInformation.IsServer = true;
            Assert.False(FourberieAuthorityPatches.BusinessUpgradeConsequencePrefix(
                new object[] { 6, 11, 3 }));
            Assert.Equal(2, runtime.SubmissionCount);
        }
        finally
        {
            FourberiePatchRuntime.Current = previousRuntime;
            ModInformation.IsServer = previousServer;
        }
    }

    [Fact]
    public void SchemeBonusAuthority_UsesServerCoverageAndNetworkLimit()
    {
        IDictionary crime = new Hashtable { [750] = 1 };
        IDictionary clans = new Hashtable { ["FMal_kingdom_a"] = 5 };

        Assert.True(FourberieSchemeBonusAuthority.TryUpgrade(
            crime, clans, 7, "kingdom_a", schemeBase: 20, schemeNetwork: 10, out var failure), failure);
        Assert.Equal(2, crime[750]);
        Assert.False(FourberieSchemeBonusAuthority.TryUpgrade(
            crime, clans, 7, "kingdom_a", schemeBase: 20, schemeNetwork: 0, out _));

        Assert.True(FourberieSchemeBonusAuthority.TryDowngrade(crime, 7, out failure), failure);
        Assert.Equal(1, crime[750]);
        Assert.True(FourberieSchemeBonusAuthority.TryReset(crime, 7, out failure), failure);
        Assert.False(crime.Contains(750));
    }

    [Fact]
    public void SchemeBonusAuthority_RejectsUnsupportedSlotsWithoutMutation()
    {
        IDictionary crime = new Hashtable { [750] = 2 };
        IDictionary clans = new Hashtable();

        Assert.False(FourberieSchemeBonusAuthority.TryUpgrade(
            crime, clans, 9, "kingdom_a", 90, 90, out _));
        Assert.False(FourberieSchemeBonusAuthority.TryDowngrade(crime, 9, out _));
        Assert.False(FourberieSchemeBonusAuthority.TryReset(crime, 9, out _));
        Assert.Equal(2, crime[750]);
    }

    [Theory]
    [InlineData(250, 170, -1, -1, 1, 1, 47)]
    [InlineData(0, 0, 1, 1, -1, 0, 5)]
    [InlineData(0, 0, 0, 0, 0, 0, 0)]
    public void SchemeBonusAuthority_ComputesPinnedEnforcerBase(
        int roguery,
        int tactics,
        int valor,
        int mercy,
        int honor,
        int calculating,
        int expected)
    {
        Assert.Equal(expected, FourberieSchemeBonusAuthority.ComputeBase(
            roguery, tactics, valor, mercy, honor, calculating));
    }

    [Fact]
    public void SchemeBonusPrefixes_SubmitTypedClientIntentAndNeverRunOriginal()
    {
        bool previousServer = ModInformation.IsServer;
        IFourberiePatchRuntime previousRuntime = FourberiePatchRuntime.Current;
        var runtime = new CaptureRuntime();
        try
        {
            ModInformation.IsServer = false;
            FourberiePatchRuntime.Current = runtime;

            Assert.False(FourberieAuthorityPatches.SchemeBonusUpgradeConsequencePrefix(new object[] { 7 }));
            Assert.Equal(FourberieOperation.UpgradeSchemeBonus, runtime.LastOperation.Operation);
            Assert.Equal(7, runtime.LastOperation.IntValue);

            Assert.False(FourberieAuthorityPatches.SchemeBonusDowngradeConsequencePrefix(new object[] { 8 }));
            Assert.Equal(FourberieOperation.DowngradeSchemeBonus, runtime.LastOperation.Operation);

            ModInformation.IsServer = true;
            Assert.False(FourberieAuthorityPatches.SchemeBonusResetConsequencePrefix(null));
            Assert.Equal(2, runtime.SubmissionCount);
        }
        finally
        {
            FourberiePatchRuntime.Current = previousRuntime;
            ModInformation.IsServer = previousServer;
        }
    }

    [Fact]
    public void AgentPartyAuthority_CapsCreationAndRefillAtTwenty()
    {
        IDictionary state = new Hashtable { [300] = 28 };

        Assert.True(FourberieAgentPartyAuthority.TryTakeForCreate(
            state, out var created, out var failure), failure);
        Assert.Equal(20, created);
        Assert.Equal(8, state[300]);

        Assert.True(FourberieAgentPartyAuthority.TryTakeForRefill(
            state, currentPartyCount: 17, out var refilled, out failure), failure);
        Assert.Equal(3, refilled);
        Assert.Equal(5, state[300]);
        Assert.False(FourberieAgentPartyAuthority.TryTakeForRefill(
            state, currentPartyCount: 20, out _, out _));
    }

    [Fact]
    public void AgentPartyAuthority_ReturnsSaboteursAndOtherTroopsToPinnedPools()
    {
        IDictionary state = new Hashtable { [300] = 2, [301] = 4 };

        Assert.True(FourberieAgentPartyAuthority.TryReturnDisbanded(
            state, saboteurs: 6, otherTroops: 3, out var failure), failure);

        Assert.Equal(8, state[300]);
        Assert.Equal(7, state[301]);
    }

    [Theory]
    [InlineData("createAgentsParty", (int)FourberieOperation.CreateAgentParty)]
    [InlineData("disbandAgentsParty", (int)FourberieOperation.DisbandAgentParty)]
    [InlineData("addAgentsToParty", (int)FourberieOperation.RefillAgentParty)]
    [InlineData("enlistAgents", 0)]
    public void AgentPartySelection_MapsOnlyDirectStateOptions(string selection, int expectedOperation)
    {
        FourberieOperation? operation = FourberieAuthorityPatches.AgentPartyOperationForSelection(selection);

        Assert.Equal(expectedOperation == 0 ? null : (FourberieOperation?)expectedOperation, operation);
    }

    [Fact]
    public void CrimeBaseResetPrefix_SubmitsTypedClientIntentAndNeverRunsOriginal()
    {
        bool previousServer = ModInformation.IsServer;
        IFourberiePatchRuntime previousRuntime = FourberiePatchRuntime.Current;
        var runtime = new CaptureRuntime();
        try
        {
            ModInformation.IsServer = false;
            FourberiePatchRuntime.Current = runtime;

            Assert.False(FourberieAuthorityPatches.CrimeBaseResetConsequencePrefix());
            Assert.Equal(FourberieOperation.ResetCrimeBaseParty, runtime.LastOperation.Operation);
            Assert.Equal(0, runtime.LastOperation.IntValue);

            ModInformation.IsServer = true;
            Assert.False(FourberieAuthorityPatches.CrimeBaseResetConsequencePrefix());
            Assert.Equal(1, runtime.SubmissionCount);
        }
        finally
        {
            FourberiePatchRuntime.Current = previousRuntime;
            ModInformation.IsServer = previousServer;
        }
    }

    [Fact]
    public void RoleAuthority_AssignsAndRemovesOnlyPinnedRoles()
    {
        IDictionary roles = new Hashtable { ["paymaster"] = "hero_old" };

        Assert.True(FourberieRoleAuthority.TryAssign(
            roles, roleCode: 1, heroId: "hero_new", out var failure), failure);
        Assert.Equal("hero_new", roles["paymaster"]);
        Assert.True(FourberieRoleAuthority.TryAssign(
            roles, roleCode: 2, heroId: "hero_enforcer", out failure), failure);
        Assert.Equal("hero_enforcer", roles["enforcer"]);
        Assert.True(FourberieRoleAuthority.TryRemove(roles, roleCode: 1, out failure), failure);
        Assert.False(roles.Contains("paymaster"));
        Assert.False(FourberieRoleAuthority.TryAssign(roles, 3, "hero_bad", out _));
    }

    [Fact]
    public void RoleSnapshot_RestoresServerOwnedMappingsAfterClientPresentationRefresh()
    {
        IDictionary roles = new Hashtable
        {
            ["paymaster"] = "hero_paymaster",
            ["enforcer"] = "hero_enforcer",
            ["victim7"] = "hero_victim",
        };
        FourberieRoleSnapshot snapshot = FourberieRoleAuthority.Capture(roles);

        roles.Remove("paymaster");
        roles["enforcer"] = "wrong_hero";
        roles["victim7"] = "new_victim";
        snapshot.Restore(roles);

        Assert.Equal("hero_paymaster", roles["paymaster"]);
        Assert.Equal("hero_enforcer", roles["enforcer"]);
        Assert.Equal("new_victim", roles["victim7"]);
    }

    [Theory]
    [InlineData("paymaster", 1)]
    [InlineData("enforcer", 2)]
    [InlineData("other", 0)]
    public void RoleAuthority_MapsPinnedRoleNames(string role, int expected) =>
        Assert.Equal(expected, FourberieRoleAuthority.RoleCode(role));

    [Fact]
    public void OperationProtocol_RequiresExactRoleAssignmentShapes()
    {
        var assign = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            FourberieOperation.AssignCriminalRole,
            string.Empty, "hero_a", 1, Array.Empty<FourberieTroopSelection>());
        var remove = new NetworkRequestFourberieOperation(
            assign.SessionId, 8, assign.ExpectedRevision,
            FourberieOperation.RemoveCriminalRole,
            string.Empty, string.Empty, 2, Array.Empty<FourberieTroopSelection>());

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(assign));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(remove));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            assign.SessionId, 9, assign.ExpectedRevision, assign.Operation,
            string.Empty, string.Empty, assign.IntValue, assign.Troops)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            remove.SessionId, 10, remove.ExpectedRevision, remove.Operation,
            string.Empty, "hero_a", remove.IntValue, remove.Troops)));
    }

    [Fact]
    public void BehaviorPreflight_ConstructsEveryBehaviorBeforeReturningAny()
    {
        var types = new[] { "first", "second" };

        var behaviors = FourberieAuthorityPatches.PreflightBehaviors(
            types,
            name => name == "first" ? typeof(FirstBehavior) : typeof(SecondBehavior));

        Assert.Collection(
            behaviors,
            behavior => Assert.IsType<FirstBehavior>(behavior),
            behavior => Assert.IsType<SecondBehavior>(behavior));
    }

    [Fact]
    public void BehaviorPreflight_FailsClosedWhenAnyBehaviorCannotBeConstructed()
    {
        var types = new[] { "first", "missing" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FourberieAuthorityPatches.PreflightBehaviors(
                types,
                name => name == "first" ? typeof(FirstBehavior) : null));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InitializeBehaviorsAndModelsWithoutStarter_FailsClosedInsteadOfPartiallyRegistering()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FourberieAuthorityPatches.InitializeBehaviorsAndModelsPrefix(Array.Empty<object>()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FinanceCalculation_DisablesWithdrawalSideEffectsOnlyOnClients(
        bool isServer,
        bool expectedApplyWithdrawals)
    {
        bool previous = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = isServer;
            bool applyWithdrawals = true;

            FourberieAuthorityPatches.FinanceReadPrefix(ref applyWithdrawals);

            Assert.Equal(expectedApplyWithdrawals, applyWithdrawals);
        }
        finally
        {
            ModInformation.IsServer = previous;
        }
    }
}
