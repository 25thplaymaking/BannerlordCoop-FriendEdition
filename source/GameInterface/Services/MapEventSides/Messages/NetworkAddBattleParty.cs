using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEventSides.Messages;

[ProtoContract]
public readonly struct NetworkAddBattleParty : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventSideId;
    [ProtoMember(2)]
    public readonly string MapEventPartyId;
    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly string MapEventId;
    [ProtoMember(6)] public readonly string PartyId;
    [ProtoMember(7)] public readonly int Side;

    public NetworkAddBattleParty(string mapEventSideId, string mapEventPartyId)
    {
        MapEventSideId = mapEventSideId;
        MapEventPartyId = mapEventPartyId;
        SessionId = null;
        AuthorityRequestId = 0;
        MapEventId = null;
        PartyId = null;
        Side = -1;
    }

    public NetworkAddBattleParty(string mapEventSideId, string mapEventPartyId, string sessionId,
        long authorityRequestId, string mapEventId, string partyId, int side)
    {
        MapEventSideId = mapEventSideId;
        MapEventPartyId = mapEventPartyId;
        SessionId = sessionId;
        AuthorityRequestId = authorityRequestId;
        MapEventId = mapEventId;
        PartyId = partyId;
        Side = side;
    }
}
