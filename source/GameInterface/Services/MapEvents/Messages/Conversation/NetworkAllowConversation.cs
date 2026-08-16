using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Conversation;

/// <summary>
/// Server -&gt; Client approval to run <c>PlayerEncounter.RestartPlayerEncounter</c> with the supplied parameters. The
/// client re-runs it for these parties under an <see cref="Common.Util.AllowedThread"/> so the original (now approved)
/// executes.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkAllowConversation : ICommand
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
    public readonly string RequestId;
    [ProtoMember(6)]
    public readonly long LeaseRevision;
    [ProtoMember(7)]
    public readonly string LeaseId;

    public NetworkAllowConversation(
        string defenderId,
        string attackerId,
        bool forcePlayerOutFromSettlement,
        ConversationRestartSource source,
        string requestId,
        long leaseRevision = 0,
        string leaseId = null)
    {
        DefenderId = defenderId;
        AttackerId = attackerId;
        ForcePlayerOutFromSettlement = forcePlayerOutFromSettlement;
        Source = source;
        RequestId = requestId;
        LeaseRevision = leaseRevision;
        LeaseId = leaseId;
    }
}
