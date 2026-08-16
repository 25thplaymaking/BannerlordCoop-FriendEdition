using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

/// <summary>Terminal result for a requester-owned siege-assault command.</summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkSiegeAssaultApproved : IEvent
{
    [ProtoMember(1)]
    public bool Approved { get; }
    [ProtoMember(2)]
    public AuthorityResultHeader Header { get; }
    [ProtoMember(3)]
    public string PartyId { get; }
    [ProtoMember(4)]
    public string SettlementId { get; }
    [ProtoMember(5)]
    public string AttackerPartyId { get; }
    [ProtoMember(6)]
    public string MapEventId { get; }

    public NetworkSiegeAssaultApproved(
        bool approved,
        AuthorityResultHeader header,
        string partyId,
        string settlementId,
        string attackerPartyId = null,
        string mapEventId = null)
    {
        Approved = approved;
        Header = header;
        PartyId = partyId;
        SettlementId = settlementId;
        AttackerPartyId = attackerPartyId;
        MapEventId = mapEventId;
    }
}
