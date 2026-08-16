using Common;
using Common.Messaging;
using Common.Network.Messages;
using Coop.Tests.Mocks;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Fourberie;
using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
using GameInterface.Services.WorkshopMods.PlayerSettlement;
using GameInterface.Services.WorkshopMods.Core;
using LiteNetLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

[Collection(ModInformationRoleCollection.Name)]
public sealed class WorkshopSnapshotRouteTests
{
    static WorkshopSnapshotRouteTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    }

    [Fact]
    public void FourberieSnapshotRoute_AcceptedResultWaitsForReady_AndStaleTerminalIsUnavailable()
        => AssertClientLifecycle<NetworkRequestFourberieState, NetworkFourberieStateQueryResult>(
            "workshop.fourberie.snapshot", header => new NetworkRequestFourberieState(header),
            result => result.Header,
            (header, status, reason) => new NetworkFourberieStateQueryResult(header, status, null, reason));

    [Fact]
    public void ImprovedGarrisonsSnapshotRoute_AcceptedResultWaitsForReady_AndStaleTerminalIsUnavailable()
        => AssertClientLifecycle<NetworkRequestImprovedGarrisonsState, NetworkImprovedGarrisonsStateQueryResult>(
            "workshop.improved-garrisons.snapshot", header => new NetworkRequestImprovedGarrisonsState(header),
            result => result.Header,
            (header, status, reason) => new NetworkImprovedGarrisonsStateQueryResult(header, status, null, reason));

    [Fact]
    public void PlayerSettlementSnapshotRoute_AcceptedResultWaitsForReady_AndStaleTerminalIsUnavailable()
        => AssertClientLifecycle<NetworkRequestPlayerSettlementState, NetworkPlayerSettlementStateQueryResult>(
            "workshop.player-settlement.snapshot", header => new NetworkRequestPlayerSettlementState(header),
            result => result.Header,
            (header, status, reason) => new NetworkPlayerSettlementStateQueryResult(header, status, null, reason));

    [Fact]
    public void FourberieSnapshotRoute_CaptureUnavailableReturnsTypedUnavailable()
        => AssertServerUnavailable<NetworkRequestFourberieState, NetworkFourberieStateQueryResult>(
            "workshop.fourberie.snapshot", header => new NetworkRequestFourberieState(header),
            result => result.Header,
            (header, status, reason) => new NetworkFourberieStateQueryResult(header, status, null, reason));

    [Fact]
    public void ImprovedGarrisonsSnapshotRoute_CaptureUnavailableReturnsTypedUnavailable()
        => AssertServerUnavailable<NetworkRequestImprovedGarrisonsState, NetworkImprovedGarrisonsStateQueryResult>(
            "workshop.improved-garrisons.snapshot", header => new NetworkRequestImprovedGarrisonsState(header),
            result => result.Header,
            (header, status, reason) => new NetworkImprovedGarrisonsStateQueryResult(header, status, null, reason));

    [Fact]
    public void PlayerSettlementSnapshotRoute_CaptureUnavailableReturnsTypedUnavailable()
        => AssertServerUnavailable<NetworkRequestPlayerSettlementState, NetworkPlayerSettlementStateQueryResult>(
            "workshop.player-settlement.snapshot", header => new NetworkRequestPlayerSettlementState(header),
            result => result.Header,
            (header, status, reason) => new NetworkPlayerSettlementStateQueryResult(header, status, null, reason));

    private static void AssertClientLifecycle<TRequest, TResult>(
        string routeId,
        Func<AuthorityRequestHeader, TRequest> buildRequest,
        Func<TResult, AuthorityResultHeader> readResultHeader,
        Func<AuthorityRequestHeader, AuthorityResultStatus, string, TResult> terminal)
        where TRequest : IMessage
        where TResult : IMessage
    {
        using var broker = new MessageBroker();
        using var network = new TestNetwork();
        var server = network.CreatePeer();
        using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
        bool snapshotReady = false;
        string acceptedSession = null;
        long acceptedRevision = -1;
        WorkshopSnapshotReadiness readiness = WorkshopSnapshotReadiness.Loading;
        using var route = router.Register(AuthorityRoute<int, TRequest, TResult>.Define(
            routeId, AuthorityRouteKind.BootstrapQuery,
            id => new AuthorityRequestHeader(1, "session", id, 0),
            (_, header) => buildRequest(header),
            request => ReadHeader(request),
            readResultHeader,
            _ => null,
            _ => "snapshot",
            _ => AuthorityHeaderValidation.Valid,
            (context, _) => new AuthorityServerReply<TResult>(
                terminal(context.Header, AuthorityResultStatus.Unavailable, "capture-unavailable"), false),
            terminal,
            result => snapshotReady && string.Equals(acceptedSession, readResultHeader(result).SessionId, StringComparison.Ordinal) &&
                acceptedRevision == readResultHeader(result).CommittedRevision
                ? AuthorityCommitProbeResult.Applied
                : AuthorityCommitProbeResult.Pending,
            _ => { },
            outcome => readiness = outcome.Completion == AuthorityClientCompletion.Applied
                ? WorkshopSnapshotReadiness.Ready
                : WorkshopSnapshotReadiness.Unavailable,
            source => source is NetPeer,
            AuthorityTimeoutPolicy.BootstrapQuery,
            requireAuthenticatedPlayer: false));

        var ticket = route.Submit(0);
        var request = Assert.Single(network.GetPeerMessagesFromType<TRequest>(server));
        broker.Publish(server, terminal(ReadHeader(request), AuthorityResultStatus.Accepted, null));

        Assert.False(ticket.IsCompleted); // router has receipt, but the registry has not applied it yet.
        acceptedSession = "session";
        acceptedRevision = 0;
        snapshotReady = true;
        router.Update(TimeSpan.Zero);
        Assert.True(ticket.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Applied, ticket.Outcome.Completion);
        Assert.Equal(WorkshopSnapshotReadiness.Ready, readiness);

        snapshotReady = false;
        var stale = route.Submit(0);
        broker.Publish(server, terminal(new AuthorityRequestHeader(1, "session", stale.RequestId, 0),
            AuthorityResultStatus.StaleSession, "stale-config-session"));
        Assert.True(stale.IsCompleted);
        Assert.Equal(AuthorityClientCompletion.Rejected, stale.Outcome.Completion);
        Assert.False(snapshotReady);
        Assert.Equal(WorkshopSnapshotReadiness.Unavailable, readiness);
    }

    private static void AssertServerUnavailable<TRequest, TResult>(
        string routeId,
        Func<AuthorityRequestHeader, TRequest> buildRequest,
        Func<TResult, AuthorityResultHeader> readResultHeader,
        Func<AuthorityRequestHeader, AuthorityResultStatus, string, TResult> terminal)
        where TRequest : IMessage
        where TResult : IMessage
    {
        RunAsServer(() =>
        {
            using var broker = new MessageBroker();
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            using var router = new AuthorityRequestRouter(broker, network, new Mock<IPlayerManager>().Object);
            using var route = router.Register(AuthorityRoute<int, TRequest, TResult>.Define(
                routeId, AuthorityRouteKind.BootstrapQuery,
                id => new AuthorityRequestHeader(1, "session", id, 0),
                (_, header) => buildRequest(header),
                request => ReadHeader(request),
                readResultHeader,
                _ => null,
                _ => "snapshot",
                _ => AuthorityHeaderValidation.Valid,
                (context, _) => new AuthorityServerReply<TResult>(
                    terminal(context.Header, AuthorityResultStatus.Unavailable, "capture-unavailable"), false),
                terminal,
                _ => AuthorityCommitProbeResult.Pending,
                _ => { },
                _ => { },
                _ => true,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: false));

            broker.Publish(peer, buildRequest(new AuthorityRequestHeader(1, "session", 1, 0)));
            var result = Assert.Single(network.GetPeerMessagesFromType<TResult>(peer));
            Assert.Equal(AuthorityResultStatus.Unavailable, readResultHeader(result).Status);
            Assert.Equal("capture-unavailable", readResultHeader(result).ReasonCode);
        });
    }

    private static AuthorityRequestHeader ReadHeader<TRequest>(TRequest request) where TRequest : IMessage => request switch
    {
        NetworkRequestFourberieState fourberie => fourberie.Header,
        NetworkRequestImprovedGarrisonsState improvedGarrisons => improvedGarrisons.Header,
        NetworkRequestPlayerSettlementState playerSettlement => playerSettlement.Header,
        _ => throw new InvalidOperationException("Unsupported snapshot request."),
    };

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
        }, blocking: true, label: nameof(WorkshopSnapshotRouteTests));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
