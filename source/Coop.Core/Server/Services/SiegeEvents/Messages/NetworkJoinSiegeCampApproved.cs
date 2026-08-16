using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

/// <summary>
/// Server approved the requester joining a siege camp; the camp write was already replicated ahead
/// of this message, so the requester can open its siege menus.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkJoinSiegeCampApproved : IEvent
{
    [ProtoMember(1)]
    public string SettlementId { get; }
    [ProtoMember(2)]
    public bool Approved { get; }
    [ProtoMember(3)]
    public AuthorityResultHeader Header { get; }
    [ProtoMember(4)]
    public string PartyId { get; }

    public NetworkJoinSiegeCampApproved(string settlementId, bool approved)
        : this(settlementId, approved, default, null)
    {
    }

    public NetworkJoinSiegeCampApproved(
        string settlementId,
        bool approved,
        AuthorityResultHeader header,
        string partyId)
    {
        SettlementId = settlementId;
        Approved = approved;
        Header = header;
        PartyId = partyId;
    }
}
