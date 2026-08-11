using Common.Messaging;
using ProtoBuf;
using System;

namespace GameInterface.Services.WorkshopMods.Core;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestWorkshopCapabilities : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long Revision;

    public NetworkRequestWorkshopCapabilities(WorkshopCapabilitySnapshot current)
    {
        ProtocolVersion = WorkshopCapabilitySnapshot.CurrentProtocolVersion;
        SessionId = current?.SessionId;
        Revision = current?.Revision ?? 0;
    }

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
