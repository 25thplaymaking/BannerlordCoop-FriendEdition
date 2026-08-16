using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.AuthorityRequests;

namespace GameInterface.Services.MapEvents.Messages.Conversation;

/// <summary>
/// Client to Server notification that this client's player encounter finished (or an approved one failed to
/// start). The server releases the AI party held for that request, if any; the sender is identified by its peer.
/// </summary>
[AuthorityRoute("map-event.conversation.end", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkConversationEnded : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly AuthorityRequestHeader Header;

    public NetworkConversationEnded(string requestId = null, AuthorityRequestHeader header = default)
    {
        RequestId = requestId;
        Header = header;
    }
}
