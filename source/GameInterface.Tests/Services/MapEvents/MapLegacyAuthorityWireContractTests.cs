using Common.Messaging;
using GameInterface.Services.MapEventParties;
using GameInterface.Services.MapEventParties.Handlers;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages.Leave;
using ProtoBuf.Meta;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public sealed class MapLegacyAuthorityWireContractTests
{
    [Fact]
    public void FinalizeRequestAndCertificate_PreserveExactRevisionHostEpochAndCorrelation()
    {
        var header = Header(701);
        var request = RoundTrip(new NetworkMapEventFinalizeAttempted(header, "map-event-a", 4));
        var result = RoundTrip(new NetworkMapEventFinalized(header, AuthorityResultStatus.Accepted,
            "map-event-a", 4, finalized: true, reasonCode: null));

        AssertRoute<NetworkMapEventFinalizeAttempted>("map-event.finalize", AuthorityRouteKind.Command);
        AssertHeader(header, request.Header);
        Assert.Equal("map-event-a", request.MapEventId);
        Assert.Equal(4, request.HostEpoch);
        Assert.Equal(header.SessionId, result.Header.SessionId);
        Assert.Equal(header.RequestId, result.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, result.Header.CommittedRevision);
        Assert.Equal("map-event-a", result.MapEventId);
        Assert.Equal(4, result.HostEpoch);
        Assert.True(result.Finalized);
    }

    [Fact]
    public void MapEventPartySnapshot_UsesBootstrapRouteAndSeparateCorrelatedReplication()
    {
        var header = Header(702);
        var troop = new FlattenedTroop("troop-a", false, 17, RosterTroopState.Wounded, 9, 3);
        var troops = new[] { troop };
        var fingerprint = MapEventPartyHandler.ComputeRosterFingerprint(troops);
        var request = RoundTrip(new NetworkRequestMapEventPartyUpdate(header, "map-event-party-a", 5));
        var replication = RoundTrip(new NetworkUpdateMapEventParty(
            "map-event-party-a", troops, "map-event-a", 5, header, fingerprint));
        var result = RoundTrip(new NetworkMapEventPartyUpdateResult(header, AuthorityResultStatus.Accepted,
            "map-event-a", "map-event-party-a", 5, fingerprint, null));

        AssertRoute<NetworkRequestMapEventPartyUpdate>(
            "map-event-party.snapshot", AuthorityRouteKind.BootstrapQuery);
        Assert.Null(Attribute.GetCustomAttribute(typeof(NetworkUpdateMapEventParty),
            typeof(AuthorityRouteAttribute)));
        AssertHeader(header, request.Header);
        Assert.Equal(5, request.HostEpoch);
        Assert.Equal(header.SessionId, replication.SessionId);
        Assert.Equal(header.RequestId, replication.AuthorityRequestId);
        Assert.Equal("map-event-a", replication.MapEventId);
        Assert.Equal(fingerprint, replication.RosterFingerprint);
        Assert.Equal(fingerprint, result.RosterFingerprint);
        Assert.Equal(header.RequestId, result.Header.RequestId);
    }

    [Fact]
    public void UnsafeRemoteMutationSubscribers_AreAbsent()
    {
        var partyMethods = typeof(MapEventPartyHandler).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var raidMethods = typeof(RaidAiInterventionConfigHandler).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(partyMethods,
            method => method.Name == "Handle_NetworkRequestMapEventPartyUpdate");
        Assert.DoesNotContain(raidMethods,
            method => method.Name == "Handle_NetworkRequestRaidAiInterventionConfigChange");
    }

    private static AuthorityRequestHeader Header(long requestId) =>
        new(1, "0123456789abcdef0123456789abcdef", requestId, 41);

    private static void AssertRoute<TRequest>(string routeId, AuthorityRouteKind kind)
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(TRequest), typeof(AuthorityRouteAttribute));
        Assert.NotNull(attribute);
        Assert.Equal(routeId, attribute.RouteId);
        Assert.Equal(kind, attribute.Kind);
    }

    private static void AssertHeader(AuthorityRequestHeader expected, AuthorityRequestHeader actual)
    {
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.ExpectedRevision, actual.ExpectedRevision);
    }

    private static T RoundTrip<T>(T value)
    {
        using var stream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(stream, value);
        stream.Position = 0;
        return (T)RuntimeTypeModel.Default.Deserialize(stream, null, typeof(T));
    }
}
