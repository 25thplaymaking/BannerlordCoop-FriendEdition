using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Leave;

// [Client -> Server] Remove this party from its map event side, leaving the battle running.
[AuthorityRoute("battle.leave", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestLeaveBattle : ICommand
{
    [ProtoMember(1)]
    public readonly string PartyId;
    [ProtoMember(2)]
    public readonly bool FinishLocalMenus;
    [ProtoMember(3)] public readonly string MapEventId;
    [ProtoMember(4)] public readonly int ProtocolVersion;
    [ProtoMember(5)] public readonly string SessionId;
    [ProtoMember(6)] public readonly long ExpectedRevision;
    [ProtoMember(7)] public readonly long AuthorityRequestId;

    public NetworkRequestLeaveBattle(string partyId, bool finishLocalMenus = true)
    {
        PartyId = partyId;
        FinishLocalMenus = finishLocalMenus;
        MapEventId = null;
        ProtocolVersion = 0;
        SessionId = null;
        ExpectedRevision = 0;
        AuthorityRequestId = 0;
    }

    public NetworkRequestLeaveBattle(AuthorityRequestHeader header, string partyId, string mapEventId,
        bool finishLocalMenus)
    {
        PartyId = partyId;
        FinishLocalMenus = finishLocalMenus;
        MapEventId = mapEventId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        ExpectedRevision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
