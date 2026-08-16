using Common.Messaging;
using ProtoBuf;
using System;

namespace GameInterface.Services.WorkshopMods.Core;

[AuthorityRoute("bootstrap.workshop-capabilities", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestWorkshopCapabilities : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long Revision;
    [ProtoMember(4)] public readonly long AuthorityRequestId;

    public NetworkRequestWorkshopCapabilities(WorkshopCapabilitySnapshot current)
    {
        ProtocolVersion = WorkshopCapabilitySnapshot.CurrentProtocolVersion;
        SessionId = current?.SessionId;
        Revision = current?.Revision ?? 0;
        AuthorityRequestId = 0;
    }

    public NetworkRequestWorkshopCapabilities(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        Revision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, Revision);

    internal bool TryValidateWireShape(out string failure)
    {
        if (ProtocolVersion != WorkshopCapabilitySnapshot.CurrentProtocolVersion)
        {
            failure = "Unsupported Workshop capability request protocol.";
            return false;
        }
        if (Revision < 0 || SessionId == null || SessionId.Length != 32 ||
            !Guid.TryParseExact(SessionId, "N", out _))
        {
            failure = "Malformed Workshop capability request identity.";
            return false;
        }
        failure = null;
        return true;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkWorkshopCapabilities : IEvent
{
    [ProtoMember(1)] public readonly WorkshopCapabilitySnapshot Snapshot;

    public NetworkWorkshopCapabilities(WorkshopCapabilitySnapshot snapshot)
        => Snapshot = snapshot;
}

/// <summary>Correlated response to a capability refresh; broadcasts retain their uncorrelated type.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkWorkshopCapabilityQueryResult : IEvent
{
    [ProtoMember(1)] public readonly WorkshopCapabilitySnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkWorkshopCapabilityQueryResult(
        AuthorityRequestHeader request,
        AuthorityResultStatus status,
        WorkshopCapabilitySnapshot snapshot,
        string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
