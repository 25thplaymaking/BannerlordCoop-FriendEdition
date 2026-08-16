using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Messages;
using System.Collections.Generic;

namespace GameInterface.Services.Tournaments.Handlers;

internal sealed partial class TournamentSessionHandler
{
    private bool RemoveSessionAndBroadcast(TournamentSessionSnapshot snapshot, long authorityRequestId = 0)
    {
        if (snapshot == null)
            return false;

        string configSessionId = configAuthority.TryGetCurrent(out var config) ? config.SessionId : string.Empty;
        var tombstone = new NetworkTournamentSessionRemoved(
            configSessionId,
            snapshot.SessionId,
            snapshot.TownId,
            snapshot.MissionInstanceId,
            snapshot.Revision + 1,
            authorityRequestId);
        // Retain terminal state before removing the mutable session. This is deliberately not
        // treated as complete until the same tombstone has been broadcast.
        if (!sessionRegistry.ApplyTombstone(tombstone) &&
            !sessionRegistry.TryGetTombstone(configSessionId, snapshot.SessionId, out _))
            return false;

        RemoveSessionTracking(liveProgressionControllers, acceptedHitProgression, snapshot.SessionId);
        RemoveAcceptedHitProgression(consumedHitProgression, snapshot.SessionId);

        try
        {
            network.SendAll(tombstone);
        }
        catch
        {
            // The registry retains this exact terminal record. A send failure must never be
            // mistaken for completion merely because the mutable session is already absent.
            network.SendAll(tombstone);
        }
        messageBroker.Publish(this, new TournamentSessionRemoved(tombstone));
        return true;
    }

    internal static void RemoveSessionTracking(
        HashSet<string> liveProgressionControllers,
        HashSet<string> acceptedHitProgression,
        string sessionId)
    {
        liveProgressionControllers.RemoveWhere(key =>
            key.StartsWith($"{sessionId}\n", System.StringComparison.Ordinal));
        RemoveAcceptedHitProgression(acceptedHitProgression, sessionId);
    }
}
