using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Heroes.Messages;

/// <summary>
/// Sent from a client to the server to request a change to a controlled hero's <see cref="TaleWorlds.CampaignSystem.Hero.HitPoints"/>.
/// The server applies it authoritatively; the existing HitPoints property sync then replicates the value
/// back to every client.
/// </summary>
[AuthorityRoute("hero.damage-report", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkHeroHitPointsChangeRequest : ICommand
{
    [ProtoMember(1)]
    public readonly string HeroId;
    [ProtoMember(2)]
    public readonly int HitPoints;
    [ProtoMember(3)] public readonly int ExpectedHitPoints;
    [ProtoMember(4)] public readonly string MapEventId;
    [ProtoMember(5)] public readonly int ProtocolVersion;
    [ProtoMember(6)] public readonly string SessionId;
    [ProtoMember(7)] public readonly long AuthorityRequestId;
    [ProtoMember(8)] public readonly long ExpectedRevision;

    public NetworkHeroHitPointsChangeRequest(string heroId, int hitPoints)
    {
        HeroId = heroId;
        HitPoints = hitPoints;
        ExpectedHitPoints = 0;
        MapEventId = null;
        ProtocolVersion = 0;
        SessionId = null;
        AuthorityRequestId = 0;
        ExpectedRevision = 0;
    }

    public NetworkHeroHitPointsChangeRequest(string heroId, int hitPoints, int expectedHitPoints,
        string mapEventId, AuthorityRequestHeader header)
    {
        HeroId = heroId;
        HitPoints = hitPoints;
        ExpectedHitPoints = expectedHitPoints;
        MapEventId = mapEventId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
