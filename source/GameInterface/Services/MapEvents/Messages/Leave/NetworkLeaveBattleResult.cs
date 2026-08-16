using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Leave;

/// <summary>[Server -&gt; Client] Correlated terminal result for one authoritative party leave.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLeaveBattleResult : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long RequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string PartyId;
    [ProtoMember(7)] public readonly string MapEventId;
    [ProtoMember(8)] public readonly bool LeaveSiege;
    [ProtoMember(9)] public readonly bool FinishLocalMenus;

    public NetworkLeaveBattleResult(AuthorityRequestHeader header, AuthorityResultStatus status,
        string partyId, string mapEventId, bool leaveSiege, bool finishLocalMenus, string reasonCode)
    {
        SessionId = header.SessionId;
        RequestId = header.RequestId;
        Status = status;
        CommittedRevision = 0;
        ReasonCode = reasonCode;
        PartyId = partyId;
        MapEventId = mapEventId;
        LeaveSiege = leaveSiege;
        FinishLocalMenus = finishLocalMenus;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, RequestId, Status, CommittedRevision, ReasonCode);
}
