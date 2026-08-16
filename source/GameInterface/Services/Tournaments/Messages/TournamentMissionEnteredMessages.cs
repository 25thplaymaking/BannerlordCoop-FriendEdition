using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Tournaments.Data;
using ProtoBuf;

namespace GameInterface.Services.Tournaments.Messages;

/// <summary>
/// [Client -&gt; Server] Confirms that the requesting peer successfully entered the shared tournament mission. The
/// server adds spectators to the ballot only after this confirmation and elects the first entrant as NPC host.
/// </summary>
[AuthorityRoute("tournament.mission-entered", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentMissionEntered : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly string MissionInstanceId;
    [ProtoMember(4)] public readonly bool IsSpectator;
    [ProtoMember(5)] public readonly int ProtocolVersion;
    [ProtoMember(6)] public readonly string ConfigSessionId;
    [ProtoMember(7)] public readonly long AuthorityRequestId;
    [ProtoMember(8)] public readonly long ConfigRevision;

    public NetworkTournamentMissionEntered(AuthorityRequestHeader header, string sessionId, long expectedRevision,
        string missionInstanceId, bool isSpectator)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MissionInstanceId = missionInstanceId;
        IsSpectator = isSpectator;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentMissionEnteredResult : IEvent
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

    public NetworkTournamentMissionEnteredResult(AuthorityRequestHeader request, AuthorityResultStatus status,
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
