using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.WorkshopMods.Core;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace GameInterface.Services.Separatism;

internal sealed class SeparatismRecruitmentHandler : IHandler
{
    internal const string ModuleId = "Separatism";
    internal const string Operation = "RecruitFallenClan";
    internal const bool RouteReady = true;

    private static readonly ILogger Logger = LogManager.GetLogger<SeparatismRecruitmentHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISeparatismCampaignService service;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    // Scope request numbers to the connection so a restarted client can reconnect with a fresh
    // sequence while duplicate deliveries on the same authenticated connection remain idempotent.
    private readonly SeparatismRequestReplayLedger<NetPeer> requestLedger = new(64);
    private readonly ConcurrentDictionary<long, string> pendingClientRequests = new();
    private long nextClientRequestId;

    public SeparatismRecruitmentHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        ISeparatismCampaignService service,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.service = service;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;

        messageBroker.Subscribe<NetworkRequestSeparatismRecruitment>(HandleRequest);
        messageBroker.Subscribe<NetworkSeparatismRecruitmentResult>(HandleResult);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkRequestSeparatismRecruitment>(HandleRequest);
        messageBroker.Unsubscribe<NetworkSeparatismRecruitmentResult>(HandleResult);
    }

    internal bool TryRequest(Hero actor, Hero target)
    {
        if (!ModInformation.IsClient ||
            !capabilityRegistry.IsEnabled(ModuleId, Operation) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !CanOffer(actor, target) ||
            !objectManager.TryGetId(actor.Clan.Kingdom, out var kingdomId) ||
            !objectManager.TryGetId(target.Clan, out var targetClanId) ||
            !objectManager.TryGetId(target, out var targetHeroId))
            return false;

        long requestId = Interlocked.Increment(ref nextClientRequestId);
        var request = new NetworkRequestSeparatismRecruitment(
            config.SessionId,
            requestId,
            target.Clan.LastFactionChangeTime.NumTicks,
            kingdomId,
            targetClanId,
            targetHeroId);
        if (!pendingClientRequests.TryAdd(requestId, targetClanId)) return false;

        network.SendAll(request);
        return true;
    }

    private bool CanOffer(Hero actor, Hero target)
    {
        Kingdom actorKingdom = actor?.Clan?.Kingdom;
        Clan targetClan = target?.Clan;
        return SeparatismConversationPolicy.CanOfferFallenClanRecruitment(
            capabilityRegistry.IsEnabled(ModuleId, Operation),
            actorKingdom != null,
            actorKingdom?.Leader == actor,
            target != null && targetClan != null,
            targetClan?.Kingdom == null,
            targetClan?.IsMinorFaction == false,
            target?.MapFaction?.Leader == target,
            target != null && actor != null &&
                !FactionManager.IsAtWarAgainstFaction(target.MapFaction, actor.MapFaction));
    }

    private void HandleRequest(MessagePayload<NetworkRequestSeparatismRecruitment> payload)
    {
        if (ModInformation.IsClient || payload?.Who is not NetPeer peer) return;
        NetworkRequestSeparatismRecruitment request = payload.What;

        if (!SeparatismRecruitmentProtocol.IsRequestShapeValid(request))
        {
            Logger.Warning("Rejected malformed Separatism recruitment request from peer {Peer}", peer.Id);
            peer.Disconnect();
            return;
        }

        if (!configAuthority.TryGetCurrent(out var config))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unavailable, request.ExpectedRevision);
            return;
        }
        if (!string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            Send(peer, request, SeparatismRecruitmentStatus.StaleSession, request.ExpectedRevision, config.SessionId);
            return;
        }
        if (!playerManager.TryGetPlayer(peer, out _))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unauthorized, request.ExpectedRevision);
            return;
        }
        if (!capabilityRegistry.IsEnabled(ModuleId, Operation))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unavailable, request.ExpectedRevision);
            return;
        }

        GameThread.RunSafe(
            () => ProcessRequest(peer, request),
            context: nameof(SeparatismRecruitmentHandler));
    }

    private void ProcessRequest(
        NetPeer peer,
        NetworkRequestSeparatismRecruitment request)
    {
        if (!configAuthority.TryGetCurrent(out var config))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unavailable, request.ExpectedRevision);
            return;
        }
        if (!string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            Send(peer, request, SeparatismRecruitmentStatus.StaleSession, request.ExpectedRevision, config.SessionId);
            return;
        }
        if (!capabilityRegistry.IsEnabled(ModuleId, Operation))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unavailable, request.ExpectedRevision);
            return;
        }
        if (!playerManager.TryGetPlayer(peer, out var player))
        {
            Send(peer, request, SeparatismRecruitmentStatus.Unauthorized, request.ExpectedRevision);
            return;
        }

        switch (requestLedger.Inspect(peer, request, out var replay))
        {
            case SeparatismReplayDecision.Replay:
                network.Send(peer, replay);
                return;
            case SeparatismReplayDecision.Conflict:
                Send(peer, request, SeparatismRecruitmentStatus.ConflictingRequest, request.ExpectedRevision);
                return;
            case SeparatismReplayDecision.Stale:
                Send(peer, request, SeparatismRecruitmentStatus.StaleRequest, request.ExpectedRevision);
                return;
        }

        SeparatismRecruitmentStatus status = ValidateAndApply(player, request, out long revision);
        var result = new NetworkSeparatismRecruitmentResult(
            request.SessionId,
            request.RequestId,
            status,
            request.TargetClanId,
            Math.Max(0, revision));
        requestLedger.Record(peer, request, result);
        network.Send(peer, result);
    }

    private SeparatismRecruitmentStatus ValidateAndApply(
        Player player,
        NetworkRequestSeparatismRecruitment request,
        out long revision)
    {
        revision = request.ExpectedRevision;
        if (!objectManager.TryGetObject<Hero>(player.HeroId, out var actor) ||
            !objectManager.TryGetObject<Clan>(player.ClanId, out var actorClan) ||
            actor.Clan != actorClan)
            return SeparatismRecruitmentStatus.Unauthorized;

        Kingdom actorKingdom = actorClan.Kingdom;
        if (actorKingdom == null ||
            actorKingdom.Leader != actor ||
            actorKingdom.RulingClan != actorClan ||
            !objectManager.TryGetId(actorKingdom, out var actorKingdomId))
            return SeparatismRecruitmentStatus.IneligibleActor;
        if (!string.Equals(actorKingdomId, request.ExpectedKingdomId, StringComparison.Ordinal))
            return SeparatismRecruitmentStatus.StaleState;

        if (!objectManager.TryGetObject<Clan>(request.TargetClanId, out var targetClan) ||
            !objectManager.TryGetObject<Hero>(request.TargetHeroId, out var targetHero) ||
            targetClan.Leader != targetHero ||
            targetHero.Clan != targetClan)
            return SeparatismRecruitmentStatus.IneligibleTarget;

        revision = targetClan.LastFactionChangeTime.NumTicks;
        if (revision != request.ExpectedRevision || targetClan.Kingdom != null)
            return SeparatismRecruitmentStatus.StaleState;
        if (targetClan.IsMinorFaction || targetHero.MapFaction?.Leader != targetHero)
            return SeparatismRecruitmentStatus.IneligibleTarget;
        if (FactionManager.IsAtWarAgainstFaction(targetHero.MapFaction, actor.MapFaction))
            return SeparatismRecruitmentStatus.AtWar;

        bool accepted = service.TryRecruitFallenClan(actor, targetClan);
        revision = targetClan.LastFactionChangeTime.NumTicks;
        return accepted
            ? SeparatismRecruitmentStatus.Accepted
            : SeparatismRecruitmentStatus.Failed;
    }

    private void HandleResult(MessagePayload<NetworkSeparatismRecruitmentResult> payload)
    {
        if (!ModInformation.IsClient ||
            payload?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer) ||
            !SeparatismRecruitmentProtocol.IsResultShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            !pendingClientRequests.TryRemove(payload.What.RequestId, out var targetClanId) ||
            !string.Equals(targetClanId, payload.What.TargetClanId, StringComparison.Ordinal))
            return;

        NetworkSeparatismRecruitmentResult result = payload.What;
        GameThread.RunSafe(
            () => MBInformationManager.AddQuickInformation(ResultText(result.Status)),
            context: nameof(SeparatismRecruitmentHandler));
    }

    private void Send(
        NetPeer peer,
        NetworkRequestSeparatismRecruitment request,
        SeparatismRecruitmentStatus status,
        long revision,
        string sessionId = null) =>
        network.Send(peer, new NetworkSeparatismRecruitmentResult(
            sessionId ?? request.SessionId,
            request.RequestId,
            status,
            request.TargetClanId,
            Math.Max(0, revision)));

    private static TextObject ResultText(SeparatismRecruitmentStatus status) => status switch
    {
        SeparatismRecruitmentStatus.Accepted =>
            new TextObject("{=coop_separatism_recruitment_accepted}The fallen clan has sworn allegiance to your kingdom."),
        SeparatismRecruitmentStatus.StaleState or SeparatismRecruitmentStatus.StaleRequest =>
            new TextObject("{=coop_separatism_recruitment_stale}The clan's situation changed before the agreement could be completed."),
        SeparatismRecruitmentStatus.AtWar =>
            new TextObject("{=coop_separatism_recruitment_war}The clan cannot join while your factions are at war."),
        _ => new TextObject("{=coop_separatism_recruitment_failed}The fallen clan could not join your kingdom."),
    };
}

internal sealed class SeparatismCapabilitySource : IWorkshopCapabilitySource
{
    private readonly IModConfig modConfig;

    public SeparatismCapabilitySource(IModConfig modConfig)
    {
        this.modConfig = modConfig;
    }

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        SeparatismOptionsData data = modConfig.Data?.ModOptions?.Separatism;
        bool optionEnabled = data?.Enabled ?? ModConfigProvider.ModOptions.Separatism.Enabled;
        bool enabled = SeparatismCapabilityPolicy.AllowRecruitment(
            optionEnabled,
            SeparatismRecruitmentHandler.RouteReady);
        yield return new WorkshopCapability(
            SeparatismRecruitmentHandler.ModuleId,
            SeparatismRecruitmentHandler.Operation,
            enabled,
            enabled ? string.Empty : "Separatism is disabled or its authoritative recruitment route is unavailable.");
    }
}
