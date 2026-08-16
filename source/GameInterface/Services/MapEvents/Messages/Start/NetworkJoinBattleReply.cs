using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>[Server -&gt; Client] Completes one pending authoritative battle-join request.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkJoinBattleReply : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly string MapEventId;
    [ProtoMember(3)]
    public readonly string PartyId;
    [ProtoMember(4)]
    public readonly bool Accepted;
    [ProtoMember(5)] public readonly string SessionId;
    [ProtoMember(6)] public readonly long AuthorityRequestId;
    [ProtoMember(7)] public readonly AuthorityResultStatus Status;
    [ProtoMember(8)] public readonly long CommittedRevision;
    [ProtoMember(9)] public readonly string ReasonCode;
    [ProtoMember(10)] public readonly int Side;

    public NetworkJoinBattleReply(string requestId, string mapEventId, string partyId, bool accepted)
    {
        RequestId = requestId;
        MapEventId = mapEventId;
        PartyId = partyId;
        Accepted = accepted;
        SessionId = null;
        AuthorityRequestId = 0;
        Status = accepted ? AuthorityResultStatus.Accepted : AuthorityResultStatus.Rejected;
        CommittedRevision = 0;
        ReasonCode = null;
        Side = -1;
    }

    public NetworkJoinBattleReply(AuthorityRequestHeader header, AuthorityResultStatus status,
        string mapEventId, string partyId, int side, string reasonCode)
    {
        RequestId = header.RequestId.ToString();
        MapEventId = mapEventId;
        PartyId = partyId;
        Accepted = status == AuthorityResultStatus.Accepted;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        Status = status;
        CommittedRevision = 0;
        ReasonCode = reasonCode;
        Side = side;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
