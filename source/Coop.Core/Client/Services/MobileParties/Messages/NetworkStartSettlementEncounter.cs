using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Client.Services.MobileParties.Messages;

internal enum SettlementEncounterStartMode
{
    EnteredSettlement,
    EncounterOnly,
}

/// <summary>Correlated authoritative result for starting the requester's settlement encounter.</summary>
[ProtoContract(SkipConstructor = true)]
internal class NetworkStartSettlementEncounter : ICommand
{
    [ProtoMember(1)]
    public string SettlementId;

    [ProtoMember(2)]
    public string PartyId;

    [ProtoMember(3)]
    public AuthorityResultHeader Header;

    [ProtoMember(4)]
    public SettlementEncounterStartMode Mode;

    public NetworkStartSettlementEncounter(
        string partyId,
        string settlementId,
        SettlementEncounterStartMode mode,
        AuthorityResultHeader header)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Mode = mode;
        Header = header;
    }
}
