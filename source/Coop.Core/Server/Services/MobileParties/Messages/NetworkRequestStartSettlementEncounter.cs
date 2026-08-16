using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Server.Services.MobileParties.Messages;

/// <summary>Client intent to start an encounter at a settlement; the server derives the party from the peer.</summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("settlement.encounter.start", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestStartSettlementEncounter : ICommand
{
    [ProtoMember(1)]
    public readonly string SettlementId;

    [ProtoMember(2)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestStartSettlementEncounter(string settlementId, AuthorityRequestHeader header = default)
    {
        SettlementId = settlementId;
        Header = header;
    }

}
