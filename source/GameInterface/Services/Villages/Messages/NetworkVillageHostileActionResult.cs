using Common.Messaging;
using GameInterface.Services.Villages.Data;
using ProtoBuf;

namespace GameInterface.Services.Villages.Messages;

/// <summary>Correlated terminal decision for the village hostile-action authority command.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkVillageHostileActionResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly VillageHostileAction Action;
    [ProtoMember(3)] public readonly string MobilePartyId;
    [ProtoMember(4)] public readonly string SettlementId;

    public NetworkVillageHostileActionResult(
        AuthorityResultHeader header,
        VillageHostileAction action,
        string mobilePartyId,
        string settlementId)
    {
        Header = header;
        Action = action;
        MobilePartyId = mobilePartyId;
        SettlementId = settlementId;
    }
}
