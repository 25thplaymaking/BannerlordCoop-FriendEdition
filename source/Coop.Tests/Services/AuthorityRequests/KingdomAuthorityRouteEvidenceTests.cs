using Common;
using Common.Messaging;
using Common.Network;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Kingdoms.Handlers;
using Coop.Core.Server.Services.Kingdoms.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Entity;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Data;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Election;
using Xunit;

namespace Coop.Tests.Services.AuthorityRequests;

public sealed class KingdomAuthorityRouteEvidenceTests
{
    private static readonly AuthorityRequestHeader RequestHeader = new(1, "kingdom-session", 17, 4);
    private static readonly AuthorityResultHeader AcceptedHeader = new(
        RequestHeader.SessionId, RequestHeader.RequestId, AuthorityResultStatus.Accepted,
        RequestHeader.ExpectedRevision, null);

    [Fact]
    public void KingdomMutations_AreDistinctTypedFailClosedRoutes_WithExactActorAndDomainProof()
    {
        var router = new CapturingAuthorityRouter();
        using var broker = new TestMessageBroker();
        using var handler = new ClientKingdomHandler(
            broker,
            Mock.Of<INetwork>(),
            Mock.Of<IControllerIdProvider>(),
            Mock.Of<IObjectManager>(),
            Mock.Of<IPlayerManager>(),
            Mock.Of<IKingdomCreationSettlementTracker>(),
            Mock.Of<IKingdomDecisionDataConverter>(),
            Mock.Of<IKingdomDecisionVoteManager>(),
            Mock.Of<IModConfigAuthority>(),
            router);

        object create = router.Get("kingdom.create");
        AssertCommandRoute<NetworkRequestCreateKingdom, NetworkCreateKingdomResult>(create);
        var createRequest = new NetworkRequestCreateKingdom(
            "player-a", "Realm", "culture-a", "party-a", "town-a", RequestHeader);
        Assert.True(IsExpected(create, createRequest,
            new NetworkCreateKingdomResult(AcceptedHeader, "kingdom-a", "Realm", "clan-a", "culture-a",
                "party-a", "town-a", "player-a")));
        Assert.False(IsExpected(create, createRequest,
            new NetworkCreateKingdomResult(AcceptedHeader, "kingdom-a", "Realm", "clan-a", "culture-a",
                "party-a", "town-a", "player-b")));

        object rename = router.Get("kingdom.rename");
        AssertCommandRoute<NetworkRequestChangeKingdomName, NetworkKingdomRenameResult>(rename);
        var renameRequest = new NetworkRequestChangeKingdomName("kingdom-a", "Realm", RequestHeader);
        Assert.True(IsExpected(rename, renameRequest,
            new NetworkKingdomRenameResult(AcceptedHeader, "kingdom-a", "Realm", "The Realm", "Realm", "player-a")));
        Assert.False(IsExpected(rename, renameRequest,
            new NetworkKingdomRenameResult(AcceptedHeader, "kingdom-b", "Realm", "The Realm", "Realm", "player-a")));

        object vote = router.Get("kingdom.decision.vote");
        AssertCommandRoute<NetworkRequestKingdomDecisionVote, NetworkKingdomDecisionVoteResult>(vote);
        var voteData = new KingdomDecisionVoteData("kingdom-a", 2, 1,
            (int)Supporter.SupportWeights.FullyPush, false, true, "outcome-a");
        var voteRequest = new NetworkRequestKingdomDecisionVote("player-a", voteData, RequestHeader);
        Assert.True(IsExpected(vote, voteRequest,
            new NetworkKingdomDecisionVoteResult(AcceptedHeader, "clan-a", voteData, false, -1, null, "player-a")));
        Assert.False(IsExpected(vote, voteRequest,
            new NetworkKingdomDecisionVoteResult(AcceptedHeader, "clan-a", voteData, false, -1, null, "player-b")));
    }

    private static void AssertCommandRoute<TRequest, TResult>(object route)
        where TRequest : IMessage where TResult : IMessage
    {
        Type[] arguments = route.GetType().GetGenericArguments();
        Assert.Equal(typeof(TRequest), arguments[1]);
        Assert.Equal(typeof(TResult), arguments[2]);
        Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(route, "Kind"));
        Assert.True(Property<bool>(route, "FailClosedOnApplyFailure"));
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(TRequest), typeof(AuthorityRouteAttribute));
        Assert.NotNull(attribute);
        Assert.Equal(Property<string>(route, "RouteId"), attribute.RouteId);
    }

    private static bool IsExpected(object route, object request, object result) =>
        (bool)Property<Delegate>(route, "IsExpectedClientResult").DynamicInvoke(request, result)!;
    private static T Property<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private sealed class CapturingAuthorityRouter : IAuthorityRequestRouter
    {
        private readonly List<object> routes = new();
        public int Priority => 0;
        public IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
            AuthorityRoute<TIntent, TRequest, TResult> route) where TRequest : IMessage where TResult : IMessage
        { routes.Add(route); return new NoOpRouteHandle<TIntent, TResult>(); }
        public object Get(string routeId) => Assert.Single(routes, route => Property<string>(route, "RouteId") == routeId);
        public bool IsRegistered(string routeId, AuthorityRouteKind kind) => routes.Any(route =>
            Property<string>(route, "RouteId") == routeId && Property<AuthorityRouteKind>(route, "Kind") == kind);
        public void Update(TimeSpan frameTime) { }
        public void Dispose() { }
    }

    private sealed class NoOpRouteHandle<TIntent, TResult> : IAuthorityRouteHandle<TIntent, TResult>
        where TResult : IMessage
    {
        public AuthorityRequestLifecycle Lifecycle => null!;
        public AuthorityRequestTicket<TResult> Submit(TIntent intent, Action<AuthorityClientOutcome<TResult>> completion = null) =>
            throw new NotSupportedException();
        public AuthorityClientOutcome<TResult> SubmitBlocking(TIntent intent) => throw new NotSupportedException();
        public void Poll() { }
        public void CancelAll(string reasonCode) { }
        public void Dispose() { }
    }
}
