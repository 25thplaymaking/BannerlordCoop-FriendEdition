using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.MapEvents.Messages.Conversation;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public class ConversationPartyTrackerTests
{
    [Fact]
    public void Lease_EndRequiresOwner_AndLeavesMonotonicTombstone()
    {
        using var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var owner = new object();
        var other = new object();
        var active = tracker.BeginLease(owner, "owner-party", "target-party", "session-1", "lease-1");

        Assert.False(tracker.TryEndLease(other, active.LeaseId, out _, out var owned));
        Assert.False(owned);
        Assert.True(tracker.TryEndLease(owner, active.LeaseId, out var ended, out owned));
        Assert.True(owned);
        Assert.False(ended.Active);
        Assert.True(ended.Revision > active.Revision);
        Assert.True(tracker.TryGetLease(active.LeaseId, out var tombstone));
        Assert.False(tombstone.Active);
        Assert.Equal(ended.Revision, tombstone.Revision);
    }

    [Fact]
    public void ReplicaLease_IsScopedToAcceptedSession_AndConflictingRevisionFailsClosed()
    {
        using var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var active = new NetworkConversationLeaseState("lease", 3, true, "owner", "target", "session-a");

        tracker.ResetReplicaSession("session-a");
        Assert.Equal(ConversationLeaseApplyResult.Applied, tracker.ApplyLeaseState(active));
        Assert.True(tracker.IsReplicaLease("session-a", "lease", 3, true, "owner", "target"));
        Assert.False(tracker.IsReplicaLease("session-b", "lease", 3, true, "owner", "target"));

        Assert.Equal(ConversationLeaseApplyResult.Conflict, tracker.ApplyLeaseState(
            new NetworkConversationLeaseState("lease", 3, false, "owner", "target", "session-a")));
        Assert.True(tracker.IsReplicaLeaseConflicted("session-a", "lease"));

        tracker.ResetReplicaSession("session-b");
        Assert.Equal(ConversationLeaseApplyResult.Applied, tracker.ApplyLeaseState(
            new NetworkConversationLeaseState("replacement", 1, true, "owner", "target", "session-b")));
        Assert.False(tracker.IsReplicaLeaseConflicted("session-a", "lease"));
        Assert.True(tracker.IsReplicaLease("session-b", "replacement", 1, true, "owner", "target"));
    }

    [Fact]
    public void ReplicaLease_OldSessionAfterAcceptedReplacement_IsIgnored()
    {
        using var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        tracker.ResetReplicaSession("session-new");
        var current = new NetworkConversationLeaseState("new", 5, true, "owner", "target", "session-new");
        Assert.Equal(ConversationLeaseApplyResult.Applied, tracker.ApplyLeaseState(current));

        Assert.Equal(ConversationLeaseApplyResult.Stale, tracker.ApplyLeaseState(
            new NetworkConversationLeaseState("old", 99, true, "owner", "target", "session-old")));
        Assert.True(tracker.IsReplicaLease("session-new", "new", 5, true, "owner", "target"));
        Assert.False(tracker.IsReplicaLease("session-old", "old", 99, true, "owner", "target"));
    }

    [Fact]
    public void RefreshingSameEngagement_RecordsServerDetectedDefender()
    {
        var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var peer = new object();

        Assert.True(tracker.TryBeginEngagement(peer, "player-party", "bandit-party", false));
        Assert.True(tracker.TryBeginEngagement(peer, "player-party", "bandit-party", true, engagerIsDefender: true));
        Assert.True(tracker.TryGetEngagement(peer, out var engagement));
        Assert.True(engagement.EngagerIsDefender);
        Assert.False(engagement.WasAiDisabled);

        Assert.True(tracker.TryEndEngagement(peer, out _, out _, out _));
        tracker.Dispose();
    }

    [Fact]
    public void StaleRequestCannotEndRefreshedEngagement()
    {
        var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var peer = new object();

        Assert.True(tracker.TryBeginEngagement(
            peer,
            "player-party",
            "bandit-party",
            false,
            requestId: "older-request"));
        Assert.True(tracker.TryBeginEngagement(
            peer,
            "player-party",
            "bandit-party",
            true,
            requestId: "current-request"));

        Assert.False(tracker.TryEndEngagement(
            peer,
            out _,
            out _,
            out _,
            expectedRequestId: "older-request",
            requireRequestIdMatch: true));
        Assert.True(tracker.TryGetEngagement(peer, out var currentEngagement));
        Assert.Equal("current-request", currentEngagement.RequestId);

        Assert.True(tracker.TryEndEngagement(
            peer,
            out _,
            out _,
            out _,
            expectedRequestId: "current-request",
            requireRequestIdMatch: true));
        tracker.Dispose();
    }

    [Fact]
    public void TryGetEngagementByEngagerParty_FindsOnlyThatPlayersHold()
    {
        var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var firstPlayer = new object();
        var secondPlayer = new object();

        Assert.True(tracker.TryBeginEngagement(firstPlayer, "player-1", "lord-1", wasAiDisabled: false));
        Assert.True(tracker.TryBeginEngagement(secondPlayer, "player-2", "lord-2", wasAiDisabled: false));

        Assert.True(tracker.TryGetEngagementByEngagerParty("player-2", out var engagement));
        Assert.Same(secondPlayer, engagement.EngagerKey);
        Assert.Equal("lord-2", engagement.PartyId);
        Assert.False(tracker.TryGetEngagementByEngagerParty("missing-player", out _));

        Assert.True(tracker.TryEndEngagement(firstPlayer, out _, out _, out _));
        Assert.True(tracker.TryEndEngagement(secondPlayer, out _, out _, out _));
        tracker.Dispose();
    }

    // A held party belongs to exactly one player. Server-side conversation outcomes are authorised
    // only as "this peer holds an engagement with this party", so a shared hold let two players each
    // apply the same one-shot result - two recruiters persuading one lord, both paying, the lord
    // defecting twice.
    [Fact]
    public void TryBeginEngagement_WhenPartyEngagedByAnotherPlayer_Fails()
    {
        var tracker = new ConversationPartyTracker(new Mock<IObjectManager>().Object);
        var firstPlayer = new object();
        var secondPlayer = new object();

        Assert.True(tracker.TryBeginEngagement(firstPlayer, "player1", "lord1", wasAiDisabled: false));
        Assert.False(tracker.TryBeginEngagement(secondPlayer, "player2", "lord1", wasAiDisabled: true));
        Assert.False(tracker.TryGetEngagement(secondPlayer, out _));

        // The holder still owns it, and releasing frees the party for the next player.
        Assert.True(tracker.TryEndEngagement(firstPlayer, out _, out _, out var shouldRelease));
        Assert.True(shouldRelease);
        Assert.True(tracker.TryBeginEngagement(secondPlayer, "player2", "lord1", wasAiDisabled: false));

        Assert.True(tracker.TryEndEngagement(secondPlayer, out _, out _, out _));
        tracker.Dispose();
    }
}
