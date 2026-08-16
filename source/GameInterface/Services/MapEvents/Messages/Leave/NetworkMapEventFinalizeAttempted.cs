using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Leave;

[AuthorityRoute("map-event.finalize", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMapEventFinalizeAttempted : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;
    [ProtoMember(2)] public readonly int ProtocolVersion;
    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long ExpectedRevision;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly int HostEpoch;

    public NetworkMapEventFinalizeAttempted(AuthorityRequestHeader header, string mapEventId, int hostEpoch)
    {
        MapEventId = mapEventId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        ExpectedRevision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
        HostEpoch = hostEpoch;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
