using Common.Messaging;
using ProtoBuf;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Client -&gt; Server] Asks the server to add the given party to an existing battle on the given side. The server
/// performs the authoritative add (<c>PartyBase.MapEventSide</c> setter -&gt; <c>MapEventSide.AddPartyInternal</c>),
/// which replicates the new battle party to all clients through the existing map-event sync.
/// </summary>
[AuthorityRoute("battle.join", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestJoinBattle : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly string MapEventId;
    [ProtoMember(3)]
    public readonly string PartyId;
    [ProtoMember(4)]
    public readonly BattleSideEnum Side;
    [ProtoMember(5)] public readonly int ProtocolVersion;
    [ProtoMember(6)] public readonly string SessionId;
    [ProtoMember(7)] public readonly long ExpectedRevision;
    [ProtoMember(8)] public readonly long AuthorityRequestId;

    public NetworkRequestJoinBattle(string requestId, string mapEventId, string partyId, BattleSideEnum side)
    {
        RequestId = requestId;
        MapEventId = mapEventId;
        PartyId = partyId;
        Side = side;
        ProtocolVersion = 0;
        SessionId = null;
        ExpectedRevision = 0;
        AuthorityRequestId = 0;
    }

    public NetworkRequestJoinBattle(AuthorityRequestHeader header, string mapEventId, string partyId,
        BattleSideEnum side)
    {
        RequestId = header.RequestId.ToString();
        MapEventId = mapEventId;
        PartyId = partyId;
        Side = side;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        ExpectedRevision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
