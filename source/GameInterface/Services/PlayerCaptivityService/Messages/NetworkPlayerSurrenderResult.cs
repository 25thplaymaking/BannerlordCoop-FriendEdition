using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.PlayerCaptivityService.Messages;

/// <summary>Correlated terminal result; the battle/captivity replicas remain the completion proof.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPlayerSurrenderResult : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string MapEventId;
    [ProtoMember(7)] public readonly string PlayerPartyId;

    public NetworkPlayerSurrenderResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        string mapEventId, string playerPartyId, string reasonCode)
    {
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
        MapEventId = mapEventId;
        PlayerPartyId = playerPartyId;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
