using System;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Tournaments.Data;
using ProtoBuf;

namespace GameInterface.Services.Tournaments.Messages;

[AuthorityRoute("tournament.state", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestTournamentState : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long Revision;
    [ProtoMember(4)] public readonly long AuthorityRequestId;

    public NetworkRequestTournamentState(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        Revision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, Revision);

    public bool TryValidateWireShape(out string failure)
    {
        if (ProtocolVersion != ModConfigSnapshot.CurrentProtocolVersion ||
            Revision < 0 ||
            SessionId == null ||
            SessionId.Length != ModConfigSnapshot.SessionIdLength ||
            !Guid.TryParseExact(SessionId, "N", out _))
        {
            failure = "invalid-tournament-state-query";
            return false;
        }

        failure = null;
        return true;
    }
}

/// <summary>
/// Correlated bootstrap response. The full snapshot is intentionally carried by the terminal
/// result so Accepted cannot be observed before the requester has a canonical state to apply.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentStateQueryResult : IEvent
{
    [ProtoMember(1)] public readonly NetworkTournamentStateSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;
    [ProtoMember(7)] public readonly long StateEpoch;

    public NetworkTournamentStateQueryResult(
        AuthorityRequestHeader request,
        AuthorityResultStatus status,
        NetworkTournamentStateSnapshot snapshot,
        long stateEpoch,
        string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
        StateEpoch = stateEpoch;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentStateSnapshot : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentNativeGameData[] NativeTournaments;
    [ProtoMember(2)]
    public readonly TournamentLeaderboardEntryData[] Leaderboard;
    [ProtoMember(3)]
    public readonly TournamentSessionSnapshot[] Sessions;

    public NetworkTournamentStateSnapshot(
        TournamentNativeGameData[] nativeTournaments,
        TournamentLeaderboardEntryData[] leaderboard,
        TournamentSessionSnapshot[] sessions)
    {
        NativeTournaments = nativeTournaments ?? Array.Empty<TournamentNativeGameData>();
        Leaderboard = leaderboard ?? Array.Empty<TournamentLeaderboardEntryData>();
        Sessions = sessions ?? Array.Empty<TournamentSessionSnapshot>();
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSessionRemoved : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly string TownId;

    public NetworkTournamentSessionRemoved(string sessionId, string townId)
    {
        SessionId = sessionId;
        TownId = townId;
    }
}

public sealed class TournamentSessionRemoved : IEvent
{
    public string SessionId { get; }
    public string TownId { get; }

    public TournamentSessionRemoved(string sessionId, string townId)
    {
        SessionId = sessionId;
        TownId = townId;
    }
}

public sealed class TournamentNativeStateChanged : IEvent
{
}
