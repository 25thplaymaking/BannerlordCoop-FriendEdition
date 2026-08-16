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
    public void TournamentJoinRequest_DeclaresRouteAndSeparatesConfigFromFeatureRevision()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestJoinTournament), typeof(AuthorityRouteAttribute));
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 29, 5);
        var request = new NetworkRequestJoinTournament(header, "town-a", "tournament-a", 17);

        Assert.NotNull(attribute);
        Assert.Equal("tournament.join", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(5, request.Header.ExpectedRevision);
        Assert.Equal(17, request.ExpectedRevision);
        Assert.Equal(29, request.Header.RequestId);
    }

    [Fact]
    public void TournamentLeavePreparationRequest_DeclaresRouteAndRetainsExactHeader()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestLeaveTournamentPreparation), typeof(AuthorityRouteAttribute));
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 30, 6);
        var request = new NetworkRequestLeaveTournamentPreparation(header, "tournament-a", 18);

        Assert.NotNull(attribute);
        Assert.Equal("tournament.leave-preparation", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(18, request.ExpectedRevision);
    }

    [Fact]
    public void TournamentLaunchRequests_DeclareTypedRoutesAndRetainConfigCorrelation()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 31, 7);
        var start = new NetworkRequestStartTournament(header, "tournament-a", 19);
        var spectate = new NetworkRequestSpectateTournament(header, "tournament-a", 20);

        Assert.Equal("tournament.start", ((AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestStartTournament), typeof(AuthorityRouteAttribute))).RouteId);
        Assert.Equal("tournament.spectate", ((AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestSpectateTournament), typeof(AuthorityRouteAttribute))).RouteId);
        Assert.Equal(header.RequestId, start.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, spectate.Header.ExpectedRevision);
        Assert.Equal(19, start.ExpectedRevision);
        Assert.Equal(20, spectate.ExpectedRevision);
    }

    [Fact]
    public void TournamentMissionEnteredRequest_DeclaresRouteAndRetainsLaunchTuple()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 32, 8);
        var request = new NetworkTournamentMissionEntered(header, "tournament-a", 21, "mission-a", true);
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkTournamentMissionEntered), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("tournament.mission-entered", attribute.RouteId);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal("mission-a", request.MissionInstanceId);
        Assert.True(request.IsSpectator);
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
