using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Leave;

// [Server -> All] Apply a party's authoritative removal from its battle participation.
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkPartyLeftBattle : ICommand
{
    [ProtoMember(1)]
    public readonly string PartyId;
    [ProtoMember(2)]
    public readonly bool LeaveSiege;
    [ProtoMember(3)]
    public readonly bool FinishLocalMenus;
    [ProtoMember(4)] public readonly string SessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly string MapEventId;

    public NetworkPartyLeftBattle(
        string partyId,
        bool leaveSiege = false,
        bool finishLocalMenus = true)
    {
        PartyId = partyId;
        LeaveSiege = leaveSiege;
        FinishLocalMenus = finishLocalMenus;
        SessionId = null;
        AuthorityRequestId = 0;
        MapEventId = null;
    }

    public NetworkPartyLeftBattle(string partyId, bool leaveSiege, bool finishLocalMenus,
        string sessionId, long authorityRequestId, string mapEventId)
    {
        PartyId = partyId;
        LeaveSiege = leaveSiege;
        FinishLocalMenus = finishLocalMenus;
        SessionId = sessionId;
        AuthorityRequestId = authorityRequestId;
        MapEventId = mapEventId;
    }
}
