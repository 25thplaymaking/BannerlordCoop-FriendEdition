using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.Tournaments.Messages;
using GameInterface.Services.Villages.Data;
using GameInterface.Services.Villages.Messages;
using System;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

public sealed class AuthorityRouteContractTests
{
    [Fact]
    public void MapEventRequest_DeclaresTheRegisteredCommandRoute()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestCreateMapEvent), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("map-event.create", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
    }

    [Fact]
    public void BattleStartRequest_DeclaresTheRegisteredCommandRoute()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkBattleStartRequest), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("map-event.battle-start", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
    }

    [Fact]
    public void VillageHostileActionRequest_DeclaresTheRegisteredCommandRouteAndRetainsItsHeader()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestVillageHostileAction), typeof(AuthorityRouteAttribute));
        var header = new AuthorityRequestHeader(1, "session", 9, 4);
        var request = new NetworkRequestVillageHostileAction(header, VillageHostileAction.Raid, "party", "village");

        Assert.NotNull(attribute);
        Assert.Equal("village.hostile-action", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
    }

    [Fact]
    public void TournamentStateRequest_DeclaresBootstrapRouteAndRetainsConfigCorrelation()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestTournamentState), typeof(AuthorityRouteAttribute));
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 27, 3);
        var request = new NetworkRequestTournamentState(header);

        Assert.NotNull(attribute);
        Assert.Equal("tournament.state", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.BootstrapQuery, attribute.Kind);
        Assert.True(request.TryValidateWireShape(out _));
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
    }

    [Fact]
    public void TournamentStateResult_RetainsExactRequestCorrelationAndRequestedEpoch()
    {
        var request = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 28, 4);
        var result = new NetworkTournamentStateQueryResult(
            request,
            AuthorityResultStatus.Accepted,
            default,
            stateEpoch: 9,
            reasonCode: null);

        Assert.Equal(request.SessionId, result.Header.SessionId);
        Assert.Equal(request.RequestId, result.Header.RequestId);
        Assert.Equal(request.ExpectedRevision, result.Header.CommittedRevision);
        Assert.Equal(9, result.StateEpoch);
    }

    [Fact]
    public void Router_RefusesARouteWhoseTypedMessageDoesNotDeclareTheSameIdentity()
    {
        using var broker = new MessageBroker();
        using var router = new AuthorityRequestRouter(broker, new Coop.Tests.Mocks.TestNetwork(),
            new Moq.Mock<global::GameInterface.Services.Players.IPlayerManager>().Object);

        var route = AuthorityRoute<string, TestRequest, TestResult>.Define(
            "wrong.route", AuthorityRouteKind.Command,
            id => new AuthorityRequestHeader(1, "session", id, 0), (intent, header) => new TestRequest(header, intent),
            request => request.Header, result => result.Header, _ => null, request => request.Intent,
            _ => AuthorityHeaderValidation.Valid,
            (_, request) => new AuthorityServerReply<TestResult>(new TestResult(new AuthorityResultHeader(
                request.Header.SessionId, request.Header.RequestId, AuthorityResultStatus.Rejected, 0, "denied")), false),
            (header, status, reason) => new TestResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason)),
            _ => AuthorityCommitProbeResult.Applied, _ => { }, _ => { }, _ => true, AuthorityTimeoutPolicy.CampaignMutation);

        Assert.Throws<InvalidOperationException>(() => router.Register(route));
    }
}
