using Common;
using Common.Messaging;
using Common.Network.Messages;
using Coop.Tests.Mocks;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Players;
using LiteNetLib;
using Moq;
using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

public sealed class AuthorityRequestRouterTests
{
    [Fact]
    public void Submit_CorrelatesOnlyMatchingTrustedResult_AndWaitsForReplicaApply()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        var applied = false;
        using var route = router.Register(CreateRoute(() => applied ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending));

        var ticket = route.Submit("intent");
        var request = Assert.Single(network.GetPeerMessagesFromType<TestRequest>(server));
        Assert.Equal(1, request.Header.RequestId);

        broker.Publish(server, new TestResult(new AuthorityResultHeader(request.Header.SessionId, request.Header.RequestId + 1,
            AuthorityResultStatus.Accepted, 3, "")));
        Assert.False(ticket.IsCompleted);

        broker.Publish(server, new TestResult(new AuthorityResultHeader(request.Header.SessionId, request.Header.RequestId,
            AuthorityResultStatus.Accepted, 3, "")));
        route.Poll();
        Assert.False(ticket.IsCompleted);

        applied = true;
        route.Poll();
        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Applied, ticket.Outcome.Completion);
    }

    [Fact]
    public void ResponseTimeout_RetriesTheSameTypedRequestIdOnce()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
            new AuthorityTimeoutPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1)));

        var ticket = route.Submit("intent");
        Thread.Sleep(10);
        route.Poll();

        var attempts = network.GetPeerMessagesFromType<TestRequest>(server).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(attempts[0].Header.RequestId, attempts[1].Header.RequestId);
        Assert.Equal(ticket.RequestId, attempts[0].Header.RequestId);
    }

    [Fact]
    public void Rejection_CompletesAndPresentsExactlyOnce()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        int presentations = 0;
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending, presented: _ => presentations++));

        var ticket = route.Submit("intent");
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", ticket.RequestId,
            AuthorityResultStatus.Rejected, 0, "denied")));
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", ticket.RequestId,
            AuthorityResultStatus.Rejected, 0, "denied")));

        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Rejected, ticket.Outcome.Completion);
        Assert.Equal(1, presentations);
    }

    [Fact]
    public void ClientSessionEnded_CancelsPendingRequestAndStillInvokesCompletion()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        int completions = 0;
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending));

        var ticket = route.Submit("intent", _ => completions++);
        broker.Publish(this, new ClientSessionEnded(default));

        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Cancelled, ticket.Outcome.Completion);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void AcceptedPresentationFailure_FailsClosedButDoesNotSuppressCallerCompletion()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        int completions = 0;
        using var route = router.Register(CreateRoute(
            () => AuthorityCommitProbeResult.Applied,
            presented: _ => throw new InvalidOperationException("presentation-failure")));

        var ticket = route.Submit("intent", _ => completions++);
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", ticket.RequestId,
            AuthorityResultStatus.Accepted, 3, "")));
        route.Poll();

        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.ReplicaApplyFailed, ticket.Outcome.Completion);
        Assert.Equal("presentation-failed", ticket.Outcome.ReasonCode);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void ProbeFailure_FailsClosedAndDoesNotLeaveTicketPending()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => throw new InvalidOperationException("probe-failure")));

        var ticket = route.Submit("intent");
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", ticket.RequestId,
            AuthorityResultStatus.Accepted, 3, "")));
        route.Poll();

        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.ReplicaApplyFailed, ticket.Outcome.Completion);
        Assert.Equal("commit-probe-failed", ticket.Outcome.ReasonCode);
    }

    private static AuthorityRoute<string, TestRequest, TestResult> CreateRoute(
        Func<AuthorityCommitProbeResult> probe,
        AuthorityTimeoutPolicy timeout = null,
        Action<AuthorityClientOutcome<TestResult>> presented = null) =>
        AuthorityRoute<string, TestRequest, TestResult>.Define(
            "test.route", AuthorityRouteKind.Command,
            id => new AuthorityRequestHeader(1, "session", id, 2),
            (intent, header) => new TestRequest(header, intent),
            request => request.Header,
            result => result.Header,
            _ => null,
            request => request.Intent,
            header => header.SessionId == "session" && header.ExpectedRevision == 2
                ? AuthorityHeaderValidation.Valid
                : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale"),
            (_, request) => new AuthorityServerReply<TestResult>(new TestResult(new AuthorityResultHeader(
                request.Header.SessionId, request.Header.RequestId, AuthorityResultStatus.Accepted, 3, "")), true),
            (header, status, reason) => new TestResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason)),
            _ => probe(),
            _ => { },
            presented ?? (_ => { }),
            source => source is NetPeer,
            timeout ?? AuthorityTimeoutPolicy.CampaignMutation);
}

[AuthorityRoute("test.route", AuthorityRouteKind.Command)]
internal readonly struct TestRequest : IMessage
{
    public TestRequest(AuthorityRequestHeader header, string intent)
    {
        Header = header;
        Intent = intent;
    }

    public AuthorityRequestHeader Header { get; }
    public string Intent { get; }
}

internal readonly struct TestResult : IMessage
{
    public TestResult(AuthorityResultHeader header) => Header = header;
    public AuthorityResultHeader Header { get; }
}
