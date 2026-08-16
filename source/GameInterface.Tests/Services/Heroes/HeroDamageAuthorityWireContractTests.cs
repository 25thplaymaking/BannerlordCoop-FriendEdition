using Common.Messaging;
using GameInterface.Services.Heroes.Messages;
using ProtoBuf.Meta;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.Heroes;

public sealed class HeroDamageAuthorityWireContractTests
{
    [Fact]
    public void DamageReport_RoundTripPreservesCommandRouteOwnerAndDamageEvidence()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 201, 23);
        var request = RoundTrip(new NetworkHeroHitPointsChangeRequest(
            "hero-a", hitPoints: 24, expectedHitPoints: 57, "map-event-a", header));
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkHeroHitPointsChangeRequest), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("hero.damage-report", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(header.ProtocolVersion, request.Header.ProtocolVersion);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
        Assert.Equal("hero-a", request.HeroId);
        Assert.Equal("map-event-a", request.MapEventId);
        Assert.Equal(57, request.ExpectedHitPoints);
        Assert.Equal(24, request.HitPoints);
        Assert.True(request.HitPoints < request.ExpectedHitPoints);
    }

    [Fact]
    public void DamageResult_RoundTripPreservesExactRequestCorrelationAndCommittedHealth()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 202, 24);
        var result = RoundTrip(new NetworkHeroHitPointsChangeResult(
            header, AuthorityResultStatus.Accepted, "hero-a", 19, null));

        Assert.Equal(header.SessionId, result.Header.SessionId);
        Assert.Equal(header.RequestId, result.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, result.Header.CommittedRevision);
        Assert.Equal(AuthorityResultStatus.Accepted, result.Header.Status);
        Assert.Equal("hero-a", result.HeroId);
        Assert.Equal(19, result.HitPoints);
    }

    private static T RoundTrip<T>(T value)
    {
        using var stream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(stream, value);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        return (T)RuntimeTypeModel.Default.Deserialize(stream, null, typeof(T));
    }
}
