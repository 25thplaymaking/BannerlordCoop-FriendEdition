using Common;
using Common.Messaging;
using Common.Network;
using Common.Tests.Utils;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Tournaments;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Handlers;
using GameInterface.Services.Tournaments.Messages;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.Tournaments;

public sealed class TournamentHitProgressionAuthorityRouteEvidenceTests
{
    [Fact]
    public void HitProgressionRoute_IsTypedFailClosedAndRequiresTheSubmittedCommitTuple()
    {
        var router = new CapturingAuthorityRouter();
        using var broker = new TestMessageBroker();
        using var handler = new TournamentSessionHandler(
            broker,
            Mock.Of<INetwork>(),
            Mock.Of<IObjectManager>(),
            Mock.Of<IPlayerManager>(),
            Mock.Of<IControllerIdProvider>(),
            Mock.Of<ITournamentSessionRegistry>(),
            Mock.Of<ITournamentGameInterface>(),
            Mock.Of<ITournamentNativeRemovalAuthorization>(),
            Mock.Of<IModConfigAuthority>(),
            router);

        object route = router.Get("tournament.hit-progression");
        Type[] routeTypes = route.GetType().GetGenericArguments();
        Assert.Equal(typeof(NetworkSubmitTournamentHitProgression), routeTypes[1]);
        Assert.Equal(typeof(NetworkTournamentHitProgressionApplied), routeTypes[2]);
        Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(route, "Kind"));
        Assert.True(Property<bool>(route, "FailClosedOnApplyFailure"));
        Assert.Equal(0, Property<AuthorityTimeoutPolicy>(route, "TimeoutPolicy").RetryCount);

        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkSubmitTournamentHitProgression), typeof(AuthorityRouteAttribute))!;
        Assert.NotNull(attribute);
        Assert.Equal("tournament.hit-progression", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);

        var header = new AuthorityRequestHeader(1, "config-session", 71, 19);
        TournamentHitProgressionData data = HitData("tournament-a", "match-a", 12);
        var request = new NetworkSubmitTournamentHitProgression(header, data, "mission-a");
        var exactResult = new NetworkTournamentHitProgressionApplied(
            header,
            AuthorityResultStatus.Accepted,
            Snapshot("tournament-a", "match-a", "mission-a"),
            data,
            "character-a",
            null);

        Assert.True(IsExpected(route, request, exactResult));
        Assert.False(IsExpected(route, request, new NetworkTournamentHitProgressionApplied(
            header,
            AuthorityResultStatus.Accepted,
            Snapshot("tournament-a", "match-a", "mission-a"),
            HitData("tournament-a", "match-a", 13),
            "character-a",
            null)));
        Assert.False(IsExpected(route, request, new NetworkTournamentHitProgressionApplied(
            header,
            AuthorityResultStatus.Accepted,
            Snapshot("tournament-a", "match-a", "mission-b"),
            data,
            "character-a",
            null)));
        Assert.False(IsExpected(route, request, new NetworkTournamentHitProgressionApplied(
            header,
            AuthorityResultStatus.Accepted,
            Snapshot("tournament-a", "match-a", "mission-a"),
            HitData("tournament-a", "match-a", 12, controllerId: "controller-b"),
            "character-a",
            null)));
        Assert.False(IsExpected(route, request, new NetworkTournamentHitProgressionApplied(
            header,
            AuthorityResultStatus.Accepted,
            Snapshot("tournament-a", "match-a", "mission-a"),
            HitData(
                "tournament-a",
                "match-a",
                12,
                attackerId: Guid.Parse("33333333-3333-3333-3333-333333333333")),
            "character-a",
            null)));

        Assert.Equal(header, request.Header);
        Assert.Equal(header.SessionId, exactResult.Header.SessionId);
        Assert.Equal(header.RequestId, exactResult.Header.RequestId);
        Assert.Equal(data.Revision, exactResult.Header.CommittedRevision);
    }

    private static TournamentHitProgressionData HitData(
        string sessionId,
        string matchId,
        long sequence,
        string controllerId = "controller-a",
        Guid? attackerId = null) => new(
        sessionId,
        matchId,
        revision: 24,
        bracketRevision: 6,
        damageOriginControllerId: controllerId,
        damageSequence: sequence,
        attackerAgentId: attackerId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        victimAgentId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        weaponItemId: "weapon-a",
        weaponUsageIndex: 0,
        movementSpeedModifier: 1f,
        shotDifficulty: 0f,
        hitpointRatio: 0.5f,
        damageAmount: 12f,
        attackType: 0,
        attackerMounted: false,
        sameTeam: false,
        fatal: false,
        charging: false,
        sneakAttack: false);

    private static TournamentSessionSnapshot Snapshot(string sessionId, string matchId, string missionId) => new(
        sessionId,
        missionId,
        "town-a",
        "scene-a",
        "prize-a",
        TournamentSessionPhase.LiveMatch,
        revision: 24,
        bracketRevision: 6,
        matchId,
        "controller-a",
        Array.Empty<string>(),
        Array.Empty<TournamentContestantData>(),
        Array.Empty<string>(),
        Array.Empty<TournamentPlayerChoiceData>(),
        Array.Empty<TournamentRoundData>(),
        0,
        0,
        0,
        false,
        false,
        null);

    private static bool IsExpected(object route, object request, object result)
    {
        var predicate = Property<Delegate>(route, "IsExpectedClientResult");
        return (bool)predicate.DynamicInvoke(request, result)!;
    }

    private static T Property<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private sealed class CapturingAuthorityRouter : IAuthorityRequestRouter
    {
        private readonly List<object> routes = new();

        public int Priority => 0;

        public IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
            AuthorityRoute<TIntent, TRequest, TResult> route)
            where TRequest : IMessage
            where TResult : IMessage
        {
            routes.Add(route);
            return new NoOpRouteHandle<TIntent, TResult>();
        }

        public object Get(string routeId) =>
            Assert.Single(routes, route => Property<string>(route, "RouteId") == routeId);

        public bool IsRegistered(string routeId, AuthorityRouteKind kind) => routes.Any(route =>
            Property<string>(route, "RouteId") == routeId && Property<AuthorityRouteKind>(route, "Kind") == kind);

        public void Update(TimeSpan frameTime) { }
        public void Dispose() { }
    }

    private sealed class NoOpRouteHandle<TIntent, TResult> : IAuthorityRouteHandle<TIntent, TResult>
        where TResult : IMessage
    {
        public AuthorityRequestLifecycle Lifecycle => null!;
        public AuthorityRequestTicket<TResult> Submit(
            TIntent intent,
            Action<AuthorityClientOutcome<TResult>> completion = null) => throw new NotSupportedException();
        public AuthorityClientOutcome<TResult> SubmitBlocking(TIntent intent) => throw new NotSupportedException();
        public void Poll() { }
        public void CancelAll(string reasonCode) { }
        public void Dispose() { }
    }
}
