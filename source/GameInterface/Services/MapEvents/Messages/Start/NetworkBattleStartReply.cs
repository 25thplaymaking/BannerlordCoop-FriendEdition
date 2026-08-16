using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Server -&gt; Client] Answer to <see cref="NetworkBattleStartRequest"/>: whether the server accepted starting the
/// battle in the requested mode. Members 1-2 preserve the old reply payload; members 3+ are the canonical
/// authority result used by the route's correlation and commit barrier.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkBattleStartReply : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;

    [ProtoMember(2)]
    public readonly bool Accepted;

    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly AuthorityResultStatus Status;
    [ProtoMember(6)] public readonly long CommittedRevision;
    [ProtoMember(7)] public readonly string ReasonCode;
    [ProtoMember(8)] public readonly int Mode;
    [ProtoMember(9)] public readonly string MapEventId;

    public NetworkBattleStartReply(string requestId, bool accepted)
    {
        RequestId = requestId;
        Accepted = accepted;
        SessionId = null;
        AuthorityRequestId = 0;
        Status = accepted ? AuthorityResultStatus.Accepted : AuthorityResultStatus.Rejected;
        CommittedRevision = 0;
        ReasonCode = null;
        Mode = (int)global::GameInterface.Services.MapEvents.Handlers.BattleStartMode.Unclaimed;
        MapEventId = null;
    }

    public NetworkBattleStartReply(AuthorityRequestHeader request, AuthorityResultStatus status,
        int mode, string mapEventId, string reasonCode)
    {
        RequestId = request.RequestId.ToString();
        Accepted = status == AuthorityResultStatus.Accepted;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
        Mode = mode;
        MapEventId = mapEventId;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
