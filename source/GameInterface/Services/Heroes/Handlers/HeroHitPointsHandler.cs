using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Heroes.Handlers;

/// <summary>
/// Damage-only route for the requesting player's own hero. Healing and recovery are server-internal;
/// a report must name the current battle and match the server's current health before it can lower it.
/// </summary>
public class HeroHitPointsHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<HeroHitPointsHandler>();
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<DamageIntent, NetworkHeroHitPointsChangeResult> route;

    private readonly struct DamageIntent
    {
        public DamageIntent(string heroId, int hitPoints, int expectedHitPoints, string mapEventId)
        {
            HeroId = heroId;
            HitPoints = hitPoints;
            ExpectedHitPoints = expectedHitPoints;
            MapEventId = mapEventId;
        }
        public string HeroId { get; }
        public int HitPoints { get; }
        public int ExpectedHitPoints { get; }
        public string MapEventId { get; }
    }

    public HeroHitPointsHandler(IMessageBroker messageBroker, IObjectManager objectManager,
        IModConfigAuthority configAuthority, INetworkConfig configuration,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        route = authorityRequestRouter.Register(
            AuthorityRoute<DamageIntent, NetworkHeroHitPointsChangeRequest,
                NetworkHeroHitPointsChangeResult>.Define(
                "hero.damage-report", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkHeroHitPointsChangeRequest(intent.HeroId, intent.HitPoints,
                    intent.ExpectedHitPoints, intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateWireShape,
                request => $"{request.HeroId}:{request.MapEventId}:{request.ExpectedHitPoints}:{request.HitPoints}", ValidateHeader, Execute,
                Terminal, ProbeClientCommit, _ => { }, PresentTerminal, configAuthority.IsTrustedServer,
                new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout, configuration.ObjectCreationTimeout, 1),
                failClosedOnApplyFailure: true, isExpectedClientResult: (request, result) =>
                    string.Equals(request.HeroId, result.HeroId, StringComparison.Ordinal) && request.HitPoints == result.HitPoints));
        messageBroker.Subscribe<HeroHitPointsChangeRequested>(HandleRequested);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<HeroHitPointsChangeRequested>(HandleRequested);
        route.Dispose();
    }

    private void HandleRequested(MessagePayload<HeroHitPointsChangeRequested> payload)
    {
        if (ModInformation.IsServer) return;
        var hero = payload.What.Hero;
        if (hero == null || hero != Hero.MainHero || payload.What.HitPoints < 0 ||
            payload.What.HitPoints >= hero.HitPoints)
            return;
        var party = MobileParty.MainParty;
        if (party?.Party?.MapEvent == null || !objectManager.TryGetId(party.Party.MapEvent, out var mapEventId))
            return;
        route.Submit(new DamageIntent(HeroId(hero), payload.What.HitPoints, hero.HitPoints, mapEventId));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkHeroHitPointsChangeRequest request) =>
        string.IsNullOrWhiteSpace(request.HeroId) || string.IsNullOrWhiteSpace(request.MapEventId) ||
        request.HeroId.Length > 256 || request.MapEventId.Length > 256 || request.HitPoints < 0 ||
        request.HitPoints >= request.ExpectedHitPoints
            ? "hero-target-invalid" : null;

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private AuthorityServerReply<NetworkHeroHitPointsChangeResult> Execute(
        AuthorityServerContext context, NetworkHeroHitPointsChangeRequest request)
    {
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var party) || party?.Party == null)
            return Reply(context.Header, AuthorityResultStatus.Rejected, request.HeroId, request.HitPoints, "player-party-missing");
        if (!objectManager.TryGetObject<Hero>(request.HeroId, out var hero) || hero == null)
            return Reply(context.Header, AuthorityResultStatus.Rejected, request.HeroId, request.HitPoints, "hero-not-found");
        if (!string.Equals(hero.StringId, context.Player.HeroId, StringComparison.Ordinal))
            return Reply(context.Header, AuthorityResultStatus.Unauthorized, request.HeroId, request.HitPoints, "hero-not-owned");
        if (party.Party.MapEvent == null || !objectManager.TryGetId(party.Party.MapEvent, out var mapEventId) ||
            !string.Equals(mapEventId, request.MapEventId, StringComparison.Ordinal))
            return Reply(context.Header, AuthorityResultStatus.StaleState, request.HeroId, request.HitPoints, "battle-not-current");
        if (hero.HitPoints != request.ExpectedHitPoints)
            return Reply(context.Header, AuthorityResultStatus.StaleState, request.HeroId, request.HitPoints, "hit-points-stale");

        hero.HitPoints = request.HitPoints;
        if (hero.HitPoints != request.HitPoints)
            return Reply(context.Header, AuthorityResultStatus.ExecutionFailed, hero.StringId, hero.HitPoints, "hit-points-not-applied");
        return Reply(context.Header, AuthorityResultStatus.Accepted, hero.StringId, hero.HitPoints, null, statePublished: true);
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkHeroHitPointsChangeResult result)
    {
        if (!objectManager.TryGetObject<Hero>(result.HeroId, out var hero) || hero == null)
            return AuthorityCommitProbeResult.Pending;
        return hero.HitPoints == result.HitPoints ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static AuthorityServerReply<NetworkHeroHitPointsChangeResult> Reply(AuthorityRequestHeader header,
        AuthorityResultStatus status, string heroId, int hitPoints, string reason, bool statePublished = false) =>
        new(new NetworkHeroHitPointsChangeResult(header, status, heroId, hitPoints, reason), statePublished);

    private static NetworkHeroHitPointsChangeResult Terminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(header, status, null, 0, reason);

    private static string HeroId(Hero hero) => hero?.StringId;

    private static void PresentTerminal(AuthorityClientOutcome<NetworkHeroHitPointsChangeResult> outcome)
    {
        if (!outcome.Applied)
            Logger.Warning("Hero health route did not commit. Completion={Completion} Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }
}
