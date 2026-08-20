using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Messages;
using GameInterface.Services.Barters;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Hideouts.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Services.Hideouts.Handlers;

/// <summary>Applies client hideout menu consequences on the authoritative server.</summary>
internal sealed class HideoutCampaignConsequencesHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<HideoutCampaignConsequencesHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly INetworkConfig configuration;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<HideoutConsequenceIntent, NetworkHideoutCampaignConsequenceResult> consequenceRoute;
    private readonly ConcurrentDictionary<NetPeer, AssaultSession> assaultSessions = new();
    private AssaultSession replicaSession;

    public HideoutCampaignConsequencesHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        INetworkConfig configuration,
        ISendCoalescer sendCoalescer,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configuration = configuration;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;

        consequenceRoute = authorityRequestRouter.Register(
            AuthorityRoute<HideoutConsequenceIntent, NetworkHideoutCampaignConsequenceRequested,
                NetworkHideoutCampaignConsequenceResult>.Define(
                routeId: "hideout.campaign-consequence",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkHideoutCampaignConsequenceRequested(
                    header, intent.SettlementId, intent.Consequence, intent.AssaultSessionId, intent.ExpectedHideoutRevision),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateWireShape,
                buildCommandKey: request => string.Concat(
                    request.SettlementId, ":", (int)request.Consequence, ":",
                    request.AssaultSessionId, ":", request.ExpectedHideoutRevision),
                validateHeader: ValidateHeader,
                execute: ExecuteConsequence,
                createTerminalResult: CreateTerminalResult,
                probeClientCommit: ProbeClientCommit,
                requestResync: _ => { },
                presentTerminalOutcome: PresentTerminalOutcome,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: new AuthorityTimeoutPolicy(
                    configuration.ObjectCreationTimeout,
                    configuration.ObjectCreationTimeout,
                    retryCount: 1),
                failClosedOnApplyFailure: true));

        messageBroker.Subscribe<HideoutCampaignConsequenceRequested>(Handle_HideoutCampaignConsequenceRequested);
        messageBroker.Subscribe<NetworkHideoutAssaultSessionState>(Handle_NetworkHideoutAssaultSessionState);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<HideoutCampaignConsequenceRequested>(Handle_HideoutCampaignConsequenceRequested);
        messageBroker.Unsubscribe<NetworkHideoutAssaultSessionState>(Handle_NetworkHideoutAssaultSessionState);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        consequenceRoute.Dispose();
        assaultSessions.Clear();
        replicaSession = null;
    }

    /// <summary>
    /// [Client] Waits until the server has prepared the hideout and the resulting defender roster deltas have
    /// reached this client. The native callback must not create its troop supplier from the pre-preparation state.
    /// </summary>
    internal bool RequestMissionPreparationBlocking(Settlement settlement, bool isDirectAssault)
    {
        if (ModInformation.IsServer || settlement?.IsHideout != true ||
            !objectManager.TryGetIdWithLogging(settlement, out var settlementId))
            return false;

        if (!SubmitBlocking(new HideoutConsequenceIntent(
                settlementId,
                isDirectAssault ? HideoutCampaignConsequence.PrepareDirectAssaultMission : HideoutCampaignConsequence.PrepareMission,
                null,
                0), out var prepare))
            return false;

        // The mission may start only after the canonical defender roster has reached this client. The
        // shared route's commit probe enforces this, rather than treating delivery of the reply as success.
        return SubmitBlocking(new HideoutConsequenceIntent(
            settlementId,
            HideoutCampaignConsequence.SetAttackCooldown,
            prepare.AssaultSessionId,
            prepare.HideoutRevision), out _);
    }

    internal bool RequestConsequenceBlocking(Settlement settlement, HideoutCampaignConsequence consequence)
    {
        if (ModInformation.IsServer || settlement?.IsHideout != true ||
            !objectManager.TryGetIdWithLogging(settlement, out var settlementId))
            return false;

        if (consequence == HideoutCampaignConsequence.GrantClearRewards)
            return false; // Disabled until an authoritative mission/map-event clear receipt exists.

        string assaultSessionId = consequence == HideoutCampaignConsequence.SetAttackCooldown &&
                                  replicaSession?.SettlementId == settlementId
            ? replicaSession.Id
            : null;
        long revision = assaultSessionId == null ? 0 : replicaSession.Revision;
        return SubmitBlocking(new HideoutConsequenceIntent(settlementId, consequence, assaultSessionId, revision), out _);
    }

    private void Handle_HideoutCampaignConsequenceRequested(
        MessagePayload<HideoutCampaignConsequenceRequested> payload)
    {
        if (!ModInformation.IsClient ||
            !objectManager.TryGetIdWithLogging(payload.What.Settlement, out var settlementId))
            return;

        if (payload.What.Consequence == HideoutCampaignConsequence.GrantClearRewards)
        {
            Logger.Warning("Ignored hideout clear-reward request because no server mission-clear receipt is wired");
            return;
        }

        string assaultSessionId = payload.What.Consequence == HideoutCampaignConsequence.SetAttackCooldown &&
                                  replicaSession?.SettlementId == settlementId
            ? replicaSession.Id
            : null;
        long revision = assaultSessionId == null ? 0 : replicaSession.Revision;
        consequenceRoute.Submit(new HideoutConsequenceIntent(
            settlementId, payload.What.Consequence, assaultSessionId, revision));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "hideout-config-unavailable");
        if (header.ProtocolVersion != snapshot.ProtocolVersion ||
            !string.Equals(header.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == snapshot.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private static string ValidateWireShape(NetworkHideoutCampaignConsequenceRequested request)
    {
        if (string.IsNullOrWhiteSpace(request.SettlementId) || request.SettlementId.Length > 256)
            return "hideout-settlement-invalid";
        if (!Enum.IsDefined(typeof(HideoutCampaignConsequence), request.Consequence))
            return "hideout-consequence-invalid";
        if (request.Consequence is HideoutCampaignConsequence.PrepareMission or
            HideoutCampaignConsequence.PrepareDirectAssaultMission)
            return string.IsNullOrEmpty(request.AssaultSessionId) && request.ExpectedHideoutRevision == 0
                ? null
                : "hideout-prepare-session-invalid";
        if (request.Consequence == HideoutCampaignConsequence.SetAttackCooldown)
        {
            // A cooldown from a MISSION assault is bound to that assault's session. A cooldown from a
            // FAILED SEND-TROOPS raid has no session by construction: send-troops never prepares a
            // mission, so no session is ever opened for it. Requiring one there rejected the request
            // forever, the raid never completed, and the hideout menu could not be left.
            bool sessionBound = !string.IsNullOrWhiteSpace(request.AssaultSessionId) &&
                                request.AssaultSessionId.Length <= 96 &&
                                request.ExpectedHideoutRevision > 0;
            bool sessionless = string.IsNullOrEmpty(request.AssaultSessionId) &&
                               request.ExpectedHideoutRevision == 0;
            return sessionBound || sessionless ? null : "hideout-cooldown-session-invalid";
        }
        return "hideout-clear-receipt-unavailable";
    }

    private AuthorityServerReply<NetworkHideoutCampaignConsequenceResult> ExecuteConsequence(
        AuthorityServerContext context,
        NetworkHideoutCampaignConsequenceRequested request)
    {
        if (!objectManager.TryGetObject<Hero>(context.Player.HeroId, out var playerHero) ||
            !objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var playerParty) ||
            !objectManager.TryGetObject<Settlement>(request.SettlementId, out var settlement) ||
            settlement?.IsHideout != true ||
            playerParty.IsActive != true ||
            playerParty.CurrentSettlement != settlement)
        {
            Logger.Warning("Rejected invalid hideout consequence request for {SettlementId}", request.SettlementId);
            return Reply(context.Header, request, AuthorityResultStatus.Rejected, "hideout-context-invalid", default);
        }

        var behavior = Campaign.Current?.GetCampaignBehavior<HideoutCampaignBehavior>();
        if (behavior == null)
        {
            Logger.Warning("Cannot apply hideout consequence because HideoutCampaignBehavior is unavailable");
            return Reply(context.Header, request, AuthorityResultStatus.Unavailable, "hideout-behavior-unavailable", default);
        }

        using var playerContext = new BarterPlayerContext(playerHero, playerParty);
        switch (request.Consequence)
        {
            case HideoutCampaignConsequence.PrepareMission:
            case HideoutCampaignConsequence.PrepareDirectAssaultMission:
                if (!settlement.Hideout.IsInfested || !settlement.Hideout.NextPossibleAttackTime.IsPast)
                    return Reply(context.Header, request, AuthorityResultStatus.Rejected, "hideout-attack-unavailable", default);

                if (assaultSessions.ContainsKey(context.Peer))
                    return Reply(context.Header, request, AuthorityResultStatus.Rejected, "hideout-assault-active", default);
                if (sendCoalescer == null)
                    return Reply(context.Header, request, AuthorityResultStatus.Unavailable, "hideout-replication-unavailable", default);

                bool preparationMutated = false;
                try
                {
                    behavior.ArrangeHideoutTroopCountsForMission();
                    preparationMutated = true;

                    if (request.Consequence == HideoutCampaignConsequence.PrepareDirectAssaultMission &&
                        !EnsureDirectAssaultMinimum(settlement))
                    {
                        Logger.Warning(
                            "Cannot prepare direct hideout assault because no defender can receive the minimum troop adjustment. SettlementId={SettlementId}",
                            request.SettlementId);
                        context.Peer.Disconnect();
                        return Reply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                            "hideout-preparation-isolated", default, suppressReply: true);
                    }

                    var session = AssaultSession.Create(context.Peer, request.SettlementId,
                        GetHealthyDefenderCount(settlement), settlement.Hideout.IsInfested);
                    if (!assaultSessions.TryAdd(context.Peer, session))
                        throw new InvalidOperationException("Hideout assault session was concurrently created");

                    FlushDefenderRosters(settlement);
                    PublishSession(context.Peer, context.Header.SessionId, session);
                    return Reply(context.Header, request, AuthorityResultStatus.Accepted, null,
                        new ConsequenceResult(session.ExpectedHealthyDefenderCount, expectedCooldownActive: false), session);
                }
                catch
                {
                    assaultSessions.TryRemove(context.Peer, out _);
                    if (preparationMutated) context.Peer.Disconnect();
                    throw;
                }

            case HideoutCampaignConsequence.SetAttackCooldown:
                if (!settlement.Hideout.IsInfested || !settlement.Hideout.NextPossibleAttackTime.IsPast)
                    return Reply(context.Header, request, AuthorityResultStatus.Rejected, "hideout-cooldown-unavailable", default);

                // Sessionless: a failed send-troops raid. There is no assault session to bind to, so this
                // is authorised by live state alone - which the context check above has already made:
                // this player's active party is inside this hideout settlement. It is refused while the
                // peer holds a real session, so a mission assault can never take this path to skip its
                // own stage transitions.
                if (string.IsNullOrEmpty(request.AssaultSessionId))
                {
                    if (assaultSessions.ContainsKey(context.Peer))
                        return Reply(context.Header, request, AuthorityResultStatus.Rejected,
                            "hideout-cooldown-stage-invalid", default);

                    try
                    {
                        settlement.Hideout.SetNextPossibleAttackTime(
                            Campaign.Current.Models.HideoutModel.HideoutHiddenDuration);
                        return Reply(context.Header, request, AuthorityResultStatus.Accepted, null,
                            new ConsequenceResult(GetHealthyDefenderCount(settlement), expectedCooldownActive: true));
                    }
                    catch
                    {
                        context.Peer.Disconnect();
                        throw;
                    }
                }

                if (!TryGetOwnedSession(context.Peer, request, HideoutAssaultStage.Prepared, out var prepared))
                    return Reply(context.Header, request, AuthorityResultStatus.Rejected, "hideout-cooldown-stage-invalid", default);

                try
                {
                    settlement.Hideout.SetNextPossibleAttackTime(
                        Campaign.Current.Models.HideoutModel.HideoutHiddenDuration);
                    prepared.CommitCooldown(settlement.Hideout.IsInfested);
                    PublishSession(context.Peer, context.Header.SessionId, prepared);
                    var reply = Reply(context.Header, request, AuthorityResultStatus.Accepted, null,
                        new ConsequenceResult(prepared.ExpectedHealthyDefenderCount, expectedCooldownActive: true), prepared);
                    // Completion is a one-shot transition. The router's replay ledger retains this exact reply;
                    // releasing the live record prevents a completed assault from blocking a later assault.
                    assaultSessions.TryRemove(context.Peer, out _);
                    return reply;
                }
                catch
                {
                    context.Peer.Disconnect();
                    throw;
                }

            case HideoutCampaignConsequence.GrantClearRewards:
                return Reply(context.Header, request, AuthorityResultStatus.Unavailable,
                    "hideout-clear-receipt-unavailable", default);

            default:
                Logger.Warning("Rejected unknown hideout consequence {Consequence}", request.Consequence);
                return Reply(context.Header, request, AuthorityResultStatus.InvalidRequest, "hideout-consequence-invalid", default);
        }
    }

    private AuthorityServerReply<NetworkHideoutCampaignConsequenceResult> Reply(
        AuthorityRequestHeader header,
        NetworkHideoutCampaignConsequenceRequested request,
        AuthorityResultStatus status,
        string reasonCode,
        ConsequenceResult expected,
        AssaultSession assaultSession = null,
        bool suppressReply = false)
    {
        return new AuthorityServerReply<NetworkHideoutCampaignConsequenceResult>(
            new NetworkHideoutCampaignConsequenceResult(
                new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
                request.SettlementId,
                request.Consequence,
                expected.ExpectedHealthyDefenderCount,
                expected.ExpectedCooldownActive,
                assaultSession?.Id,
                assaultSession?.Stage ?? HideoutAssaultStage.None,
                assaultSession?.Revision ?? 0,
                assaultSession?.Infested ?? false),
            statePublished: status == AuthorityResultStatus.Accepted,
            suppressReply: suppressReply);
    }

    private static NetworkHideoutCampaignConsequenceResult CreateTerminalResult(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reasonCode)
    {
        return new NetworkHideoutCampaignConsequenceResult(
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
            null,
            default,
            0,
            false,
            null,
            HideoutAssaultStage.None,
            0,
            false);
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkHideoutCampaignConsequenceResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted ||
            !objectManager.TryGetObject<Settlement>(result.SettlementId, out var settlement) ||
            settlement?.IsHideout != true)
            return AuthorityCommitProbeResult.Pending;

        // A sessionless cooldown (failed send-troops) has no session to compare against, so it is
        // confirmed by the state it was meant to produce rather than by a stage transition.
        if (result.Consequence == HideoutCampaignConsequence.SetAttackCooldown &&
            string.IsNullOrEmpty(result.AssaultSessionId))
        {
            return settlement.Hideout != null && !settlement.Hideout.NextPossibleAttackTime.IsPast
                ? AuthorityCommitProbeResult.Applied
                : AuthorityCommitProbeResult.Pending;
        }

        var session = replicaSession;
        if (session == null || session.Id != result.AssaultSessionId ||
            session.SettlementId != result.SettlementId || session.Stage != result.Stage ||
            session.Revision != result.HideoutRevision || session.Infested != result.ExpectedInfested ||
            session.ExpectedHealthyDefenderCount != result.ExpectedHealthyDefenderCount ||
            session.CooldownActive != result.ExpectedCooldownActive)
            return AuthorityCommitProbeResult.Pending;

        return result.Consequence switch
        {
            HideoutCampaignConsequence.PrepareMission or HideoutCampaignConsequence.PrepareDirectAssaultMission =>
                GetHealthyDefenderCount(settlement) == result.ExpectedHealthyDefenderCount
                    ? AuthorityCommitProbeResult.Applied
                    : AuthorityCommitProbeResult.Pending,
            HideoutCampaignConsequence.SetAttackCooldown =>
                session.Stage == HideoutAssaultStage.CooldownCommitted
                    ? AuthorityCommitProbeResult.Applied
                    : AuthorityCommitProbeResult.Pending,
            HideoutCampaignConsequence.GrantClearRewards => AuthorityCommitProbeResult.Invalid,
            _ => AuthorityCommitProbeResult.Invalid
        };
    }

    private bool SubmitBlocking(HideoutConsequenceIntent intent, out NetworkHideoutCampaignConsequenceResult result)
    {
        var outcome = consequenceRoute.SubmitBlocking(intent);
        result = outcome.Result;
        if (outcome.Applied) return true;

        Logger.Warning(
            "Authoritative hideout consequence did not commit locally. SettlementId={SettlementId}, Consequence={Consequence}, Completion={Completion}, Reason={Reason}",
            intent.SettlementId, intent.Consequence, outcome.Completion, outcome.ReasonCode);
        GameThread.RunSafe(
            () => InformationManager.DisplayMessage(new InformationMessage(
                "The hideout could not be prepared by the server. Please try again.")),
            context: nameof(SubmitBlocking));
        return false;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkHideoutCampaignConsequenceResult> outcome)
    {
        if (outcome.Applied) return;
        Logger.Warning("Hideout authority request ended without a usable result. Completion={Completion}, Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private bool TryGetOwnedSession(
        NetPeer peer,
        NetworkHideoutCampaignConsequenceRequested request,
        HideoutAssaultStage expectedStage,
        out AssaultSession session)
    {
        return assaultSessions.TryGetValue(peer, out session) &&
               session.Id == request.AssaultSessionId &&
               session.SettlementId == request.SettlementId &&
               session.Stage == expectedStage &&
               session.Revision == request.ExpectedHideoutRevision;
    }

    private void PublishSession(NetPeer peer, string configSessionId, AssaultSession session)
    {
        network.Send(peer, new NetworkHideoutAssaultSessionState(
            configSessionId,
            session.Id,
            session.SettlementId,
            session.Stage,
            session.Revision,
            session.ExpectedHealthyDefenderCount,
            session.CooldownActive,
            session.Infested));
    }

    private void Handle_NetworkHideoutAssaultSessionState(MessagePayload<NetworkHideoutAssaultSessionState> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot) ||
            !string.Equals(snapshot.SessionId, payload.What.ConfigSessionId, StringComparison.Ordinal))
            return;

        replicaSession = new AssaultSession(
            owner: null,
            id: payload.What.AssaultSessionId,
            settlementId: payload.What.SettlementId,
            stage: payload.What.Stage,
            revision: payload.What.HideoutRevision,
            expectedHealthyDefenderCount: payload.What.ExpectedHealthyDefenderCount,
            cooldownActive: payload.What.CooldownActive,
            infested: payload.What.Infested);
    }

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (!ModInformation.IsServer || payload.What?.PlayerId == null) return;
        assaultSessions.TryRemove(payload.What.PlayerId, out _);
    }

    private static bool EnsureDirectAssaultMinimum(Settlement settlement)
    {
        const int directAssaultMinimum = 25;
        var defenders = GetDefenderParties(settlement).ToList();
        var healthyCount = defenders.Sum(party => party.MemberRoster.TotalHealthyCount);
        if (healthyCount >= directAssaultMinimum)
            return true;

        var receivingParty = defenders.FirstOrDefault(
            party => party.Party?.Culture?.BanditBandit != null);
        if (receivingParty == null)
            return false;

        // Native performs the same adjustment immediately after creating the MapEvent. Its defender side is
        // populated from these hideout parties, so doing it before creation lets the authoritative roster delta
        // reach the client before native constructs the mission troop supplier.
        receivingParty.MemberRoster.AddToCounts(
            receivingParty.Party.Culture.BanditBandit,
            directAssaultMinimum - healthyCount);
        return true;
    }

    private void FlushDefenderRosters(Settlement settlement)
    {
        if (sendCoalescer == null)
            return;

        foreach (var party in GetDefenderParties(settlement))
        {
            if (!objectManager.TryGetId(party.MemberRoster, out var rosterId))
                continue;

            sendCoalescer.FlushInstance(Compact(rosterId, typeof(TroopRoster)), network);
        }
    }

    private static int GetHealthyDefenderCount(Settlement settlement) =>
        GetDefenderParties(settlement).Sum(party => party.MemberRoster.TotalHealthyCount);

    private static IEnumerable<MobileParty> GetDefenderParties(Settlement settlement) =>
        settlement.Parties.Where(party => party.IsBandit || party.IsBanditBossParty);

    private readonly struct HideoutConsequenceIntent
    {
        public HideoutConsequenceIntent(
            string settlementId,
            HideoutCampaignConsequence consequence,
            string assaultSessionId,
            long expectedHideoutRevision)
        {
            SettlementId = settlementId;
            Consequence = consequence;
            AssaultSessionId = assaultSessionId;
            ExpectedHideoutRevision = expectedHideoutRevision;
        }

        public string SettlementId { get; }
        public HideoutCampaignConsequence Consequence { get; }
        public string AssaultSessionId { get; }
        public long ExpectedHideoutRevision { get; }
    }

    private sealed class AssaultSession
    {
        public AssaultSession(
            NetPeer owner, string id, string settlementId, HideoutAssaultStage stage, long revision,
            int expectedHealthyDefenderCount, bool cooldownActive, bool infested)
        {
            Owner = owner;
            Id = id;
            SettlementId = settlementId;
            Stage = stage;
            Revision = revision;
            ExpectedHealthyDefenderCount = expectedHealthyDefenderCount;
            CooldownActive = cooldownActive;
            Infested = infested;
        }

        public NetPeer Owner { get; }
        public string Id { get; }
        public string SettlementId { get; }
        public HideoutAssaultStage Stage { get; private set; }
        public long Revision { get; private set; }
        public int ExpectedHealthyDefenderCount { get; }
        public bool CooldownActive { get; private set; }
        public bool Infested { get; private set; }

        public static AssaultSession Create(NetPeer owner, string settlementId, int defenderCount, bool infested) =>
            new(owner, Guid.NewGuid().ToString("N"), settlementId, HideoutAssaultStage.Prepared, 1,
                defenderCount, cooldownActive: false, infested);

        public void CommitCooldown(bool infested)
        {
            Stage = HideoutAssaultStage.CooldownCommitted;
            Revision++;
            CooldownActive = true;
            Infested = infested;
        }
    }

    private readonly struct ConsequenceResult
    {
        public ConsequenceResult(int expectedHealthyDefenderCount, bool expectedCooldownActive)
        {
            ExpectedHealthyDefenderCount = expectedHealthyDefenderCount;
            ExpectedCooldownActive = expectedCooldownActive;
        }

        public int ExpectedHealthyDefenderCount { get; }
        public bool ExpectedCooldownActive { get; }
    }
}
