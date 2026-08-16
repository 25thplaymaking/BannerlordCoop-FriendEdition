using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.Locations.Messages.Conversation;
using GameInterface.Services.ObjectManager;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Locations;

public sealed class LocationConversationTrackerAuthorityTests
{
    [Fact]
    public void Lease_EndIsOwnerOnly_AndTombstoneIsIdempotent()
    {
        using var tracker = new LocationConversationTracker(new Mock<IObjectManager>().Object);
        var owner = new object();
        var lease = tracker.BeginLease(owner, "session", "location", "owner", "target");
        Assert.False(tracker.TryEndLease(new object(), lease.Id, out _));
        Assert.True(tracker.TryEndLease(owner, lease.Id, out var ended));
        Assert.False(ended.Active);
        Assert.True(tracker.TryEndLease(owner, lease.Id, out var replay));
        Assert.Equal(ended.Revision, replay.Revision);
    }

    [Fact]
    public void Replica_RejectsDelayedOldSessionAfterAcceptedReplacement()
    {
        using var tracker = new LocationConversationTracker(new Mock<IObjectManager>().Object);
        tracker.ResetReplicaSession("new-session");
        Assert.True(tracker.ApplyLeaseState(new NetworkLocationConversationLeaseState("new-session", "lease", 1, true, "loc", "owner", "target")));
        Assert.False(tracker.ApplyLeaseState(new NetworkLocationConversationLeaseState("old-session", "old", 99, true, "loc", "owner", "target")));
        Assert.True(tracker.IsReplicaLease("new-session", "lease", 1, true, "loc", "owner", "target"));
    }
}
