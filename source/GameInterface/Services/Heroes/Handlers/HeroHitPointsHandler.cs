using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Heroes.Extensions;
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
        // Refusals below answer with the hero's AUTHORITATIVE health, never an echo of what the
        // client asked for. The client already applied its own value locally - the generated
        // AutoSync prefix is a void Harmony prefix and cannot skip the setter - and the server only
        // replicates HitPoints when ITS value changes, which a refusal means it did not. Echoing the
        // request back therefore left a refused client stranded on its own number with nothing that
        // would ever correct it. Live: a player sat at 1% and wounded, unable to fight, while the
        // server had them at full health.
        if (!IsReportableBy(hero, party, context))
            return Reply(context.Header, AuthorityResultStatus.Unauthorized, hero.StringId, hero.HitPoints, "hero-not-owned");
        if (party.Party.MapEvent == null || !objectManager.TryGetId(party.Party.MapEvent, out var mapEventId) ||
            !string.Equals(mapEventId, request.MapEventId, StringComparison.Ordinal))
            return Reply(context.Header, AuthorityResultStatus.StaleState, hero.StringId, hero.HitPoints, "battle-not-current");
        if (hero.HitPoints != request.ExpectedHitPoints)
            return Reply(context.Header, AuthorityResultStatus.StaleState, hero.StringId, hero.HitPoints, "hit-points-stale");

        hero.HitPoints = request.HitPoints;
        if (hero.HitPoints != request.HitPoints)
            return Reply(context.Header, AuthorityResultStatus.ExecutionFailed, hero.StringId, hero.HitPoints, "hit-points-not-applied");
        return Reply(context.Header, AuthorityResultStatus.Accepted, hero.StringId, hero.HitPoints, null, statePublished: true);
    }

    /// <summary>
    /// Whether this player may report health for <paramref name="hero"/>: their own hero, or a
    /// companion or family member travelling in their party.
    /// </summary>
    /// <remarks>
    /// This deliberately mirrors the client's <c>HeroExtensions.IsHealthControlledByThisInstance</c>,
    /// which forwards health for the player's own hero AND for non-player heroes in the party they
    /// control. The host previously authorised only the first, so every companion damage report was
    /// refused as "hero-not-owned" by construction — the client cannot help sending them, and the
    /// host could never accept them. A refusal used to strand the reporting client on its own value;
    /// that is fixed separately, but the asymmetry itself was the reason refusals happened at all.
    /// <para>
    /// Another player's hero stays out of scope even when it stands in this party: that hero's own
    /// client is the only peer allowed to report for it.
    /// </para>
    /// </remarks>
    private static bool IsReportableBy(Hero hero, MobileParty party, AuthorityServerContext context)
    {
        if (string.Equals(hero.StringId, context.Player.HeroId, StringComparison.Ordinal)) return true;
        if (hero.IsPlayerHero()) return false;

        return ReferenceEquals(hero.PartyBelongedTo, party);
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

    private void PresentTerminal(AuthorityClientOutcome<NetworkHeroHitPointsChangeResult> outcome)
    {
        if (outcome.Applied) return;

        Logger.Warning("Hero health route did not commit. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);

        // Reconcile rather than just report. Whatever the refusal reason, this client is now holding
        // a health value the server never accepted, and nothing else will correct it: HitPoints
        // replicates on server-side CHANGE, and the server did not change. Snap to the authoritative
        // value the refusal carries. Wrapped in AllowedThread so the write is treated as a
        // server-approved apply and is not forwarded straight back as a new request.
        NetworkHeroHitPointsChangeResult result = outcome.Result;
        if (string.IsNullOrEmpty(result.HeroId)) return;
        if (!objectManager.TryGetObject<Hero>(result.HeroId, out var hero) || hero == null) return;
        if (hero.HitPoints == result.HitPoints) return;

        Logger.Information(
            "Reconciling {HeroId} to the host's health {HitPoints} after a refused report (was {Local})",
            result.HeroId, result.HitPoints, hero.HitPoints);

        using (new AllowedThread())
        {
            hero.HitPoints = result.HitPoints;
        }
    }
}
