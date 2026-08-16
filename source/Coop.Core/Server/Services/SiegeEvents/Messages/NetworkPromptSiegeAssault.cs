using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

/// <summary>
/// A siege assault started; a besieging client adopts the replicated assault map event as its
/// player encounter so it can enter the wall-assault mission.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkPromptSiegeAssault : IEvent
{
    [ProtoMember(1)]
    public string AttackerPartyId { get; }
    [ProtoMember(2)]
    public string SettlementId { get; }
    /// <summary>Present only for the requester-specific canonical-state acknowledgement.</summary>
    [ProtoMember(3)]
    public AuthorityResultHeader Header { get; }
    [ProtoMember(4)]
    public string RequestingPartyId { get; }
    [ProtoMember(5)]
    public string MapEventId { get; }

    public NetworkPromptSiegeAssault(
        string attackerPartyId,
        string settlementId,
        AuthorityResultHeader header = default,
        string requestingPartyId = null,
        string mapEventId = null)
    {
        AttackerPartyId = attackerPartyId;
        SettlementId = settlementId;
        Header = header;
        RequestingPartyId = requestingPartyId;
        MapEventId = mapEventId;
    }
}
