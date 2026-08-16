using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.SiegeEvents.Messages;

/// <summary>
/// Client asks the server to start the wall assault of a settlement its party is besieging.
/// </summary>
[AuthorityRoute("siege.assault", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public record NetworkRequestSiegeAssault : ICommand
{
    [ProtoMember(1)]
    public string PartyId { get; }
    [ProtoMember(2)]
    public string SettlementId { get; }
    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestSiegeAssault(
        string partyId,
        string settlementId,
        AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Header = header;
    }
}
