using GameInterface.Services.ObjectManager;
using GameInterface.Services.Tournaments;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Handlers;
using Moq;
using Serilog;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.Tournaments;

public class TournamentHitProgressionCleanupTests
{
    [Fact]
    public void RemoveAcceptedHitProgression_RemovesOnlyTargetSessionEntries()
    {
        var acceptedHitProgression = new HashSet<string>
        {
            "session-a\nmatch-1\ncontroller-1\n1",
            "session-a\nmatch-1\ncontroller-1\n2",
            "session-b\nmatch-1\ncontroller-1\n1"
        };

        TournamentSessionHandler.RemoveAcceptedHitProgression(acceptedHitProgression, "session-a");

        Assert.Single(acceptedHitProgression);
        Assert.Contains("session-b\nmatch-1\ncontroller-1\n1", acceptedHitProgression);
    }

    [Fact]
    public void RemoveSessionTracking_RemovesLiveControllerAndHitProgressionForOnlyTargetSession()
    {
        var liveProgressionControllers = new HashSet<string>
        {
            "session-a\ncontroller-1",
            "session-b\ncontroller-1"
        };
        var acceptedHitProgression = new HashSet<string>
        {
            "session-a\nmatch-1\ncontroller-1\n1",
            "session-b\nmatch-1\ncontroller-1\n1"
        };

        TournamentSessionHandler.RemoveSessionTracking(
            liveProgressionControllers,
            acceptedHitProgression,
            "session-a");

        Assert.DoesNotContain("session-a\ncontroller-1", liveProgressionControllers);
        Assert.Contains("session-b\ncontroller-1", liveProgressionControllers);
        Assert.Single(acceptedHitProgression);
        Assert.Contains("session-b\nmatch-1\ncontroller-1\n1", acceptedHitProgression);
    }

    [Fact]
    public void SimulationProgression_IsSkippedOnlyForTheControllerWhoseHitXpWasAccepted()
    {
        var liveProgressionControllers = new HashSet<string>
        {
            TournamentSessionHandler.ProgressionControllerKey("session-a", "controller-1")
        };
        var progressedPlayer = Contestant("controller-1");
        var playerWithoutAcceptedHits = Contestant("controller-2");
        var simulatedHero = Contestant(null, isHuman: false);

        Assert.False(TournamentSessionHandler.NeedsSimulationProgression(
            "session-a", progressedPlayer, liveProgressionControllers));
        Assert.True(TournamentSessionHandler.NeedsSimulationProgression(
            "session-a", playerWithoutAcceptedHits, liveProgressionControllers));
        Assert.True(TournamentSessionHandler.NeedsSimulationProgression(
            "session-a", simulatedHero, liveProgressionControllers));
    }

    [Fact]
    public void TryCreateSessionId_DoesNotRetainGeneratedIdentity()
    {
        var objectManager = new ObjectManager(Mock.Of<ILogger>());

        Assert.True(TournamentGameInterface.TryCreateSessionId(objectManager, out var firstSessionId));
        Assert.True(TournamentGameInterface.TryCreateSessionId(objectManager, out var secondSessionId));

        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.False(objectManager.Contains(firstSessionId));
        Assert.False(objectManager.Contains(secondSessionId));
    }

    private static TournamentContestantData Contestant(string controllerId, bool isHuman = true) =>
        new(
            "slot-" + (controllerId ?? "simulated"),
            "character-" + (controllerId ?? "simulated"),
            1,
            controllerId,
            "name",
            isHuman,
            false,
            false,
            string.Empty,
            0);
}
