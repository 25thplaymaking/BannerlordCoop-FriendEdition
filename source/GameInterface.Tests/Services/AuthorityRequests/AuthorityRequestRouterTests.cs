using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Coop.Tests.Mocks;
using Autofac;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

[Collection(ModInformationRoleCollection.Name)]
public sealed class AuthorityRequestRouterTests
{
    static AuthorityRequestRouterTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    }

    [Fact]
    public void AcceptedResult_StaysPendingUntilTheClientRegistryProbeReportsReady()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        var registryReady = false;
        using var route = router.Register(CreateRoute(() => registryReady
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending));

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

        registryReady = true;
        route.Poll();
        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Applied, ticket.Outcome.Completion);
    }

    [Fact]
    public void AcceptedResult_WithMismatchedFeaturePayload_FailsClosedBeforeCommit()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Applied,
            expected: (request, _) => request.Intent == "expected"));

        var ticket = route.Submit("unexpected");
        var request = Assert.Single(network.GetPeerMessagesFromType<TestRequest>(server));
        broker.Publish(server, new TestResult(new AuthorityResultHeader(request.Header.SessionId, request.Header.RequestId,
            AuthorityResultStatus.Accepted, 3, "")));

        route.Poll();

        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.ReplicaApplyFailed, ticket.Outcome.Completion);
        Assert.Equal("invalid-replica", ticket.Outcome.ReasonCode);
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
    public void Update_PollsBootstrapAndCommandRoutesFromTheMainLoop()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
            new AuthorityTimeoutPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1)));

        route.Submit("intent");
        Thread.Sleep(10);
        router.Update(TimeSpan.Zero);

        Assert.Equal(2, network.GetPeerMessagesFromType<TestRequest>(server).Count());
    }

    [Fact]
    public void IsRegistered_RequiresTheExactRouteIdAndKind()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending));

        Assert.True(router.IsRegistered("test.route", AuthorityRouteKind.Command));
        Assert.False(router.IsRegistered("test.route", AuthorityRouteKind.BootstrapQuery));
        Assert.False(router.IsRegistered("unknown", AuthorityRouteKind.Command));
    }

    [Fact]
    public void LifetimeScopedRouter_PollsTheResolvedInstanceAndUnregistersDisposedRoutes()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(broker).As<IMessageBroker>();
        builder.RegisterInstance(network).As<INetwork>();
        builder.RegisterInstance(new Mock<IPlayerManager>().Object).As<IPlayerManager>();
        builder.RegisterType<AuthorityRequestRouter>().As<IAuthorityRequestRouter>().InstancePerLifetimeScope();
        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        var firstResolution = scope.Resolve<IAuthorityRequestRouter>();
        var secondResolution = scope.Resolve<IAuthorityRequestRouter>();
        Assert.Same(firstResolution, secondResolution);

        using var route = firstResolution.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
            new AuthorityTimeoutPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1)));
        route.Submit("intent");
        Thread.Sleep(10);
        secondResolution.Update(TimeSpan.Zero);

        Assert.Equal(2, network.GetPeerMessagesFromType<TestRequest>(server).Count());
        Assert.True(firstResolution.IsRegistered("test.route", AuthorityRouteKind.Command));

        route.Dispose();

        Assert.False(secondResolution.IsRegistered("test.route", AuthorityRouteKind.Command));
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", 1,
            AuthorityResultStatus.Rejected, 0, "late-result")));
        secondResolution.Update(TimeSpan.Zero);
        Assert.Equal(2, network.GetPeerMessagesFromType<TestRequest>(server).Count());
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

    [Fact]
    public void SessionMismatch_CancelsEveryPendingTicket()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending));

        var first = route.Submit("first");
        var second = route.Submit("second");
        broker.Publish(server, new TestResult(new AuthorityResultHeader("replacement", first.RequestId,
            AuthorityResultStatus.Rejected, 0, "session-replaced")));

        Assert.Equal(AuthorityClientCompletion.Cancelled, first.Outcome.Completion);
        Assert.Equal(AuthorityClientCompletion.Cancelled, second.Outcome.Completion);
    }

    /// <summary>
    /// Completing a ticket marshals its presentation callback onto the game thread and waits for
    /// it, while the game thread's own Poll takes the route lock as its first act. Cancelling from
    /// inside that lock parked the network poller and the game loop against each other for the full
    /// 30s blocking timeout, once per pending request — a hard hang whenever the host replaced its
    /// session while a request was in flight. The route must not hold its lock across a completion.
    /// </summary>
    [Fact]
    public void SessionMismatch_DoesNotHoldTheRouteLockWhileMarshallingToTheGameThread()
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);

        var callbackEntered = new ManualResetEventSlim(false);
        var pollCompleted = new ManualResetEventSlim(false);
        int callbacksSeen = 0;
        int pollFinishedWhileCancelling = 0;

        using var route = router.Register(CreateRoute(
            () => AuthorityCommitProbeResult.Pending,
            presented: _ =>
            {
                // Runs on the game-loop pump while the publishing thread waits for it. Only the
                // first of the two cancellations needs to observe the lock.
                if (Interlocked.Exchange(ref callbacksSeen, 1) != 0) return;
                callbackEntered.Set();
                // Generous, because this only bounds how long a FAILING run takes: with the lock
                // released, Poll returns immediately. A short wait would false-fail on a starved
                // CI box that could not schedule the probe thread in time.
                Volatile.Write(ref pollFinishedWhileCancelling,
                    pollCompleted.Wait(TimeSpan.FromSeconds(10)) ? 1 : 0);
            }));

        route.Submit("first");
        route.Submit("second");

        // Poll contends for exactly the lock HandleResult takes, so it can only finish here if the
        // publishing thread released that lock before marshalling the cancellation.
        var poller = new Thread(() =>
        {
            callbackEntered.Wait(TimeSpan.FromSeconds(5));
            route.Poll();
            pollCompleted.Set();
        })
        { IsBackground = true, Name = "authority-route-poll-probe" };
        poller.Start();

        // Returns only after both cancellations have run their marshalled callbacks.
        broker.Publish(server, new TestResult(new AuthorityResultHeader("replacement", 1,
            AuthorityResultStatus.Rejected, 0, "session-replaced")));
        poller.Join(TimeSpan.FromSeconds(20));

        Assert.Equal(1, Volatile.Read(ref pollFinishedWhileCancelling));
    }

    [Fact]
    public void PresentationCallback_RunsOnTheInitializedGameThread()
    {
        Assert.True(GameThread.Instance.IsInitialized, "game-loop pump was not initialized");
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        bool presentedOnGameThread = false;
        using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
            presented: _ => presentedOnGameThread = GameThread.Instance.IsGameThread));

        var ticket = route.Submit("intent");
        broker.Publish(server, new TestResult(new AuthorityResultHeader("session", ticket.RequestId,
            AuthorityResultStatus.Rejected, 0, "denied")));

        Assert.True(presentedOnGameThread);
    }

    [Theory]
    [InlineData(AuthorityResultStatus.Unavailable)]
    [InlineData(AuthorityResultStatus.StaleSession)]
    [InlineData(AuthorityResultStatus.StaleState)]
    public void ServerHeaderFailure_UsesTheExplicitTerminalStatus(AuthorityResultStatus status)
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            using var router = new AuthorityRequestRouter(broker, network, manager.Object);
            using var route = router.Register(CreateRoute(
                () => AuthorityCommitProbeResult.Pending,
                validateHeader: _ => AuthorityHeaderValidation.Reject(status, "server-not-ready")));

            broker.Publish(peer, new TestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "intent"));

            var reply = Assert.Single(network.GetPeerMessagesFromType<TestResult>(peer));
            Assert.Equal(status, reply.Header.Status);
            Assert.Equal("server-not-ready", reply.Header.ReasonCode);
        });
    }

    [Fact]
    public void FullInFlightServerLedger_ReturnsUnavailableWithoutEvictingTheOriginalRequest()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            using var router = new AuthorityRequestRouter(broker, network, manager.Object,
                replayLedgerCapacityPerPeer: 1);
            using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, request) =>
                {
                    if (request.Header.RequestId == 1)
                        broker.Publish(peer, new TestRequest(new AuthorityRequestHeader(1, "session", 2, 2), "second"));
                    return Accepted(request.Header);
                }));

            broker.Publish(peer, new TestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "first"));

            var replies = network.GetPeerMessagesFromType<TestResult>(peer).ToArray();
            Assert.Equal(2, replies.Length);
            Assert.Contains(replies, reply => reply.Header.RequestId == 1 &&
                reply.Header.Status == AuthorityResultStatus.Accepted);
            Assert.Contains(replies, reply => reply.Header.RequestId == 2 &&
                reply.Header.Status == AuthorityResultStatus.Unavailable &&
                reply.Header.ReasonCode == "authority-overloaded");
        });
    }

    [Fact]
    public void CompletedReplay_IsCached_AndConflictDoesNotExecuteAgain()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            int executions = 0;
            using var router = new AuthorityRequestRouter(broker, network, manager.Object);
            using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, request) =>
                {
                    executions++;
                    return Accepted(request.Header);
                }));
            var request = new TestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "first");

            broker.Publish(peer, request);
            broker.Publish(peer, request);
            broker.Publish(peer, new TestRequest(request.Header, "conflict"));

            Assert.Equal(1, executions);
            Assert.Equal(2, network.GetPeerMessagesFromType<TestResult>(peer).Count());
        });
    }

    [Fact]
    public void BootstrapRoute_EndsAtReplySentWithoutCommandMutationPhases()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            using var router = new AuthorityRequestRouter(broker, network, manager.Object);
            using var route = router.Register(CreateBootstrapRoute());

            broker.Publish(peer, new BootstrapTestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "query"));

            // The server keys tracking by peer as well as id, because request ids are a
            // per-client sequence and concurrent joiners all start at 1.
            Assert.True(route.Lifecycle.TryGetSnapshot($"{peer.Id}:1", out var snapshot));
            Assert.Equal(AuthorityRequestPhase.ReplySent, snapshot.Phase);
            Assert.Equal(AuthorityResultStatus.Accepted.ToString(), snapshot.Outcome);
        });
    }

    [Fact]
    public void UnauthorizedRequest_DoesNotReserveReplayIdBeforeLaterAuthenticatedExecution()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = new Mock<IPlayerManager>();
            Player player = new Player("controller", "hero", "party", "clan", "character");
            manager.Setup(m => m.TryGetPlayer(peer, out player)).Returns(false);
            int executions = 0;
            using var router = new AuthorityRequestRouter(broker, network, manager.Object);
            using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, request) =>
                {
                    executions++;
                    return Accepted(request.Header);
                }));
            var request = new TestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "intent");

            broker.Publish(peer, request);
            manager.Setup(m => m.TryGetPlayer(peer, out player)).Returns(true);
            broker.Publish(peer, request);

            Assert.Equal(1, executions);
            Assert.Contains(network.GetPeerMessagesFromType<TestResult>(peer),
                reply => reply.Header.Status == AuthorityResultStatus.Unauthorized);
            Assert.Contains(network.GetPeerMessagesFromType<TestResult>(peer),
                reply => reply.Header.Status == AuthorityResultStatus.Accepted);
        });
    }

    [Fact]
    public void ExecutorAndPublicationFailures_ReturnExecutionFailed()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            using (var router = new AuthorityRequestRouter(broker, network, manager.Object))
            using (router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, __) => throw new InvalidOperationException("boom"))))
            {
                broker.Publish(peer, new TestRequest(new AuthorityRequestHeader(1, "session", 1, 2), "throw"));
                Assert.Equal(AuthorityResultStatus.ExecutionFailed,
                    Assert.Single(network.GetPeerMessagesFromType<TestResult>(peer)).Header.Status);
            }

            network.Clear();
            using (var router = new AuthorityRequestRouter(broker, network, manager.Object))
            using (router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, request) => new AuthorityServerReply<TestResult>(
                    new TestResult(new AuthorityResultHeader("session", request.Header.RequestId,
                        AuthorityResultStatus.Accepted, 3, "")), false))))
            {
                broker.Publish(peer, new TestRequest(new AuthorityRequestHeader(1, "session", 2, 2), "unpublished"));
                var publicationReply = Assert.Single(network.GetPeerMessagesFromType<TestResult>(peer));
                Assert.Equal(AuthorityResultStatus.ExecutionFailed, publicationReply.Header.Status);
                Assert.Equal("publication-failed", publicationReply.Header.ReasonCode);
            }
        });
    }

    [Fact]
    public void IsolatedPartialPublication_DoesNotSendOrReplayATerminalResult()
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var manager = AuthenticatedManager(peer);
            int executions = 0;
            using var router = new AuthorityRequestRouter(broker, network, manager.Object);
            using var route = router.Register(CreateRoute(() => AuthorityCommitProbeResult.Pending,
                execute: (_, request) =>
                {
                    executions++;
                    return new AuthorityServerReply<TestResult>(new TestResult(new AuthorityResultHeader(
                        "session", request.Header.RequestId, AuthorityResultStatus.ExecutionFailed, 0,
                        "partial-publication")), false, suppressReply: true);
                }));

            var request = new TestRequest(new AuthorityRequestHeader(1, "session", 88, 2), "isolated");
            broker.Publish(peer, request);
            broker.Publish(peer, request);

            Assert.Equal(1, executions);
            Assert.False(network.SentNetworkMessages.ContainsKey(peer.Id));
        });
    }

    private static AuthorityRoute<string, TestRequest, TestResult> CreateRoute(
        Func<AuthorityCommitProbeResult> probe,
        AuthorityTimeoutPolicy timeout = null,
        Action<AuthorityClientOutcome<TestResult>> presented = null,
        Func<AuthorityRequestHeader, AuthorityHeaderValidation> validateHeader = null,
        Func<AuthorityServerContext, TestRequest, AuthorityServerReply<TestResult>> execute = null,
        Func<TestRequest, TestResult, bool> expected = null) =>
        AuthorityRoute<string, TestRequest, TestResult>.Define(
            "test.route", AuthorityRouteKind.Command,
            id => new AuthorityRequestHeader(1, "session", id, 2),
            (intent, header) => new TestRequest(header, intent),
            request => request.Header,
            result => result.Header,
            _ => null,
            request => request.Intent,
            validateHeader ?? (header => header.SessionId == "session" && header.ExpectedRevision == 2
                ? AuthorityHeaderValidation.Valid
                : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale")),
            execute ?? ((_, request) => Accepted(request.Header)),
            (header, status, reason) => new TestResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason)),
            _ => probe(),
            _ => { },
            presented ?? (_ => { }),
            source => source is NetPeer,
            timeout ?? AuthorityTimeoutPolicy.CampaignMutation,
            isExpectedClientResult: expected);

    private static AuthorityServerReply<TestResult> Accepted(AuthorityRequestHeader header) =>
        new AuthorityServerReply<TestResult>(new TestResult(new AuthorityResultHeader(
            header.SessionId, header.RequestId, AuthorityResultStatus.Accepted, 3, "")), true);

    private static AuthorityRoute<string, BootstrapTestRequest, TestResult> CreateBootstrapRoute() =>
        AuthorityRoute<string, BootstrapTestRequest, TestResult>.Define(
            "test.bootstrap", AuthorityRouteKind.BootstrapQuery,
            id => new AuthorityRequestHeader(1, "session", id, 2),
            (intent, header) => new BootstrapTestRequest(header, intent),
            request => request.Header,
            result => result.Header,
            _ => null,
            request => request.Intent,
            header => header.SessionId == "session" && header.ExpectedRevision == 2
                ? AuthorityHeaderValidation.Valid
                : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale"),
            (_, request) => Accepted(request.Header),
            (header, status, reason) => new TestResult(new AuthorityResultHeader(
                header.SessionId, header.RequestId, status, 0, reason)),
            _ => AuthorityCommitProbeResult.Applied,
            _ => { },
            _ => { },
            source => source is NetPeer,
            AuthorityTimeoutPolicy.BootstrapQuery);

    private static Mock<IPlayerManager> AuthenticatedManager(NetPeer peer)
    {
        var manager = new Mock<IPlayerManager>();
        Player player = new Player("controller", "hero", "party", "clan", "character");
        manager.Setup(m => m.TryGetPlayer(peer, out player)).Returns(true);
        return manager;
    }

    private static void RunAsServer(Action action)
    {
        bool wasServer = ModInformation.IsServer;
        Exception failure = null;
        GameThread.Run(() =>
        {
            ModInformation.IsServer = true;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { ModInformation.IsServer = wasServer; }
        }, blocking: true, label: nameof(RunAsServer));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
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

[AuthorityRoute("test.bootstrap", AuthorityRouteKind.BootstrapQuery)]
internal readonly struct BootstrapTestRequest : IMessage
{
    public BootstrapTestRequest(AuthorityRequestHeader header, string intent)
    {
        Header = header;
        Intent = intent;
    }

    public AuthorityRequestHeader Header { get; }
    public string Intent { get; }
}
