using GameInterface.Services.WorkshopMods.Fourberie;
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
}
