using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.Alleys.Handlers;
using GameInterface.Services.Alleys.Interfaces;
using GameInterface.Services.Alleys.Messages;
using GameInterface.Services.Armies.Handlers;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.TroopRosters.Data;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

public sealed class AlleyArmyAuthorityRouteEvidenceTests
{
    private static readonly AuthorityRequestHeader Header =
        new(1, "0123456789abcdef0123456789abcdef", 73, 11);

    [Fact]
    public void AlleyRoutes_RegisterCompiledCommandContracts_AndOwnCorrelationHeaders()
    {
        var router = CaptureAlleyRoutes();
        var cases = new[]
        {
            Case("alley.acquire", new RequestAcquireAlley("alley", "owner", "overseer", EmptyRoster, Header)),
            Case("alley.clear", new RequestClearAlley("alley", Header)),
            Case("alley.abandon", new RequestAbandonAlley("alley", true, Header)),
            Case("alley.overseer.change", new RequestChangeAlleyOverseer("alley", "overseer", Header)),
            Case("alley.garrison.transfer", new RequestSetAlleyGarrison("alley", EmptyRoster, Header)),
            Case("alley.recruit", new RequestRecruitAlleyTroops("alley", EmptyRoster, Header)),
        };

        AssertRouteContracts(router, cases, failClosedOnApplyFailure: true);
    }

    [Fact]
    public void AlleyRoutes_PreserveEnabledAndExplicitlyDisabledAuthoritySemantics()
    {
        var router = CaptureAlleyRoutes();
        var disabled = new[]
        {
            DisabledCase("alley.acquire", new RequestAcquireAlley("alley", "owner", "overseer", EmptyRoster, Header), "takeover-session-required"),
            DisabledCase("alley.clear", new RequestClearAlley("alley", Header), "clear-session-required"),
            DisabledCase("alley.garrison.transfer", new RequestSetAlleyGarrison("alley", EmptyRoster, Header), "server-transfer-required"),
            DisabledCase("alley.recruit", new RequestRecruitAlleyTroops("alley", EmptyRoster, Header), "server-derived-recruitment-required"),
        };

        foreach (var item in disabled)
        {
            AuthorityResultHeader result = Execute(router.Route(item.RouteId), item.Request);
            Assert.Equal(AuthorityResultStatus.Unavailable, result.Status);
            Assert.Equal(item.Reason, result.ReasonCode);
            AssertCorrelation(result);
        }

        foreach (var item in new[]
        {
            Case("alley.abandon", new RequestAbandonAlley("alley", true, Header)),
            Case("alley.overseer.change", new RequestChangeAlleyOverseer("alley", "overseer", Header)),
        })
        {
            AuthorityResultHeader result = Execute(router.Route(item.RouteId), item.Request);
            Assert.Equal(AuthorityResultStatus.Rejected, result.Status);
            Assert.Equal("invalid-owner", result.ReasonCode);
            AssertCorrelation(result);
        }
    }

    [Fact]
    public void ArmyRoutes_RegisterCompiledCommandContracts_AndOwnCorrelationHeaders()
    {
        var router = CaptureArmyRoutes();
        AssertRouteContracts(router, ArmyCases(), failClosedOnApplyFailure: false);
    }

    [Fact]
    public void ArmyRoutes_ReachServerActorAuthorization_InsteadOfRunningAsClientAuthoritativeCommands()
    {
        var router = CaptureArmyRoutes();

        foreach (var item in ArmyCases())
        {
            AuthorityResultHeader result = Execute(router.Route(item.RouteId), item.Request);
            Assert.Equal(AuthorityResultStatus.Rejected, result.Status);
            Assert.Equal("army-actor-missing", result.ReasonCode);
            AssertCorrelation(result);
        }
    }

    private static RouteCase[] ArmyCases() => new[]
    {
        Case("army.create", new RequestCreateArmy("kingdom", "settlement", "Besieger", new List<string> { "party" }, Header)),
        Case("army.invite", new RequestArmyInvite("army", "party", Header)),
        Case("army.invite.respond", new RequestArmyInviteResponse("army", true, Header)),
        Case("army.leave", new RequestLeaveArmy("army", Header)),
        Case("army.kick", new RequestKickArmyMember("army", "party", Header)),
        Case("army.boost-cohesion", new RequestBoostArmyCohesion("army", 5f, Header)),
        Case("army.objective.change", new RequestChangeArmyObjective("army", "settlement", true, Header)),
    };

