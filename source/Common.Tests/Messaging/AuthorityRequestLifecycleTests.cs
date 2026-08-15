using Common.Messaging;

namespace Common.Tests.Messaging;

public class AuthorityRequestLifecycleTests
{
    [Fact]
    public void ClientLifecycle_RecordsAppliedServerApprovedRequest()
    {
        var lifecycle = new AuthorityRequestLifecycle("map-event.create");

        lifecycle.BeginClient("request-1");
        lifecycle.ClientSent("request-1");
        lifecycle.ClientReplyReceived("request-1", "Created");
        lifecycle.ClientApplied("request-1", "Created:map-event-7");

        Assert.True(lifecycle.TryGetSnapshot("request-1", out var snapshot));
        Assert.Equal(AuthorityRequestPhase.ClientApplied, snapshot.Phase);
        Assert.Equal("Created:map-event-7", snapshot.Outcome);
        Assert.True(snapshot.IsTerminal);
    }

    [Fact]
    public void TerminalFailure_CannotBeOverwrittenByALateReply()
    {
        var lifecycle = new AuthorityRequestLifecycle("map-event.create");

        lifecycle.BeginClient("request-2");
        lifecycle.ClientSent("request-2");
        lifecycle.ClientTimedOut("request-2", "Timeout:00:00:05");
        lifecycle.ClientReplyReceived("request-2", "Created");

        Assert.True(lifecycle.TryGetSnapshot("request-2", out var snapshot));
        Assert.Equal(AuthorityRequestPhase.ClientTimedOut, snapshot.Phase);
        Assert.Equal("Timeout:00:00:05", snapshot.Outcome);
    }

    [Fact]
    public void ServerLifecycle_RecordsValidationAndDecision()
    {
        var lifecycle = new AuthorityRequestLifecycle("map-event.create");

        lifecycle.BeginServer("request-3");
        lifecycle.ServerValidated("request-3");
        lifecycle.ServerResolved("request-3", "Rejected:party-not-controlled");

        Assert.True(lifecycle.TryGetSnapshot("request-3", out var snapshot));
        Assert.Equal(AuthorityRequestPhase.ServerResolved, snapshot.Phase);
        Assert.Equal("Rejected:party-not-controlled", snapshot.Outcome);
        Assert.True(snapshot.IsTerminal);
    }

    [Fact]
    public void CapacityPressure_EvictsTheOldestIncompleteRequestInsteadOfGrowingWithoutBound()
    {
        var lifecycle = new AuthorityRequestLifecycle("map-event.create", capacity: 1);

        lifecycle.BeginClient("request-4");
        lifecycle.BeginClient("request-5");

        Assert.False(lifecycle.TryGetSnapshot("request-4", out _));
        Assert.True(lifecycle.TryGetSnapshot("request-5", out _));
    }
}
