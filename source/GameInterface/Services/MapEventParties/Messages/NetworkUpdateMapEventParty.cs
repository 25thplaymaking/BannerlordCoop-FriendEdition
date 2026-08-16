using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEventParties.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkUpdateMapEventParty : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventPartyId;
    [ProtoMember(2)]
    public readonly FlattenedTroop[] FlattenedTroops;
    [ProtoMember(3)] public readonly string MapEventId;
    [ProtoMember(4)] public readonly int HostEpoch;
    [ProtoMember(5)] public readonly string SessionId;
    [ProtoMember(6)] public readonly long AuthorityRequestId;
    [ProtoMember(7)] public readonly string RosterFingerprint;

    public NetworkUpdateMapEventParty(string mapEventPartyId, FlattenedTroop[] flattenedTroops)
        : this(mapEventPartyId, flattenedTroops, null, 0, default, null)
    {
    }

    public NetworkUpdateMapEventParty(string mapEventPartyId, FlattenedTroop[] flattenedTroops,
        string mapEventId, int hostEpoch, AuthorityRequestHeader requestHeader, string rosterFingerprint)
    {
        MapEventPartyId = mapEventPartyId;
        FlattenedTroops = flattenedTroops;
        MapEventId = mapEventId;
        HostEpoch = hostEpoch;
        SessionId = requestHeader.SessionId;
        AuthorityRequestId = requestHeader.RequestId;
        RosterFingerprint = rosterFingerprint;
    }
}
