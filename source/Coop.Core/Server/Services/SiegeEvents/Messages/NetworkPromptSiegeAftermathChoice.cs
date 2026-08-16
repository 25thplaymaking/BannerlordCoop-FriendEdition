using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

/// <summary>
/// The server parked a player-led siege aftermath; the leading player's client opens the choice menu
/// if its own encounter flow hasn't already.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkPromptSiegeAftermathChoice : IEvent
{
    [ProtoMember(1)] public string SettlementId { get; }
    [ProtoMember(2)] public string LeaderPartyId { get; }
    [ProtoMember(3)] public string AftermathId { get; }
    [ProtoMember(4)] public long Generation { get; }
    [ProtoMember(5)] public string SessionId { get; }

    public NetworkPromptSiegeAftermathChoice(string settlementId, string leaderPartyId,
        string aftermathId = null, long generation = 1, string sessionId = null)
    {
        SettlementId = settlementId;
        LeaderPartyId = leaderPartyId;
        AftermathId = aftermathId;
        Generation = generation;
        SessionId = sessionId;
    }
}
