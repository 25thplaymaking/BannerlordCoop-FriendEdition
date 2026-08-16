using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEventParties.Messages;

/// <summary>Correlated result for a server-owned canonical MapEventParty roster snapshot.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMapEventPartyUpdateResult : IEvent
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string MapEventId;
    [ProtoMember(7)] public readonly string MapEventPartyId;
    [ProtoMember(8)] public readonly int HostEpoch;
    [ProtoMember(9)] public readonly string RosterFingerprint;

    public NetworkMapEventPartyUpdateResult(AuthorityRequestHeader header, AuthorityResultStatus status,
        string mapEventId, string mapEventPartyId, int hostEpoch, string rosterFingerprint, string reasonCode)
    {
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        Status = status;
        CommittedRevision = header.ExpectedRevision;
        ReasonCode = reasonCode;
        MapEventId = mapEventId;
        MapEventPartyId = mapEventPartyId;
        HostEpoch = hostEpoch;
        RosterFingerprint = rosterFingerprint;
    }

    public AuthorityResultHeader Header => new AuthorityResultHeader(
        SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
