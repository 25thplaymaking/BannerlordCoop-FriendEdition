using Common.Messaging;
using Coop.Tests.Mocks;
using GameInterface.Services.AuthorityRequests;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

public sealed class AuthorityReplayLedgerTests
{
    [Fact]
    public void SameRequestAndCommand_IsCompletedOnce_WhileAConflictingReplayIsRejected()
    {
        using var network = new TestNetwork();
        var peer = network.CreatePeer();
        var ledger = new AuthorityReplayLedger<TestResult>();

        Assert.Equal(AuthorityReplayDecision.New, ledger.Inspect(peer, "session", "test.route", 7, "first").Decision);
        Assert.Equal(AuthorityReplayDecision.InFlight, ledger.Inspect(peer, "session", "test.route", 7, "first").Decision);

        var result = new TestResult(new AuthorityResultHeader("session", 7, AuthorityResultStatus.Rejected, 0, "denied"));
        ledger.Complete(peer, "session", "test.route", 7, result);

        var completed = ledger.Inspect(peer, "session", "test.route", 7, "first");
        Assert.Equal(AuthorityReplayDecision.Completed, completed.Decision);
        Assert.Equal("denied", completed.Result.Header.ReasonCode);
        Assert.Equal(AuthorityReplayDecision.Conflict, ledger.Inspect(peer, "session", "test.route", 7, "other").Decision);
    }
}
