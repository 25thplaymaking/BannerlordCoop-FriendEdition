using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.MobileParties.Messages;

/// <summary>
/// Message from the server commanding a party to enter a settlement.
/// For all parties except the player party
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkPartyEnterSettlement : ICommand
{
    [ProtoMember(1)]
    public string SettlementId;
    [ProtoMember(2)]
    public string PartyId;
    /// <summary>Present only for an authority route's requester-specific state acknowledgement.</summary>
    [ProtoMember(3)]
    public AuthorityResultHeader Header { get; }

    public NetworkPartyEnterSettlement(
        string settlementId,
        string partyId,
        AuthorityResultHeader header = default)
    {
        SettlementId = settlementId;
        PartyId = partyId;
        Header = header;
    }
}
