using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.PlayerCaptivityService.Messages;

[AuthorityRoute("battle.surrender", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkPlayerSurrendered : ICommand
{
    [ProtoMember(1)]
    public readonly string PlayerParty;
    [ProtoMember(2)]
    public readonly string MapEventId;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string SessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ExpectedRevision;

    public NetworkPlayerSurrendered(string mobilePartyId, string mapEventId)
    {
        PlayerParty = mobilePartyId;
        MapEventId = mapEventId;
        ProtocolVersion = 0;
        SessionId = null;
        AuthorityRequestId = 0;
        ExpectedRevision = 0;
    }

    public NetworkPlayerSurrendered(string mapEventId, AuthorityRequestHeader header)
    {
        // The legacy party id is intentionally absent from route traffic. The server derives it
        // from the authenticated peer; accepting a supplied party id was the spoofing boundary.
        PlayerParty = null;
        MapEventId = mapEventId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