    private static void AssertRouteContracts(CaptureRouter router, IEnumerable<RouteCase> cases, bool failClosedOnApplyFailure)
    {
        RouteCase[] expected = cases.ToArray();
        Assert.Equal(expected.Length, router.Routes.Count);
        Assert.Equal(expected.Select(x => x.RouteId).OrderBy(x => x), router.RouteIds.OrderBy(x => x));

        foreach (var item in expected)
        {
            object route = router.Route(item.RouteId);
            Type requestType = route.GetType().GetGenericArguments()[1];
            var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(requestType, typeof(AuthorityRouteAttribute));

            Assert.NotNull(attribute);
            Assert.Equal(item.RouteId, attribute.RouteId);
            Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
            Assert.Equal(AuthorityRouteKind.Command, Property<AuthorityRouteKind>(route, "Kind"));
            Assert.Equal(failClosedOnApplyFailure, Property<bool>(route, "FailClosedOnApplyFailure"));
            Assert.Null(InvokeDelegate(route, "ValidateWireShape", item.Request));
            Assert.False(string.IsNullOrWhiteSpace((string)InvokeDelegate(route, "BuildCommandKey", item.Request)));

            var requestHeader = (AuthorityRequestHeader)InvokeDelegate(route, "ReadRequestHeader", item.Request);
            Assert.Equal(Header.SessionId, requestHeader.SessionId);
            Assert.Equal(Header.RequestId, requestHeader.RequestId);
            Assert.Equal(Header.ExpectedRevision, requestHeader.ExpectedRevision);

            object terminal = InvokeDelegate(route, "CreateTerminalResult", Header, AuthorityResultStatus.Rejected, "evidence");
            var terminalHeader = (AuthorityResultHeader)InvokeDelegate(route, "ReadResultHeader", terminal);
            Assert.Equal(AuthorityResultStatus.Rejected, terminalHeader.Status);
            Assert.Equal("evidence", terminalHeader.ReasonCode);
            AssertCorrelation(terminalHeader);
        }
    }

    private static AuthorityResultHeader Execute(object route, IMessage request)
    {
        var player = new Player("controller", "hero", "party", "clan", "character");
        var context = new AuthorityServerContext(null, player, Header, Property<string>(route, "RouteId"));
        object reply = InvokeDelegate(route, "Execute", context, request);
        object result = reply.GetType().GetProperty("Result")!.GetValue(reply);
        return (AuthorityResultHeader)result.GetType().GetField("Header")!.GetValue(result);
    }

    private static CaptureRouter CaptureAlleyRoutes()
    {
        var router = new CaptureRouter();
        _ = new AlleyManagementHandler(
            new MessageBroker(),
            new Mock<IObjectManager>().Object,
            new Mock<INetwork>().Object,
            new Mock<ISessionAlleyPlayerDataInterface>().Object,
            new Mock<IAlleyCampaignBehaviorInterface>().Object,
            new Mock<IPlayerManager>().Object,
            new Mock<IModConfigAuthority>().Object,
            router);
        return router;
    }

    private static CaptureRouter CaptureArmyRoutes()
    {
        var router = new CaptureRouter();
        _ = new ArmyAuthorityHandler(
            new MessageBroker(),
            new Mock<IObjectManager>().Object,
            new Mock<INetwork>().Object,
            new Mock<IModConfigAuthority>().Object,
            new Mock<IPlayerManager>().Object,
            router);
        return router;
    }

    private static object InvokeDelegate(object route, string property, params object[] arguments) =>
        ((Delegate)route.GetType().GetProperty(property)!.GetValue(route)).DynamicInvoke(arguments);

    private static T Property<T>(object value, string property) =>
        (T)value.GetType().GetProperty(property)!.GetValue(value);

    private static void AssertCorrelation(AuthorityResultHeader result)
    {
        Assert.Equal(Header.SessionId, result.SessionId);
        Assert.Equal(Header.RequestId, result.RequestId);
        Assert.Equal(Header.ExpectedRevision, result.CommittedRevision);
    }

    private static RouteCase Case<TRequest>(string routeId, TRequest request) where TRequest : IMessage =>
        new(routeId, request);

    private static DisabledRouteCase DisabledCase<TRequest>(string routeId, TRequest request, string reason) where TRequest : IMessage =>
        new(routeId, request, reason);

    private static readonly TroopRosterElementData[] EmptyRoster = Array.Empty<TroopRosterElementData>();

    private readonly record struct RouteCase(string RouteId, IMessage Request);
    private readonly record struct DisabledRouteCase(string RouteId, IMessage Request, string Reason);

    private sealed class CaptureRouter : IAuthorityRequestRouter
    {
        public List<object> Routes { get; } = new();
        public IEnumerable<string> RouteIds => Routes.Select(x => Property<string>(x, "RouteId"));
        public int Priority => 0;

        public IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
            AuthorityRoute<TIntent, TRequest, TResult> route)
            where TRequest : IMessage
            where TResult : IMessage
        {
            Routes.Add(route);
            return null;
        }

        public object Route(string routeId) => Assert.Single(Routes, x => Property<string>(x, "RouteId") == routeId);
        public bool IsRegistered(string routeId, AuthorityRouteKind kind) =>
            Routes.Any(x => Property<string>(x, "RouteId") == routeId && Property<AuthorityRouteKind>(x, "Kind") == kind);
        public void Update(TimeSpan frameTime) { }
        public void Dispose() { }
    }
}
