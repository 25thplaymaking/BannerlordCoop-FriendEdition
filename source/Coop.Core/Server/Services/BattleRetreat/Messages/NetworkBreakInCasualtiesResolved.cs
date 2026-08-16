using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.BattleRetreat.Messages;

/// <summary>Correlated proof of the requester's authoritative break-in troop state.</summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkBreakInCasualtiesResolved : IEvent
{
    [ProtoMember(1)]
    public string PartyId { get; }
    [ProtoMember(2)]
    public string SettlementId { get; }
    [ProtoMember(3)]
    public int RegularMemberCount { get; }
    [ProtoMember(4)]
    public bool Approved { get; }
    [ProtoMember(5)]
    public AuthorityResultHeader Header { get; }

    public NetworkBreakInCasualtiesResolved(
        string partyId,
        string settlementId,
        int regularMemberCount,
        bool approved,
        AuthorityResultHeader header)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        RegularMemberCount = regularMemberCount;
        Approved = approved;
        Header = header;
    }
}
