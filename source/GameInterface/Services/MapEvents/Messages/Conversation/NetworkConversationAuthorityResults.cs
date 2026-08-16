using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Conversation;

internal enum ConversationResultKind
{
    Lease = 0,
    PlayerInteraction = 1,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkConversationBeginResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly ConversationResultKind Kind;
    [ProtoMember(3)] public readonly string LeaseId;
    [ProtoMember(4)] public readonly long LeaseRevision;
    [ProtoMember(5)] public readonly string DefenderId;
    [ProtoMember(6)] public readonly string AttackerId;
    [ProtoMember(7)] public readonly bool ForcePlayerOutFromSettlement;
    [ProtoMember(8)] public readonly ConversationRestartSource Source;
    [ProtoMember(9)] public readonly string RestartRequestId;

    public NetworkConversationBeginResult(AuthorityResultHeader header, ConversationResultKind kind, string leaseId,
        long leaseRevision, string defenderId, string attackerId, bool forcePlayerOutFromSettlement,
        ConversationRestartSource source, string restartRequestId)
    { Header = header; Kind = kind; LeaseId = leaseId; LeaseRevision = leaseRevision; DefenderId = defenderId;
      AttackerId = attackerId; ForcePlayerOutFromSettlement = forcePlayerOutFromSettlement; Source = source;
      RestartRequestId = restartRequestId; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkConversationEndResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string LeaseId;
    [ProtoMember(3)] public readonly long LeaseRevision;
    public NetworkConversationEndResult(AuthorityResultHeader header, string leaseId, long leaseRevision)
    { Header = header; LeaseId = leaseId; LeaseRevision = leaseRevision; }
}

/// <summary>Canonical lease replica. Applying it never opens native conversation UI.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkConversationLeaseState : IMessage
{
    [ProtoMember(1)] public readonly string LeaseId;
    [ProtoMember(2)] public readonly long Revision;
    [ProtoMember(3)] public readonly bool IsActive;
    [ProtoMember(4)] public readonly string OwnerPartyId;
    [ProtoMember(5)] public readonly string TargetPartyId;
    public NetworkConversationLeaseState(string leaseId, long revision, bool isActive, string ownerPartyId, string targetPartyId)
    { LeaseId = leaseId; Revision = revision; IsActive = isActive; OwnerPartyId = ownerPartyId; TargetPartyId = targetPartyId; }
}
