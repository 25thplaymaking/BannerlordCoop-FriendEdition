using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages.Start;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public sealed class BattleStartCoordinatorCommitTests
{
    [Fact]
    public void AcceptedMission_WaitsForBattleModeRegistryThenCommits()
    {
        const string eventId = "battle-start-commit";
        var header = new AuthorityRequestHeader(1, "session", 12, 3);
        var reply = new NetworkBattleStartReply(header, AuthorityResultStatus.Accepted,
            (int)BattleStartMode.Mission, eventId, null);

        try
        {
            Assert.Equal(AuthorityCommitProbeResult.Pending, BattleStartCoordinator.ProbeClientCommit(reply));
            BattleModeRegistry.Begin(eventId, BattleStartMode.Mission);
            Assert.Equal(AuthorityCommitProbeResult.Applied, BattleStartCoordinator.ProbeClientCommit(reply));
        }
        finally
        {
            BattleModeRegistry.End();
        }
    }

    [Fact]
    public void ResultWithDifferentModeOrEvent_IsInvalidBeforeClientCommit()
    {
        var header = new AuthorityRequestHeader(1, "session", 12, 3);
        var request = new NetworkBattleStartRequest(header, (int)BattleStartMode.Mission, "event-a", "party-a");
        var wrongEvent = new NetworkBattleStartReply(header, AuthorityResultStatus.Accepted,
            (int)BattleStartMode.Mission, "event-b", null);
        var wrongMode = new NetworkBattleStartReply(header, AuthorityResultStatus.Accepted,
            (int)BattleStartMode.Simulation, "event-a", null);
        var invalidMode = new NetworkBattleStartReply(header, AuthorityResultStatus.Accepted,
            (int)BattleStartMode.Unclaimed, "event-a", null);

        Assert.False(BattleStartCoordinator.IsExpectedClientResult(request, wrongEvent));
        Assert.False(BattleStartCoordinator.IsExpectedClientResult(request, wrongMode));
        Assert.Equal(AuthorityCommitProbeResult.Invalid, BattleStartCoordinator.ProbeClientCommit(invalidMode));
    }
}
