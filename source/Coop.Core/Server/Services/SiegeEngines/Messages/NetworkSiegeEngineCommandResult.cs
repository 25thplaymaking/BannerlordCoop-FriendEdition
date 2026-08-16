using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEngines.Messages;

public enum SiegeEngineCommandKind
{
    Deploy = 0,
    Remove = 1,
}

/// <summary>Correlates a server-approved engine order to its exact canonical slot delta.</summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkSiegeEngineCommandResult : IEvent
{
    [ProtoMember(1)] public AuthorityResultHeader Header { get; }
    [ProtoMember(2)] public SiegeEngineCommandKind Kind { get; }
    [ProtoMember(3)] public string SiegeEventId { get; }
    [ProtoMember(4)] public string ContainerId { get; }
    [ProtoMember(5)] public int Side { get; }
    [ProtoMember(6)] public int Index { get; }
    [ProtoMember(7)] public bool IsRanged { get; }
    [ProtoMember(8)] public string EngineTypeId { get; }
    [ProtoMember(9)] public string SiegeEngineId { get; }
    [ProtoMember(10)] public long SlotRevision { get; }
    [ProtoMember(11)] public string RevisionEpoch { get; }

    public NetworkSiegeEngineCommandResult(
        AuthorityResultHeader header,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        string containerId,
        int side,
        int index,
        bool isRanged,
        string engineTypeId,
        string siegeEngineId,
        long slotRevision,
        string revisionEpoch)
    {
        Header = header;
        Kind = kind;
        SiegeEventId = siegeEventId;
        ContainerId = containerId;
        Side = side;
        Index = index;
        IsRanged = isRanged;
        EngineTypeId = engineTypeId;
        SiegeEngineId = siegeEngineId;
        SlotRevision = slotRevision;
        RevisionEpoch = revisionEpoch;
    }
}
