using Common;
using Common.Messaging;
using Common.Network;
using Common.Tests.Utils;
using Coop.Core.Client.Services.BattleRetreat.Handlers;
using Coop.Core.Client.Services.BattleRetreat.Messages;
using Coop.Core.Client.Services.MobileParties.Handlers;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Client.Services.SiegeEvents.Handlers;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Services.BattleRetreat.Messages;
using Coop.Core.Server.Services.MobileParties.Messages;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.UI.Interfaces;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Coop.Tests.Services.AuthorityRequests;

public sealed class BattleSiegeAuthorityRouteEvidenceTests
{
    private static readonly AuthorityRequestHeader RequestHeader = new(1, "session-a", 37, 8);
    private static readonly AuthorityResultHeader AcceptedHeader = new(
        RequestHeader.SessionId,
        RequestHeader.RequestId,
        AuthorityResultStatus.Accepted,
        RequestHeader.ExpectedRevision,
        null);

    [Fact]
    public void SettlementEncounterRoutes_DerivePartyAndRequireExactCorrelatedState()
    {
        var router = new CapturingAuthorityRouter();
        using var broker = new TestMessageBroker();
        using var handler = new ClientSettlementExitEnterHandler(
            broker,
            Mock.Of<IObjectManager>(),
            Mock.Of<ISettlementInterface>(),
            Mock.Of<IModConfigAuthority>(),
            router);

        object startRoute = router.Get("settlement.encounter.start");
        object endRoute = router.Get("settlement.encounter.end");
        Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(startRoute, "Kind"));
        Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(endRoute, "Kind"));
        Assert.False(Property<bool>(startRoute, "FailClosedOnApplyFailure"));
        Assert.False(Property<bool>(endRoute, "FailClosedOnApplyFailure"));
        Assert.Null(typeof(NetworkRequestStartSettlementEncounter).GetProperty("PartyId"));
        Assert.Null(typeof(NetworkRequestStartSettlementEncounter).GetField("PartyId"));
        Assert.Null(typeof(NetworkRequestEndSettlementEncounter).GetProperty("PartyId"));
        Assert.Null(typeof(NetworkRequestEndSettlementEncounter).GetField("PartyId"));

        var startRequest = new NetworkRequestStartSettlementEncounter("town-a", RequestHeader);
        Assert.True(IsExpected(startRoute, startRequest,
            new NetworkStartSettlementEncounter("party-a", "town-a",
                SettlementEncounterStartMode.EnteredSettlement, AcceptedHeader)));
        Assert.False(IsExpected(startRoute, startRequest,
            new NetworkStartSettlementEncounter("party-a", "town-b",
                SettlementEncounterStartMode.EnteredSettlement, AcceptedHeader)));

        var endRequest = new NetworkRequestEndSettlementEncounter("town-a", RequestHeader);
        Assert.True(IsExpected(endRoute, endRequest,
            new NetworkSettlementEncounterLeaveResult("party-a", "town-a",
                SettlementEncounterLeaveOutcome.Applied, AcceptedHeader)));
        Assert.False(IsExpected(endRoute, endRequest,
            new NetworkSettlementEncounterLeaveResult("party-a", "town-b",
                SettlementEncounterLeaveOutcome.Applied, AcceptedHeader)));
    }

    [Fact]
    public void BattleRetreatRoutes_AreTypedFailClosedAndRequireExactDomainProof()
    {
        var router = new CapturingAuthorityRouter();
        using var broker = new TestMessageBroker();
        using var handler = new ClientBattleRetreatHandler(
            broker,
            Mock.Of<IObjectManager>(),
            Mock.Of<IModConfigAuthority>(),
            router);

        object retreatRoute = router.Get("battle.retreat");
        AssertCommandRoute<NetworkRequestBattleRetreat, NetworkBattleRetreatResolved>(retreatRoute);
        var retreatRequest = new NetworkRequestBattleRetreat("party-a", "battle-a", RequestHeader);
        Assert.True(IsExpected(retreatRoute, retreatRequest,
            new NetworkBattleRetreatResolved("party-a", true, Array.Empty<string>(), AcceptedHeader)));
        Assert.False(IsExpected(retreatRoute, retreatRequest,
            new NetworkBattleRetreatResolved("party-b", true, Array.Empty<string>(), AcceptedHeader)));
        Assert.False(IsExpected(retreatRoute, retreatRequest,
            new NetworkBattleRetreatResolved("party-a", false, Array.Empty<string>(), AcceptedHeader)));

        object breakInRoute = router.Get("battle.break-in-casualties");
        AssertCommandRoute<NetworkRequestBreakInCasualties, NetworkBreakInCasualtiesResolved>(breakInRoute);
        var breakInRequest = new NetworkRequestBreakInCasualties("party-a", "town-a", RequestHeader);
        Assert.True(IsExpected(breakInRoute, breakInRequest,
            new NetworkBreakInCasualtiesResolved("party-a", "town-a", 14, true, AcceptedHeader)));
        Assert.False(IsExpected(breakInRoute, breakInRequest,
            new NetworkBreakInCasualtiesResolved("party-a", "town-b", 14, true, AcceptedHeader)));
        Assert.False(IsExpected(breakInRoute, breakInRequest,
            new NetworkBreakInCasualtiesResolved("party-a", "town-a", -1, true, AcceptedHeader)));

        Assert.Equal(RequestHeader, retreatRequest.Header);
        Assert.Equal(RequestHeader, breakInRequest.Header);
    }

    [Fact]
    public void SiegeRoutes_AreTypedFailClosedAndRequireExactAssaultAndAftermathProof()
    {
        var router = new CapturingAuthorityRouter();
        using var broker = new TestMessageBroker();
        var config = new Mock<INetworkConfig>();
        config.SetupGet(value => value.ObjectCreationTimeout).Returns(TimeSpan.FromSeconds(1));
        // siege.assault budgets its APPLY against mission entry, not object creation: an assault
        // opens a siege scene, and timing that out fails closed and cancels the session.
        config.SetupGet(value => value.MissionEntryTimeout).Returns(TimeSpan.FromSeconds(60));
        using var entryHandler = new ClientSiegeEntryHandler(
            broker,
            Mock.Of<INetwork>(),
            config.Object,
            Mock.Of<IObjectManager>(),
            Mock.Of<ISiegeEventInterface>(),
            Mock.Of<IModConfigAuthority>(),
            router,
            Mock.Of<ILoadingInterface>());
        using var aftermathHandler = new ClientSiegeAftermathHandler(
            broker,
            Mock.Of<IObjectManager>(),
            Mock.Of<ISiegeEventInterface>(),
            Mock.Of<IModConfigAuthority>(),
            router);

        object assaultRoute = router.Get("siege.assault");
        AssertCommandRoute<NetworkRequestSiegeAssault, NetworkSiegeAssaultApproved>(assaultRoute);
        var assaultRequest = new NetworkRequestSiegeAssault("party-a", "town-a", RequestHeader);
        Assert.True(IsExpected(assaultRoute, assaultRequest,
            new NetworkSiegeAssaultApproved(true, AcceptedHeader, "party-a", "town-a")));
        Assert.False(IsExpected(assaultRoute, assaultRequest,
            new NetworkSiegeAssaultApproved(true, AcceptedHeader, "party-a", "town-b")));

        object aftermathRoute = router.Get("siege.aftermath");
        AssertCommandRoute<NetworkRequestSiegeAftermath, NetworkSiegeAftermathApplied>(aftermathRoute);
        var aftermathRequest = new NetworkRequestSiegeAftermath(
            "party-a", "town-a", aftermathType: 2, aftermathId: "capture-7", header: RequestHeader);
        Assert.True(IsExpected(aftermathRoute, aftermathRequest,
            Aftermath("party-a", "town-a", 2, "capture-7")));
        Assert.False(IsExpected(aftermathRoute, aftermathRequest,
            Aftermath("party-a", "town-a", 2, "capture-8")));
        Assert.False(IsExpected(aftermathRoute, aftermathRequest,
            Aftermath("party-a", "town-a", 1, "capture-7")));

        Assert.Equal(RequestHeader, assaultRequest.Header);
        Assert.Equal(RequestHeader, aftermathRequest.Header);
    }

    private static NetworkSiegeAftermathApplied Aftermath(
        string partyId,
        string settlementId,
        int aftermathType,
        string aftermathId) => new(
            settlementId,
            aftermathType,
            RequestHeader.SessionId,
            RequestHeader.RequestId,
            aftermathId,
            partyId,
            generation: 1,
            header: AcceptedHeader);

    private static void AssertCommandRoute<TRequest, TResult>(object route)
        where TRequest : IMessage
        where TResult : IMessage
    {
        Type routeType = route.GetType();
        Type[] arguments = routeType.GetGenericArguments();
        Assert.Equal(typeof(TRequest), arguments[1]);
        Assert.Equal(typeof(TResult), arguments[2]);
        Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(route, "Kind"));
        Assert.True(Property<bool>(route, "FailClosedOnApplyFailure"));

        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(TRequest), typeof(AuthorityRouteAttribute));
        Assert.NotNull(attribute);
        Assert.Equal(Property<string>(route, "RouteId"), attribute.RouteId);
        Assert.Equal(Property<AuthorityRouteKind>(route, "Kind"), attribute.Kind);
    }

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
