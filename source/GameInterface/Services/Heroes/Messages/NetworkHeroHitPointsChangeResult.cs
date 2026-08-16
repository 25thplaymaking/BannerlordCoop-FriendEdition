using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Heroes.Messages;

/// <summary>Correlated terminal result for the owner-scoped hero health route.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkHeroHitPointsChangeResult : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string HeroId;
    [ProtoMember(7)] public readonly int HitPoints;

    public NetworkHeroHitPointsChangeResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        string heroId, int hitPoints, string reasonCode)
    {
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
        HeroId = heroId;
        HitPoints = hitPoints;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
