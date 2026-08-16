using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Tournaments.Data;
using ProtoBuf;

namespace GameInterface.Services.Tournaments.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentRequestRejected : ICommand
{
    [ProtoMember(1)]
    public readonly string TownId;
    [ProtoMember(2)]
    public readonly string Reason;

    public NetworkTournamentRequestRejected(string townId, string reason)
    {
        TownId = townId;
        Reason = reason;
    }
}

[AuthorityRoute("tournament.join", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestJoinTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string TownId;
    [ProtoMember(2)]
    public readonly string SessionId;
    [ProtoMember(3)]
    public readonly long ExpectedRevision;
    [ProtoMember(4)] public readonly int ProtocolVersion;
    [ProtoMember(5)] public readonly string ConfigSessionId;
    [ProtoMember(6)] public readonly long AuthorityRequestId;
    [ProtoMember(7)] public readonly long ConfigRevision;

    public NetworkRequestJoinTournament(AuthorityRequestHeader header, string townId, string sessionId, long expectedRevision)
    {
        TownId = townId;
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.leave-preparation", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestLeaveTournamentPreparation : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestLeaveTournamentPreparation(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentJoinResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkTournamentJoinResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentLeavePreparationResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string RemovedSessionId;
    [ProtoMember(3)] public readonly string RemovedTownId;
    [ProtoMember(4)] public readonly string SessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly AuthorityResultStatus Status;
    [ProtoMember(7)] public readonly long CommittedRevision;
    [ProtoMember(8)] public readonly string ReasonCode;

    public NetworkTournamentLeavePreparationResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string removedSessionId, string removedTownId, string reasonCode)
    {
        Snapshot = snapshot;
        RemovedSessionId = removedSessionId;
        RemovedTownId = removedTownId;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[AuthorityRoute("tournament.start", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestStartTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestStartTournament(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.spectate", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestSpectateTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestSpectateTournament(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentLaunchResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;
    [ProtoMember(7)] public readonly string TournamentSessionId;
    [ProtoMember(8)] public readonly string MissionInstanceId;
    [ProtoMember(9)] public readonly string RequesterControllerId;
    [ProtoMember(10)] public readonly bool IsSpectator;

    public NetworkTournamentLaunchResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string requesterControllerId, bool isSpectator, string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
        TournamentSessionId = snapshot?.SessionId;
        MissionInstanceId = snapshot?.MissionInstanceId;
        RequesterControllerId = requesterControllerId;
        IsSpectator = isSpectator;
    }

    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestLeaveActiveTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;

    public NetworkRequestLeaveActiveTournament(string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestTournamentChoice : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)]
    public readonly string MatchId;
    [ProtoMember(4)]
    public readonly TournamentPlayerChoice Choice;

    public NetworkRequestTournamentChoice(
        string sessionId,
        long expectedRevision,
        string matchId,
        TournamentPlayerChoice choice)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Choice = choice;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestTournamentBet : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)]
    public readonly string MatchId;
    [ProtoMember(4)]
    public readonly int Amount;
    [ProtoMember(5)]
    public readonly long Sequence;

    public NetworkRequestTournamentBet(
        string sessionId,
        long expectedRevision,
        string matchId,
        int amount,
        long sequence)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Amount = amount;
        Sequence = sequence;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkSubmitTournamentSpawnManifest : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSpawnManifestData Manifest;

    public NetworkSubmitTournamentSpawnManifest(TournamentSpawnManifestData manifest)
    {
        Manifest = manifest;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkSubmitTournamentMatchResult : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentMatchResultData Result;

    public NetworkSubmitTournamentMatchResult(TournamentMatchResultData result)
    {
        Result = result;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSessionSnapshot : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSessionSnapshot Snapshot;

    public NetworkTournamentSessionSnapshot(TournamentSessionSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSpawnManifest : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSpawnManifestData Manifest;

    public NetworkTournamentSpawnManifest(TournamentSpawnManifestData manifest)
    {
        Manifest = manifest;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentBetResult : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long Revision;
    [ProtoMember(3)]
    public readonly bool Accepted;
    [ProtoMember(4)]
    public readonly string Reason;
    [ProtoMember(5)]
    public readonly int BettedDenars;
    [ProtoMember(6)]
    public readonly int ExpectedPayout;
    [ProtoMember(7)]
    public readonly long Sequence;
    [ProtoMember(8)]
    public readonly string MatchId;
    [ProtoMember(9)]
    public readonly int ThisRoundBettedDenars;
    [ProtoMember(10)]
    public readonly bool IsSettlement;

    public NetworkTournamentBetResult(
        string sessionId,
        long revision,
        long sequence,
        string matchId,
        bool accepted,
        string reason,
        int bettedDenars,
        int thisRoundBettedDenars,
        int expectedPayout,
        bool isSettlement)
    {
        SessionId = sessionId;
        Revision = revision;
        Sequence = sequence;
        MatchId = matchId;
        Accepted = accepted;
        Reason = reason;
        BettedDenars = bettedDenars;
        ThisRoundBettedDenars = thisRoundBettedDenars;
        ExpectedPayout = expectedPayout;
        IsSettlement = isSettlement;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkEnterTournamentMission : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)]
    public readonly bool IsSpectator;
    [ProtoMember(3)] public readonly string ConfigSessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly string TournamentSessionId;
    [ProtoMember(6)] public readonly string MissionInstanceId;
    [ProtoMember(7)] public readonly string RequesterControllerId;

    public NetworkEnterTournamentMission(AuthorityRequestHeader request, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator)
    {
        Snapshot = snapshot;
        IsSpectator = isSpectator;
        ConfigSessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        TournamentSessionId = snapshot?.SessionId;
        MissionInstanceId = snapshot?.MissionInstanceId;
        RequesterControllerId = requesterControllerId;
    }

    public bool IsExactLaunch(AuthorityRequestHeader request, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator) =>
        request.SessionId == ConfigSessionId && request.RequestId == AuthorityRequestId &&
        snapshot != null && snapshot.SessionId == TournamentSessionId &&
        snapshot.MissionInstanceId == MissionInstanceId && requesterControllerId == RequesterControllerId &&
        isSpectator == IsSpectator;
}

public sealed class TournamentSessionUpdated : IEvent
{
    public TournamentSessionSnapshot Snapshot { get; }

    public TournamentSessionUpdated(TournamentSessionSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

public sealed class TournamentSpawnManifestUpdated : IEvent
{
    public TournamentSpawnManifestData Manifest { get; }

    public TournamentSpawnManifestUpdated(TournamentSpawnManifestData manifest)
    {
        Manifest = manifest;
    }
}
