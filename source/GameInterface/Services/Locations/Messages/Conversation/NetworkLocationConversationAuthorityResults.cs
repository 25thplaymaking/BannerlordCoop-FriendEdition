using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.Locations.Messages.Conversation;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLocationConversationBeginResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string LeaseId;
    [ProtoMember(3)] public readonly long LeaseRevision;
    [ProtoMember(4)] public readonly string LocationId;
    [ProtoMember(5)] public readonly string OwnerCharacterId;
    [ProtoMember(6)] public readonly string TargetCharacterId;
    [ProtoMember(7)] public readonly int Generation;
    public NetworkLocationConversationBeginResult(AuthorityResultHeader header, string leaseId, long leaseRevision,
        string locationId, string ownerCharacterId, string targetCharacterId, int generation)
    { Header = header; LeaseId = leaseId; LeaseRevision = leaseRevision; LocationId = locationId;
      OwnerCharacterId = ownerCharacterId; TargetCharacterId = targetCharacterId; Generation = generation; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLocationConversationEndResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string LeaseId;
    [ProtoMember(3)] public readonly long LeaseRevision;
    public NetworkLocationConversationEndResult(AuthorityResultHeader header, string leaseId, long leaseRevision)
    { Header = header; LeaseId = leaseId; LeaseRevision = leaseRevision; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLocationConversationLeaseState : IMessage
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly string LeaseId;
    [ProtoMember(3)] public readonly long Revision;
    [ProtoMember(4)] public readonly bool IsActive;
    [ProtoMember(5)] public readonly string LocationId;
    [ProtoMember(6)] public readonly string OwnerCharacterId;
    [ProtoMember(7)] public readonly string TargetCharacterId;
    public NetworkLocationConversationLeaseState(string sessionId, string leaseId, long revision, bool isActive,
        string locationId, string ownerCharacterId, string targetCharacterId)
    { SessionId=sessionId; LeaseId=leaseId; Revision=revision; IsActive=isActive; LocationId=locationId;
      OwnerCharacterId=ownerCharacterId; TargetCharacterId=targetCharacterId; }
}
