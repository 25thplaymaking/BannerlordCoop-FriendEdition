using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.AuthorityRequests;

namespace Coop.Core.Client.Services.SiegeEvents.Messages;

/// <summary>
/// Client asks the server to apply its siege aftermath choice (devastate, pillage or show mercy).
/// </summary>
[AuthorityRoute("siege.aftermath", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public record NetworkRequestSiegeAftermath : ICommand
{
    [ProtoMember(1)] public string PartyId { get; }
    [ProtoMember(2)] public string SettlementId { get; }
    [ProtoMember(3)] public int AftermathType { get; }
    [ProtoMember(4)] public string AftermathId { get; }
    [ProtoMember(5)] public AuthorityRequestHeader Header { get; }

    public NetworkRequestSiegeAftermath(string partyId, string settlementId, int aftermathType,
        string aftermathId = null, AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        AftermathType = aftermathType;
        AftermathId = aftermathId;
        Header = header;
    }
}
