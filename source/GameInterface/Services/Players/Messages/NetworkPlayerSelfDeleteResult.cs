using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Players.Messages;

/// <summary>Terminal route metadata. Accepted replies are suppressed because self-delete disconnects the requester.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPlayerSelfDeleteResult : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    public NetworkPlayerSelfDeleteResult(AuthorityRequestHeader request, AuthorityResultStatus status, string reason)
    { SessionId = request.SessionId; AuthorityRequestId = request.RequestId; Status = status;
      CommittedRevision = request.ExpectedRevision; ReasonCode = reason; }
    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
