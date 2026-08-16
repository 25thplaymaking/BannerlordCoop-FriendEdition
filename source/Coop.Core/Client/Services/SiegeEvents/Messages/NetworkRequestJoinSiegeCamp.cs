using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.SiegeEvents.Messages;

/// <summary>
/// Client asks the server to add its party to an ongoing siege camp.
/// </summary>
[AuthorityRoute("siege.join-camp", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public record NetworkRequestJoinSiegeCamp : ICommand
{
    [ProtoMember(1)]
    public string PartyId { get; }
    [ProtoMember(2)]
    public string SettlementId { get; }
    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestJoinSiegeCamp(string partyId, string settlementId, AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Header = header;
    }
}
