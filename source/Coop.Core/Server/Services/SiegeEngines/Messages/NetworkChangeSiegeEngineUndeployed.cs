using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEngines.Messages;

/// <summary>
/// Notify clients a deployed siege engine was removed from its slot.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkChangeSiegeEngineUndeployed : IEvent
{
    [ProtoMember(1)]
    public string ContainerId { get; }
    [ProtoMember(2)]
    public int Index { get; }
    [ProtoMember(3)]
    public bool IsRanged { get; }
    [ProtoMember(4)]
    public bool MoveToReserve { get; }
    [ProtoMember(5)]
    public long SlotRevision { get; }
    [ProtoMember(6)]
    public string RevisionEpoch { get; }
    [ProtoMember(7)]
    public string AuthoritySessionId { get; }
    [ProtoMember(8)]
    public long AuthorityRequestId { get; }

    public NetworkChangeSiegeEngineUndeployed(
        string containerId,
        int index,
        bool isRanged,
        bool moveToReserve,
        long slotRevision,
        string revisionEpoch,
        string authoritySessionId = null,
        long authorityRequestId = 0)
    {
        ContainerId = containerId;
        Index = index;
        IsRanged = isRanged;
        MoveToReserve = moveToReserve;
        SlotRevision = slotRevision;
        RevisionEpoch = revisionEpoch;
        AuthoritySessionId = authoritySessionId;
        AuthorityRequestId = authorityRequestId;
    }
}
