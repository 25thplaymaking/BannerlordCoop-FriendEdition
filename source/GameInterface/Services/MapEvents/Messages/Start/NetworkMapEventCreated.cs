using Common.Messaging;
using GameInterface.Services.MapEvents;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// Server -&gt; Client response carrying the object-manager id of the authoritatively created
/// <see cref="TaleWorlds.CampaignSystem.MapEvents.MapEvent"/>, correlated to the original request by <see cref="RequestId"/>.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMapEventCreated : IEvent
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly MapEventCreationOutcome Outcome;
    [ProtoMember(3)]
    public readonly string MapEventId;
    [ProtoMember(4)]
    public readonly int ProtocolVersion;
    [ProtoMember(5)]
    public readonly string SessionId;
    [ProtoMember(6)]
    public readonly long AuthorityRequestId;
    [ProtoMember(7)]
    public readonly AuthorityResultStatus Status;
    [ProtoMember(8)]
    public readonly long CommittedRevision;
    [ProtoMember(9)]
    public readonly string ReasonCode;
    [ProtoMember(10)]
    public readonly string AttackerId;
    [ProtoMember(11)]
    public readonly string DefenderId;

    public NetworkMapEventCreated(
        string requestId,
        MapEventCreationOutcome outcome,
        string mapEventId)
    {
        RequestId = requestId;
        Outcome = outcome;
        MapEventId = mapEventId;
        ProtocolVersion = 0;
        SessionId = null;
        AuthorityRequestId = 0;
        Status = AuthorityResultStatus.InvalidRequest;
        CommittedRevision = 0;
        ReasonCode = null;
        AttackerId = null;
        DefenderId = null;
    }

    public NetworkMapEventCreated(
        AuthorityRequestHeader requestHeader,
        AuthorityResultStatus status,
        MapEventCreationOutcome outcome,
        string mapEventId,
        string reasonCode,
        string attackerId,
        string defenderId,
        long committedRevision)
    {
        RequestId = requestHeader.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Outcome = outcome;
        MapEventId = mapEventId;
        ProtocolVersion = requestHeader.ProtocolVersion;
        SessionId = requestHeader.SessionId;
        AuthorityRequestId = requestHeader.RequestId;
        Status = status;
        CommittedRevision = committedRevision;
        ReasonCode = reasonCode;
        AttackerId = attackerId;
        DefenderId = defenderId;
    }

    public AuthorityResultHeader Header => new AuthorityResultHeader(
        SessionId,
        AuthorityRequestId,
        Status,
        CommittedRevision,
        ReasonCode);
}
