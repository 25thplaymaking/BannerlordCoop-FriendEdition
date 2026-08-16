using Common.Messaging;
using GameInterface.Services.Villages.Data;
using ProtoBuf;

namespace GameInterface.Services.Villages.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkVillageHostileActionStarted : ICommand
{
    [ProtoMember(1)]
    public readonly VillageHostileAction Action;
    [ProtoMember(2)]
    public readonly string MobilePartyId;
    [ProtoMember(3)]
    public readonly string SettlementId;
    [ProtoMember(4)]
    public readonly string SessionId;
    [ProtoMember(5)]
    public readonly long AuthorityRequestId;

    public NetworkVillageHostileActionStarted(
        VillageHostileAction action,
        string mobilePartyId,
        string settlementId,
        string sessionId,
        long authorityRequestId)
    {
        Action = action;
        MobilePartyId = mobilePartyId;
        SettlementId = settlementId;
        SessionId = sessionId;
        AuthorityRequestId = authorityRequestId;
    }
}
