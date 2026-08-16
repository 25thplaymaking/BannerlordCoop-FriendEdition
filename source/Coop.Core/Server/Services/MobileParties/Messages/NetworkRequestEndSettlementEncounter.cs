using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Server.Services.MobileParties.Messages;

/// <summary>Client intent to end its current settlement encounter; the server derives the party from the peer.</summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("settlement.encounter.end", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestEndSettlementEncounter : ICommand
{
    [ProtoMember(1)]
    public string SettlementId { get; }

    [ProtoMember(2)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestEndSettlementEncounter(string settlementId, AuthorityRequestHeader header = default)
    {
        SettlementId = settlementId;
        Header = header;
    }
}
