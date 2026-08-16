using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.Locations.Messages.Conversation;

/// <summary>
/// Client -> Server request to start a conversation with a settlement-location NPC. The server replies
/// with <see cref="NetworkAllowLocationConversation"/> when the NPC is free, or
/// <see cref="NetworkLocationConversationDenied"/> when another player already holds it. The reply echoes
/// <see cref="Generation"/> so the client can ignore a stale reply for a request it has since abandoned.
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("location.conversation.begin", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestLocationConversation : ICommand
{
    [ProtoMember(1)]
    public readonly string LocationId;
    [ProtoMember(2)]
    public readonly string CharacterId;
    [ProtoMember(3)]
    public readonly int Generation;
    [ProtoMember(4)] public readonly AuthorityRequestHeader Header;

    public NetworkRequestLocationConversation(string locationId, string characterId, int generation, AuthorityRequestHeader header = default)
    {
        LocationId = locationId;
        CharacterId = characterId;
        Generation = generation;
        Header = header;
    }
}
