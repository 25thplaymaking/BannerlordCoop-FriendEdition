using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace GameInterface.Services.Separatism;

internal sealed class SeparatismRecruitmentHandler : IHandler
{
    internal const string ModuleId = "Separatism";
    internal const string Operation = "RecruitFallenClan";
    private static readonly ILogger Logger = LogManager.GetLogger<SeparatismRecruitmentHandler>();

    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISeparatismCampaignService service;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly IAuthorityRouteHandle<RecruitmentIntent, NetworkSeparatismRecruitmentResult> route;

    public SeparatismRecruitmentHandler(IObjectManager objectManager, IPlayerManager playerManager,
        ISeparatismCampaignService service, IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry, IAuthorityRequestRouter authorityRequestRouter)
    {
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.service = service;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.authorityRequestRouter = authorityRequestRouter;
        route = authorityRequestRouter.Register(
            AuthorityRoute<RecruitmentIntent, NetworkRequestSeparatismRecruitment,
                NetworkSeparatismRecruitmentResult>.Define(
                SeparatismRecruitmentProtocol.RouteId, AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestSeparatismRecruitment(header, intent.ExpectedFactionChangeTicks,
                    intent.ExpectedKingdomId, intent.TargetClanId, intent.TargetHeroId),
                request => request.Header, result => result.Header, SeparatismRecruitmentProtocol.ValidateRequest,
                SeparatismRecruitmentProtocol.StructuralKey, ValidateHeader, Execute, CreateTerminalResult,
                ProbeClientCommit, _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedResult));
    }

    public void Dispose() => route.Dispose();

    internal bool IsRouteReady => authorityRequestRouter.IsRegistered(
        SeparatismRecruitmentProtocol.RouteId, AuthorityRouteKind.Command) &&
        configAuthority.TryGetCurrent(out var config) && capabilityRegistry.IsReadyFor(config.SessionId);

    internal bool TryRequest(Hero actor, Hero target)
    {
        if (!ModInformation.IsClient || !IsRouteReady || !capabilityRegistry.IsEnabled(ModuleId, Operation) ||
            !CanOffer(actor, target) || !objectManager.TryGetId(actor.Clan.Kingdom, out var kingdomId) ||
            !objectManager.TryGetId(target.Clan, out var targetClanId) || !objectManager.TryGetId(target, out var targetHeroId))
            return false;

        route.Submit(new RecruitmentIntent(target.Clan.LastFactionChangeTime.NumTicks, kingdomId, targetClanId, targetHeroId));
        return true;
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config");
    }

    private AuthorityServerReply<NetworkSeparatismRecruitmentResult> Execute(
        AuthorityServerContext context, NetworkRequestSeparatismRecruitment request)
    {
        if (!capabilityRegistry.IsEnabled(ModuleId, Operation))
            return Reject(context.Header, request, AuthorityResultStatus.Unavailable, "capability-unavailable");
        if (!objectManager.TryGetObject<Hero>(context.Player.HeroId, out var actor) ||
            !objectManager.TryGetObject<Clan>(context.Player.ClanId, out var actorClan) || actor.Clan != actorClan)
            return Reject(context.Header, request, AuthorityResultStatus.Unauthorized, "actor-mismatch");

        Kingdom actorKingdom = actorClan.Kingdom;
        if (actorKingdom == null || actorKingdom.Leader != actor || actorKingdom.RulingClan != actorClan ||
            !objectManager.TryGetId(actorKingdom, out var actorKingdomId))
            return Reject(context.Header, request, AuthorityResultStatus.Rejected, "actor-ineligible");
        if (!string.Equals(actorKingdomId, request.ExpectedKingdomId, StringComparison.Ordinal))
            return Reject(context.Header, request, AuthorityResultStatus.StaleState, "kingdom-changed");
        if (!objectManager.TryGetObject<Clan>(request.TargetClanId, out var targetClan) ||
            !objectManager.TryGetObject<Hero>(request.TargetHeroId, out var targetHero) ||
            targetClan.Leader != targetHero || targetHero.Clan != targetClan)
            return Reject(context.Header, request, AuthorityResultStatus.Rejected, "target-ineligible");

        long currentTicks = targetClan.LastFactionChangeTime.NumTicks;
        if (currentTicks != request.ExpectedFactionChangeTicks || targetClan.Kingdom != null)
            return Result(context.Header, request, AuthorityResultStatus.StaleState, "target-changed", actorKingdomId,
                targetClan.Kingdom == actorKingdom ? actorKingdomId : null, currentTicks, false);
        if (targetClan.IsMinorFaction || targetHero.MapFaction?.Leader != targetHero)
            return Reject(context.Header, request, AuthorityResultStatus.Rejected, "target-ineligible");
        if (FactionManager.IsAtWarAgainstFaction(targetHero.MapFaction, actor.MapFaction))
            return Reject(context.Header, request, AuthorityResultStatus.Rejected, "at-war");

        try
        {
            bool applied = service.TryRecruitFallenClan(actor, targetClan);
            long committedTicks = targetClan.LastFactionChangeTime.NumTicks;
            bool exactCommitted = targetClan.Kingdom == actorKingdom && actorKingdom.Clans.Contains(targetClan) &&
                targetClan.Leader == targetHero && targetHero.Clan == targetClan;
            if (applied && exactCommitted)
                return Result(context.Header, request, AuthorityResultStatus.Accepted, null, actorKingdomId,
                    actorKingdomId, committedTicks, true);
            if (!applied && targetClan.Kingdom == null && !actorKingdom.Clans.Contains(targetClan) &&
                committedTicks == request.ExpectedFactionChangeTicks)
                return Result(context.Header, request, AuthorityResultStatus.ExecutionFailed, "recruitment-failed",
                    actorKingdomId, null, committedTicks, false);
            return IsolateAfterAmbiguousMutation(context, request, actorKingdomId, targetClan,
                "membership-postcondition", null);
        }
        catch (Exception exception)
        {
            // The service only lets an exception escape when its rollback path itself failed. The
            // observable membership can look restored while an internal collection/publication is
            // not, so this remains fail-closed rather than treating it as a normal rejection.
            return IsolateAfterAmbiguousMutation(context, request, actorKingdomId, targetClan,
                "recruitment-rollback-threw", exception);
        }
    }

