using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Client.Services.MobileParties.Messages;

internal enum SettlementEncounterLeaveOutcome
{
    Applied,
    Suppressed,
    AlreadyOutside,
}

/// <summary>Correlated authoritative result for ending the requester's settlement encounter.</summary>
[ProtoContract(SkipConstructor = true)]
internal class NetworkSettlementEncounterLeaveResult : ICommand
{
    [ProtoMember(1)]
    public readonly string PartyId;

    [ProtoMember(2)]
    public readonly SettlementEncounterLeaveOutcome Outcome;

    [ProtoMember(3)]
    public readonly string SettlementId;

    [ProtoMember(4)]
    public readonly AuthorityResultHeader Header;

    public NetworkSettlementEncounterLeaveResult(
        string partyId,
        string settlementId,
        SettlementEncounterLeaveOutcome outcome,
        AuthorityResultHeader header)
    {
        PartyId = partyId;
        SettlementId = settlementId;
        Outcome = outcome;
        Header = header;
    }

}
