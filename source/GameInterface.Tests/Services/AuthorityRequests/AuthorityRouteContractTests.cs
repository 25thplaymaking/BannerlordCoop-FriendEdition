using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Messages.Start;
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
    public void Router_RefusesARouteWhoseTypedMessageDoesNotDeclareTheSameIdentity()
    {
        using var broker = new MessageBroker();
        using var router = new AuthorityRequestRouter(broker, new Coop.Tests.Mocks.TestNetwork(),
            new Moq.Mock<global::GameInterface.Services.Players.IPlayerManager>().Object);

        var route = AuthorityRoute<string, TestRequest, TestResult>.Define(
            "wrong.route", AuthorityRouteKind.Command,
            id => new AuthorityRequestHeader(1, "session", id, 0), (intent, header) => new TestRequest(header, intent),
            request => request.Header, result => result.Header, _ => null, request => request.Intent, _ => null,
            (_, request) => new AuthorityServerReply<TestResult>(new TestResult(new AuthorityResultHeader(
                request.Header.SessionId, request.Header.RequestId, AuthorityResultStatus.Rejected, 0, "denied")), false),
            (header, status, reason) => new TestResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason)),
            _ => AuthorityCommitProbeResult.Applied, _ => { }, _ => { }, _ => true, AuthorityTimeoutPolicy.CampaignMutation);

        Assert.Throws<InvalidOperationException>(() => router.Register(route));
    }
}
