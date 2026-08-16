using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.Locations.Messages.Conversation;

/// <summary>
/// Client -> Server notification that this client's location conversation finished (or an approved one
/// failed to start). The server releases the NPC held for that player, if any; the sender is identified
/// by its peer, so no payload is needed.
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("location.conversation.end", AuthorityRouteKind.Command)]
internal readonly struct NetworkLocationConversationEnded : ICommand
{
    [ProtoMember(1)] public readonly string LeaseId;
    [ProtoMember(2)] public readonly AuthorityRequestHeader Header;
    public NetworkLocationConversationEnded(string leaseId = null, AuthorityRequestHeader header = default)
    { LeaseId = leaseId; Header = header; }
}