    private AuthorityServerReply<NetworkSeparatismRecruitmentResult> IsolateAfterAmbiguousMutation(
        AuthorityServerContext context, NetworkRequestSeparatismRecruitment request, string actorKingdomId,
        Clan targetClan, string stage, Exception exception)
    {
        Logger.Fatal(exception, "Ambiguous Separatism recruitment mutation; disconnecting campaign peers. Route={Route} RequestId={RequestId} Stage={Stage}",
            context.RouteId, context.Header.RequestId, stage);
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player) || !playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            try { peer.Disconnect(); }
            catch (Exception disconnectException)
            {
                Logger.Fatal(disconnectException, "Could not isolate peer after Separatism mutation ambiguity. Route={Route} RequestId={RequestId}",
                    context.RouteId, context.Header.RequestId);
            }
        }
        return new AuthorityServerReply<NetworkSeparatismRecruitmentResult>(
            CreateResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, "recruitment-isolated",
                actorKingdomId, targetClan.Kingdom == null ? null : actorKingdomId,
                targetClan.LastFactionChangeTime.NumTicks), false, suppressReply: true);
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkSeparatismRecruitmentResult result)
    {
        if (!SeparatismRecruitmentProtocol.IsResultShapeValid(result) || result.Header.Status != AuthorityResultStatus.Accepted ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            !string.Equals(config.SessionId, result.Header.SessionId, StringComparison.Ordinal) ||
            config.Revision != result.Header.CommittedRevision ||
            !string.Equals(result.ExpectedKingdomId, result.CommittedKingdomId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject<Kingdom>(result.CommittedKingdomId, out var kingdom) ||
            !objectManager.TryGetObject<Clan>(result.TargetClanId, out var clan) ||
            !objectManager.TryGetObject<Hero>(result.TargetHeroId, out var hero))
            return AuthorityCommitProbeResult.Invalid;

        return clan.Kingdom == kingdom && kingdom.Clans.Contains(clan) && clan.Leader == hero && hero.Clan == clan &&
               clan.LastFactionChangeTime.NumTicks == result.CommittedFactionChangeTicks
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static bool IsExpectedResult(NetworkRequestSeparatismRecruitment request,
        NetworkSeparatismRecruitmentResult result) => SeparatismRecruitmentProtocol.IsResultShapeValid(result) &&
        request.AuthorityRequestId == result.Header.RequestId &&
        string.Equals(request.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        request.ExpectedConfigRevision == result.Header.CommittedRevision &&
        string.Equals(request.CommandDigest, result.CommandDigest, StringComparison.Ordinal) &&
        string.Equals(request.ExpectedKingdomId, result.ExpectedKingdomId, StringComparison.Ordinal) &&
        string.Equals(request.ExpectedKingdomId, result.CommittedKingdomId, StringComparison.Ordinal) &&
        string.Equals(request.TargetClanId, result.TargetClanId, StringComparison.Ordinal) &&
        string.Equals(request.TargetHeroId, result.TargetHeroId, StringComparison.Ordinal) &&
        request.ExpectedFactionChangeTicks == result.ExpectedFactionChangeTicks;

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkSeparatismRecruitmentResult> outcome) =>
        GameThread.RunSafe(() => MBInformationManager.AddQuickInformation(outcome.Applied
            ? new TextObject("{=coop_separatism_recruitment_accepted}The fallen clan has sworn allegiance to your kingdom.")
            : FailureText(outcome.ReasonCode)), context: nameof(SeparatismRecruitmentHandler));

    private static TextObject FailureText(string reasonCode) => reasonCode switch
    {
        "at-war" => new TextObject("{=coop_separatism_recruitment_war}The clan cannot join while your factions are at war."),
        "stale-config" or "kingdom-changed" or "target-changed" => new TextObject("{=coop_separatism_recruitment_stale}The clan's situation changed before the agreement could be completed."),
        _ => new TextObject("{=coop_separatism_recruitment_failed}The fallen clan could not join your kingdom."),
    };

    private static AuthorityServerReply<NetworkSeparatismRecruitmentResult> Reject(AuthorityRequestHeader header,
        NetworkRequestSeparatismRecruitment request, AuthorityResultStatus status, string reasonCode) =>
        Result(header, request, status, reasonCode, request.ExpectedKingdomId, null,
            request.ExpectedFactionChangeTicks, false);

    private static AuthorityServerReply<NetworkSeparatismRecruitmentResult> Result(AuthorityRequestHeader header,
        NetworkRequestSeparatismRecruitment request, AuthorityResultStatus status, string reasonCode,
        string expectedKingdomId, string committedKingdomId, long committedTicks, bool statePublished) =>
        new(CreateResult(header, request, status, reasonCode, expectedKingdomId, committedKingdomId, committedTicks), statePublished);

    private static NetworkSeparatismRecruitmentResult CreateResult(AuthorityRequestHeader header,
        NetworkRequestSeparatismRecruitment request, AuthorityResultStatus status, string reasonCode,
        string expectedKingdomId, string committedKingdomId, long committedTicks) =>
        new(new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
            request.CommandDigest, expectedKingdomId, committedKingdomId, request.TargetClanId, request.TargetHeroId,
            request.ExpectedFactionChangeTicks, Math.Max(0, committedTicks));

    private static NetworkSeparatismRecruitmentResult CreateTerminalResult(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reasonCode) => new(new AuthorityResultHeader(
            header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
            "terminal", null, null, null, null, 0, 0);

    private bool CanOffer(Hero actor, Hero target)
    {
        Kingdom actorKingdom = actor?.Clan?.Kingdom;
        Clan targetClan = target?.Clan;
        return SeparatismConversationPolicy.CanOfferFallenClanRecruitment(true, actorKingdom != null,
            actorKingdom?.Leader == actor, target != null && targetClan != null, targetClan?.Kingdom == null,
            targetClan?.IsMinorFaction == false, target?.MapFaction?.Leader == target,
            target != null && actor != null && !FactionManager.IsAtWarAgainstFaction(target.MapFaction, actor.MapFaction));
    }

    private readonly struct RecruitmentIntent
    {
        public RecruitmentIntent(long expectedFactionChangeTicks, string expectedKingdomId, string targetClanId,
            string targetHeroId)
        {
            ExpectedFactionChangeTicks = expectedFactionChangeTicks;
            ExpectedKingdomId = expectedKingdomId;
            TargetClanId = targetClanId;
            TargetHeroId = targetHeroId;
        }

        public long ExpectedFactionChangeTicks { get; }
        public string ExpectedKingdomId { get; }
        public string TargetClanId { get; }
        public string TargetHeroId { get; }
    }
}

internal sealed class SeparatismCapabilitySource : IWorkshopCapabilitySource
{
    private readonly IModConfig modConfig;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRequestRouter authorityRequestRouter;

    public SeparatismCapabilitySource(IModConfig modConfig, IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.modConfig = modConfig;
        this.configAuthority = configAuthority;
        this.authorityRequestRouter = authorityRequestRouter;
    }

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        SeparatismOptionsData data = modConfig.Data?.ModOptions?.Separatism;
        bool optionEnabled = data?.Enabled ?? ModConfigProvider.ModOptions.Separatism.Enabled;
        bool routeReady = authorityRequestRouter.IsRegistered(SeparatismRecruitmentProtocol.RouteId,
            AuthorityRouteKind.Command) && configAuthority.TryGetCurrent(out _);
        bool enabled = SeparatismCapabilityPolicy.AllowRecruitment(optionEnabled, routeReady);
        yield return new WorkshopCapability(SeparatismRecruitmentHandler.ModuleId,
            SeparatismRecruitmentHandler.Operation, enabled,
            enabled ? string.Empty : "Separatism is disabled or its authoritative recruitment route is unavailable.");
    }
}
