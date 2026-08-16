using Common.Messaging;
using GameInterface.Services.Villages.Data;
using ProtoBuf;

namespace GameInterface.Services.Villages.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("village.hostile-action", AuthorityRouteKind.Command)]
public readonly struct NetworkRequestVillageHostileAction : ICommand
{
    [ProtoMember(1)]
    public readonly VillageHostileAction Action;
    [ProtoMember(2)]
    public readonly string MobilePartyId;
    [ProtoMember(3)]
    public readonly string SettlementId;
    [ProtoMember(4)]
    public readonly int ProtocolVersion;
    [ProtoMember(5)]
    public readonly string SessionId;
    [ProtoMember(6)]
    public readonly long AuthorityRequestId;
    [ProtoMember(7)]
    public readonly long ExpectedRevision;

    public NetworkRequestVillageHostileAction(
        AuthorityRequestHeader header,
        VillageHostileAction action,
        string mobilePartyId,
        string settlementId)
    {
        Action = action;
        MobilePartyId = mobilePartyId;
        SettlementId = settlementId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new AuthorityRequestHeader(
        ProtocolVersion,
        SessionId,
        AuthorityRequestId,
        ExpectedRevision);
}
