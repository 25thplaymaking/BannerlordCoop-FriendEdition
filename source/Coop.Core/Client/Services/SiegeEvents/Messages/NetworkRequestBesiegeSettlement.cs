using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.SiegeEvents.Messages;

/// <summary>
/// Client asks the server to start a siege of a settlement led by its party.
/// </summary>
[AuthorityRoute("siege.besiege-settlement", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public record NetworkRequestBesiegeSettlement : ICommand
{
    [ProtoMember(1)]
    public string PartyId { get; }
    [ProtoMember(2)]
    public string SettlementId { get; }
    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestBesiegeSettlement(string partyId, string settlementId, AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Header = header;
    }
}
