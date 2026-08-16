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
/// Owner-scoped route for health changes made by a client's hero or companions. The peer selects a
/// health value only; the server derives the player and proves that the named hero is in that player's party.
/// The regular synced HitPoints property remains the canonical state publication.
/// </summary>
public class HeroHitPointsHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<HeroHitPointsHandler>();
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<HeroHitPointsChangeRequested, NetworkHeroHitPointsChangeResult> route;

    public HeroHitPointsHandler(IMessageBroker messageBroker, IObjectManager objectManager,
        IPlayerManager playerManager, IModConfigAuthority configAuthority, INetworkConfig configuration,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        route = authorityRequestRouter.Register(
            AuthorityRoute<HeroHitPointsChangeRequested, NetworkHeroHitPointsChangeRequest,
                NetworkHeroHitPointsChangeResult>.Define(
                "hero.hit-points", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkHeroHitPointsChangeRequest(HeroId(intent.Hero), intent.HitPoints, header),
                request => request.Header, result => result.Header, ValidateWireShape,
                request => $"{request.HeroId}:{request.HitPoints}", ValidateHeader, Execute,
                Terminal, ProbeClientCommit, _ => { }, PresentTerminal, configAuthority.IsTrustedServer,
                new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout, configuration.ObjectCreationTimeout, 1),
                failClosedOnApplyFailure: true, isExpectedClientResult: (request, result) =>
                    string.Equals(request.HeroId, result.HeroId, StringComparison.Ordinal) && request.HitPoints == result.HitPoints));
        this.playerManager = playerManager;
        messageBroker.Subscribe<HeroHitPointsChangeRequested>(HandleRequested);
    }

    private readonly IPlayerManager playerManager;

    public void Dispose() => route.Dispose();

    private void HandleRequested(MessagePayload<HeroHitPointsChangeRequested> payload)
    {
        if (ModInformation.IsServer) return;
        route.Submit(payload.What);
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkHeroHitPointsChangeRequest request) =>
        string.IsNullOrWhiteSpace(request.HeroId) || request.HeroId.Length > 256
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
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var party) || party == null)
            return Reply(context.Header, AuthorityResultStatus.Rejected, request.HeroId, request.HitPoints, "player-party-missing");
        if (!objectManager.TryGetObject<Hero>(request.HeroId, out var hero) || hero == null)
            return Reply(context.Header, AuthorityResultStatus.Rejected, request.HeroId, request.HitPoints, "hero-not-found");
        if (!OwnsHero(context, party, hero))
            return Reply(context.Header, AuthorityResultStatus.Unauthorized, request.HeroId, request.HitPoints, "hero-not-owned");

        hero.HitPoints = request.HitPoints;
        if (hero.HitPoints != request.HitPoints)
            return Reply(context.Header, AuthorityResultStatus.ExecutionFailed, hero.StringId, hero.HitPoints, "hit-points-not-applied");
        return Reply(context.Header, AuthorityResultStatus.Accepted, hero.StringId, hero.HitPoints, null, statePublished: true);
    }

    private static bool OwnsHero(AuthorityServerContext context, MobileParty party, Hero hero)
    {
        if (string.Equals(hero.StringId, context.Player.HeroId, StringComparison.Ordinal)) return true;
        for (int index = 0; index < party.MemberRoster.Count; index++)
        {
            var element = party.MemberRoster.GetElementCopyAtIndex(index);
            if (element.Number > 0 && element.Character?.HeroObject == hero) return true;
        }
        return false;
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
