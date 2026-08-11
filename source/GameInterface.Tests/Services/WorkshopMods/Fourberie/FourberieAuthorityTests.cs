using GameInterface.Services.WorkshopMods.Fourberie;
using System;
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
            12,
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
}
