using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.BattleRetreat.Messages;

/// <summary>Correlated terminal result for a party leaving an unresolved battle mission.</summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkBattleMissionRetreatResolved : IEvent
{
    [ProtoMember(1)]
    public string PartyId { get; }
    [ProtoMember(2)]
    public string MapEventId { get; }
    [ProtoMember(3)]
    public bool Approved { get; }
    [ProtoMember(4)]
    public AuthorityResultHeader Header { get; }

    public NetworkBattleMissionRetreatResolved(
        string partyId,
        string mapEventId,
        bool approved,
        AuthorityResultHeader header)
    {
        PartyId = partyId;
        MapEventId = mapEventId;
        Approved = approved;
        Header = header;
    }
}
