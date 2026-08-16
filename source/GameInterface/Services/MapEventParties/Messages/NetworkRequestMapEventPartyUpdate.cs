using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEventParties.Messages;

[AuthorityRoute("map-event-party.snapshot", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestMapEventPartyUpdate : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventPartyId;
    [ProtoMember(2)] public readonly int ProtocolVersion;
    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long ExpectedRevision;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly int HostEpoch;

    public NetworkRequestMapEventPartyUpdate(AuthorityRequestHeader header, string mapEventPartyId, int hostEpoch)
    {
        MapEventPartyId = mapEventPartyId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        ExpectedRevision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
        HostEpoch = hostEpoch;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
