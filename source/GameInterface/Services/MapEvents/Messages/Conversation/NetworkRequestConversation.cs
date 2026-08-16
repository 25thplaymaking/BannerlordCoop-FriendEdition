using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.AuthorityRequests;

namespace GameInterface.Services.MapEvents.Messages.Conversation;

/// <summary>
/// Client -&gt; Server request to run <c>PlayerEncounter.RestartPlayerEncounter</c> for the given parties. The server
/// validates it and, if allowed, replies with <see cref="NetworkAllowConversation"/>. Rejected requests receive no
/// response.
/// </summary>
[AuthorityRoute("map-event.conversation.begin", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestConversation : ICommand
{
    [ProtoMember(1)]
    public readonly string DefenderId;
    [ProtoMember(2)]
    public readonly string AttackerId;
    [ProtoMember(3)]
    public readonly bool ForcePlayerOutFromSettlement;
    [ProtoMember(4)]
    public readonly ConversationRestartSource Source;
    [ProtoMember(5)]
    public readonly bool ArmyTalkEncounter;
    [ProtoMember(6)]
    public readonly string RequestId;
    [ProtoMember(7)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestConversation(
        string defenderId,
        string attackerId,
        bool forcePlayerOutFromSettlement,
        ConversationRestartSource source,
        bool armyTalkEncounter,
        string requestId = null,
        AuthorityRequestHeader header = default)
    {
        DefenderId = defenderId;
        AttackerId = attackerId;
        ForcePlayerOutFromSettlement = forcePlayerOutFromSettlement;
        Source = source;
        ArmyTalkEncounter = armyTalkEncounter;
        RequestId = requestId;
        Header = header;
    }
}
