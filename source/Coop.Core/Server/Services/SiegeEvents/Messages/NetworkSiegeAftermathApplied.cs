using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.AuthorityRequests;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

/// <summary>
/// The aftermath the server actually applied for a captured settlement, so client menus narrate the
/// right choice.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkSiegeAftermathApplied : IEvent
{
    [ProtoMember(1)] public string SettlementId { get; }
    [ProtoMember(2)] public int AftermathType { get; }
    // Default for the state broadcast. The router terminal reply alone has a valid result header.
    [ProtoMember(3)] public AuthorityResultHeader Header { get; }
    [ProtoMember(4)] public string SessionId { get; }
    [ProtoMember(5)] public long AuthorityRequestId { get; }
    [ProtoMember(6)] public string AftermathId { get; }
    [ProtoMember(7)] public string LeaderPartyId { get; }
    [ProtoMember(8)] public long Generation { get; }

    public NetworkSiegeAftermathApplied(string settlementId, int aftermathType,
        string sessionId = null, long authorityRequestId = 0, string aftermathId = null,
        string leaderPartyId = null, long generation = 1, AuthorityResultHeader header = default)
    {
        SettlementId = settlementId;
        AftermathType = aftermathType;
        Header = header;
        SessionId = sessionId;
        AuthorityRequestId = authorityRequestId;
        AftermathId = aftermathId;
        LeaderPartyId = leaderPartyId;
        Generation = generation;
    }
}
