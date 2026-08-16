using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.BattleRetreat.Messages;

/// <summary>
/// Client asks the server to apply its break-in losses. Carries no casualty data - the server decides.
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("battle.break-in-casualties", AuthorityRouteKind.Command)]
public record NetworkRequestBreakInCasualties : ICommand
{
    [ProtoMember(1)]
    public string PartyId { get; }

    [ProtoMember(2)]
    public string SettlementId { get; }

    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestBreakInCasualties(
        string partyId,
        string settlementId,
        AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Header = header;
    }
}
