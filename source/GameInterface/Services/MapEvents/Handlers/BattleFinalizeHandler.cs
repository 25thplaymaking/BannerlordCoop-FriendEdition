using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Services.SiegeEvents.Patches;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Handlers;

internal readonly struct MapEventFinalizeIntent
{
    public MapEventFinalizeIntent(string mapEventId, int hostEpoch)
    {
        MapEventId = mapEventId;
        HostEpoch = hostEpoch;
    }

    public string MapEventId { get; }
    public int HostEpoch { get; }
}

/// <summary>
/// Owns finalizing a map event and tearing its encounter down (split out of <see cref="BattleHandler"/>). The
/// server finalizes an authenticated host request (<see cref="NetworkMapEventFinalizeAttempted"/>) and automatically on a
/// concluded victory (<see cref="MapEventConcluded"/>), deduping so <c>FinalizeEventAux</c> never runs twice, and
/// tells every involved player to close its encounter (<see cref="NetworkClosePvpEncounter"/>). The correlated
/// <see cref="NetworkMapEventFinalized"/> certificate completes only after the canonical tombstone applies.
/// </summary>
internal class BattleFinalizeHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleFinalizeHandler>();

    // Server-side: map events whose finalize has already run, so a duplicate finalize is ignored. A battle can
    // be finalized twice: the host leaves (finalize #1), host migration promotes another player, and that new
    // host's own "done" sends finalize #2. Re-running MapEvent.FinalizeEventAux re-forfeits the rosters (the same
    // troop removed twice -> the client roster goes negative). Keyed by the event INSTANCE via a weak table, so
    // it self-evicts when the event is GC'd (no growth, no eviction race vs. a duplicate that arrives a second
    // later) and never conflates two distinct events that happen to share an object id.
    private static readonly object FinalizedMarker = new object();
    private readonly ConditionalWeakTable<MapEvent, object> finalizedMapEvents = new ConditionalWeakTable<MapEvent, object>();
    private readonly object finalizedMapEventsLock = new object();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;
    private readonly INetwork network;
    private readonly IMapEventLogger mapEventLogger;
    private readonly IBattleTroopReserveBuilder reserveBuilder;
    private readonly ISettlementInterface settlementInterface;
    private readonly IBattleHostRegistry hostRegistry;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<MapEventFinalizeIntent, NetworkMapEventFinalized> finalizeRoute;

    public BattleFinalizeHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot,
        INetwork network,
        IMapEventLogger mapEventLogger,
        IBattleTroopReserveBuilder reserveBuilder,
        ISettlementInterface settlementInterface,
        IBattleHostRegistry hostRegistry,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;
        this.network = network;
        this.mapEventLogger = mapEventLogger;
        this.reserveBuilder = reserveBuilder;
        this.settlementInterface = settlementInterface;
        this.hostRegistry = hostRegistry;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;

        finalizeRoute = authorityRequestRouter.Register(
            AuthorityRoute<MapEventFinalizeIntent, NetworkMapEventFinalizeAttempted,
                NetworkMapEventFinalized>.Define(
                routeId: "map-event.finalize",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkMapEventFinalizeAttempted(
                    header, intent.MapEventId, intent.HostEpoch),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateFinalizeWireShape,
                buildCommandKey: request => string.Concat(
                    request.MapEventId.Length, ":", request.MapEventId, ":", request.HostEpoch),
                validateHeader: ValidateHeader,
                execute: ExecuteFinalize,
                createTerminalResult: (header, status, reason) => new NetworkMapEventFinalized(
                    header, status, null, 0, false, reason),
                probeClientCommit: ProbeFinalizeCommit,
                requestResync: _ => { },
                presentTerminalOutcome: PresentFinalizeOutcome,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) =>
                    string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal) &&
                    request.HostEpoch == result.HostEpoch));

        messageBroker.Subscribe<MapEventFinalizeAttempted>(Handle_MapEventFinalizeAttempted);
        messageBroker.Subscribe<NetworkRaidBattleTransition>(Handle_NetworkRaidBattleTransition);
        messageBroker.Subscribe<MapEventConcluded>(Handle_MapEventConcluded);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MapEventFinalizeAttempted>(Handle_MapEventFinalizeAttempted);
        messageBroker.Unsubscribe<NetworkRaidBattleTransition>(Handle_NetworkRaidBattleTransition);
        messageBroker.Unsubscribe<MapEventConcluded>(Handle_MapEventConcluded);
        finalizeRoute.Dispose();
    }

    private void Handle_MapEventFinalizeAttempted(MessagePayload<MapEventFinalizeAttempted> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.MapEvent, out string mapEventId))
            return;

        if (MapEventConfig.Debug)
            mapEventLogger.DebugMapEvent(payload.What.MapEvent, "Map event finalize attempted through the authority owner.");

        if (ModInformation.IsServer)
        {
            FinalizeInternal(mapEventId, payload.What.MapEvent);
            return;
        }

        if (!hostRegistry.TryGet(mapEventId, out var assignment) || assignment.Epoch <= 0)
        {
            Logger.Warning("Map-event finalize was not submitted because its host generation is unavailable. MapEvent={MapEventId}",
                mapEventId);
            RecoverRejectedFinalize();
            return;
        }

        finalizeRoute.Submit(new MapEventFinalizeIntent(mapEventId, assignment.Epoch));
    }

    private AuthorityServerReply<NetworkMapEventFinalized> ExecuteFinalize(
        AuthorityServerContext context, NetworkMapEventFinalizeAttempted request)
    {
        if (!hostRegistry.TryGet(request.MapEventId, out var assignment) || assignment.Epoch <= 0)
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Unavailable,
                "battle-host-not-ready");
        if (assignment.Epoch != request.HostEpoch)
            return FinalizeReply(context.Header, request, AuthorityResultStatus.StaleState,
                "stale-host-epoch");
        if (!string.Equals(context.Player.ControllerId, assignment.HostControllerId, StringComparison.Ordinal))
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Unauthorized,
                "invalid-battle-host");
        if (!objectManager.TryGetObject<MapEvent>(request.MapEventId, out var mapEvent) || mapEvent == null)
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Unavailable,
                "map-event-not-found");
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var playerParty) ||
            playerParty?.Party == null || !ReferenceEquals(playerParty.Party.MapEvent, mapEvent) ||
            mapEvent.FindMapEventParty(playerParty.Party) == null)
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Unauthorized,
                "invalid-battle-host");
        if (mapEvent.IsFinalized)
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Rejected,
                "map-event-finalized");
        if (ShouldContinueRaidAfterResistanceBattle(mapEvent))
            return FinalizeReply(context.Header, request, AuthorityResultStatus.Unavailable,
                "raid-transition-server-owned");

        if (MapEventConfig.Debug)
            mapEventLogger.DebugMapEvent(mapEvent, "Handling authoritative map-event finalize request.");

        var mutationBoundaryCrossed = false;
        try
        {
            var playerPartyIds = FinalizeAndCollectPlayers(mapEvent, markMutationBoundary: () =>
                mutationBoundaryCrossed = true);
            if (playerPartyIds.Length > 0)
                PvpEncounterCloseSender.Send(network, playerPartyIds, mapEventId: request.MapEventId);

            if (objectManager.TryGetObject<MapEvent>(request.MapEventId, out _))
                return IsolateFinalize(context, request, "finalize-tombstone-missing");

            return FinalizeReply(context.Header, request, AuthorityResultStatus.Accepted,
                null, finalized: true, statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Map-event finalize failed. RequestId={RequestId} MapEvent={MapEventId} BoundaryCrossed={BoundaryCrossed}",
                context.Header.RequestId, request.MapEventId, mutationBoundaryCrossed);
            return mutationBoundaryCrossed
                ? IsolateFinalize(context, request, "ambiguous-finalize-mutation")
                : FinalizeReply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                    "finalize-execution-failed");
        }
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var current)) return default;
        return new AuthorityRequestHeader(current.ProtocolVersion, current.SessionId, requestId, current.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion ||
            !string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private static string ValidateFinalizeWireShape(NetworkMapEventFinalizeAttempted request) =>
        string.IsNullOrWhiteSpace(request.MapEventId) || request.MapEventId.Length > 256 || request.HostEpoch <= 0
            ? "invalid-map-event-finalize"
            : null;

    private static AuthorityServerReply<NetworkMapEventFinalized> FinalizeReply(
        AuthorityRequestHeader header, NetworkMapEventFinalizeAttempted request,
        AuthorityResultStatus status, string reasonCode, bool finalized = false, bool statePublished = false,
        bool suppressReply = false) =>
        new(new NetworkMapEventFinalized(header, status, request.MapEventId,
            request.HostEpoch, finalized, reasonCode), statePublished, suppressReply);

    private AuthorityServerReply<NetworkMapEventFinalized> IsolateFinalize(
        AuthorityServerContext context, NetworkMapEventFinalizeAttempted request, string reasonCode)
    {
        Logger.Fatal(
            "Isolating campaign peers after ambiguous map-event finalize. RequestId={RequestId} MapEvent={MapEventId} Reason={Reason}",
            context.Header.RequestId, request.MapEventId, reasonCode);
        DisconnectAllCampaignPeers(context.Peer);
        return FinalizeReply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
            reasonCode, suppressReply: true);
    }

    private AuthorityCommitProbeResult ProbeFinalizeCommit(NetworkMapEventFinalized result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || !result.Finalized ||
            string.IsNullOrEmpty(result.MapEventId) || result.HostEpoch <= 0)
            return AuthorityCommitProbeResult.Invalid;
        if (objectManager.TryGetObject<MapEvent>(result.MapEventId, out _))
            return AuthorityCommitProbeResult.Pending;

        if (PlayerEncounter.Current?._mapEvent != null)
            return AuthorityCommitProbeResult.Pending;
        return AuthorityCommitProbeResult.Applied;
    }

    private void PresentFinalizeOutcome(AuthorityClientOutcome<NetworkMapEventFinalized> outcome)
    {
        if (outcome.Applied)
        {
            RecoverFinalizedMapEvent(outcome.Result.MapEventId);
            return;
        }

        Logger.Warning("Map-event finalize did not apply. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
        RecoverRejectedFinalize();
    }

    private void FinalizeInternal(string mapEventId, MapEvent mapEvent)
    {
        var mutationBoundaryCrossed = false;
        try
        {
            if (TryContinueRaidAfterResistanceBattle(mapEvent, () => mutationBoundaryCrossed = true))
                return;

            var playerPartyIds = FinalizeAndCollectPlayers(mapEvent, markMutationBoundary: () =>
                mutationBoundaryCrossed = true);
            if (playerPartyIds.Length > 0)
                PvpEncounterCloseSender.Send(network, playerPartyIds, mapEventId: mapEventId);

            if (objectManager.TryGetObject<MapEvent>(mapEventId, out _))
                throw new InvalidOperationException("The finalized map event remained registered.");
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Internal map-event finalize failed. MapEvent={MapEventId} BoundaryCrossed={BoundaryCrossed}",
                mapEventId, mutationBoundaryCrossed);
            if (mutationBoundaryCrossed)
                DisconnectAllCampaignPeers(null);
        }
    }

    private void DisconnectAllCampaignPeers(NetPeer requestingPeer)
    {
        try { requestingPeer?.Disconnect(); } catch { }
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.TryGetPeer(player.ControllerId, out var peer) || ReferenceEquals(peer, requestingPeer))
                continue;
            try { peer.Disconnect(); } catch { }
        }
    }

    private static void RecoverRejectedFinalize()
    {
        if (Campaign.Current == null || PlayerEncounter.Current == null) return;
        try { GameMenu.SwitchToMenu("encounter"); }
        catch (Exception exception) { Logger.Warning(exception, "Failed to restore encounter menu after finalize rejection"); }
    }

    /// <summary>
    /// [Server] A battle reached a victory state — finalize it and close EVERY involved player's encounter, so a
    /// concluded coop battle tears down without the player leaving the post-battle menu (the auto-finalize on
    /// conclusion). There is no single leaver here, so the close instruction covers all involved players directly.
    /// </summary>
    private void Handle_MapEventConcluded(MessagePayload<MapEventConcluded> payload)
    {
        if (ModInformation.IsClient) return;

        var knownPlayerPartyIds = MapEventPlayerPartyCollector.Combine(payload.What.PlayerPartyIds);
        var closeAlreadySent = !string.IsNullOrEmpty(payload.What.SurrenderedPartyId);
        if (!objectManager.TryGetObjectWithLogging(payload.What.MapEventId, out MapEvent mapEvent))
        {
            if (!closeAlreadySent && knownPlayerPartyIds.Length > 0)
                PvpEncounterCloseSender.Send(network, knownPlayerPartyIds, payload.What.SurrenderedPartyId, payload.What.MapEventId);

            return;
        }

        if (MapEventConfig.Debug)
            mapEventLogger.DebugMapEvent(mapEvent, "Battle concluded; auto-finalizing and closing every involved player's encounter.");

        // Fires automatically on every victory, so guard the game thread: a finalize edge case must not escape
        // and tear down the campaign tick.
        var mutationBoundaryCrossed = false;
        try
        {
            if (TryContinueRaidAfterResistanceBattle(mapEvent, () => mutationBoundaryCrossed = true))
                return;

            var playerPartyIds = FinalizeAndCollectPlayers(mapEvent, knownPlayerPartyIds,
                () => mutationBoundaryCrossed = true);

            if (!closeAlreadySent && playerPartyIds.Length > 0)
                PvpEncounterCloseSender.Send(network, playerPartyIds, payload.What.SurrenderedPartyId, payload.What.MapEventId);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to auto-finalize concluded map event");
            if (mutationBoundaryCrossed)
                DisconnectAllCampaignPeers(null);
        }
    }

    /// <summary>
    /// [Server] Finalize <paramref name="mapEvent"/> on the game thread, capturing the involved player party ids
    /// first (finalize clears them) so they get a reliable server-addressed encounter close instead of each
    /// racing its own local teardown. <see cref="GameThread.Run"/> runs inline when already on the game thread.
    /// </summary>
    private string[] FinalizeAndCollectPlayers(MapEvent mapEvent, string[] knownPlayerPartyIds = null,
        Action markMutationBoundary = null)
    {
        markMutationBoundary?.Invoke();
        if (!TryMarkFinalized(mapEvent))
            return MapEventPlayerPartyCollector.Combine(knownPlayerPartyIds);

        string[] playerPartyIds = null;
        GameThread.Run(() =>
        {
            playerPartyIds = MapEventPlayerPartyCollector.Combine(
                knownPlayerPartyIds,
                MapEventPlayerPartyCollector.CollectPartyIds(mapEvent, objectManager));

            var excludedIds = CollectExcludedPlayerPartyIds(mapEvent);
            if (excludedIds.Count > 0)
                playerPartyIds = playerPartyIds.Where(id => !excludedIds.Contains(id)).ToArray();
            var raidSettlement = GetRaidFinalizationSettlement(mapEvent);
            var raidAttackers = GetRaidAttackerPlayerParties(mapEvent);

            // A winning inside defender is kept off the close above, but nothing seats it on the siege-defeated
            // menu: the server tears the SiegeEvent/MapEvent down via replication, bypassing vanilla's local
            // siege-end routing, so the winner falls through to the settlement arrival menu. Capture its parties
            // + settlement now (finalize clears them) and prompt after finalize (below), behind the event destroy.
            string defenderVictorySettlementId = null;
            string[] defenderVictoryPartyIds = null;
            if (mapEvent.IsSiegeAssault && mapEvent.BattleState == BattleState.DefenderVictory)
            {
                defenderVictoryPartyIds = CollectWinningInsideDefenderPartyIds(mapEvent);
                if (defenderVictoryPartyIds.Length > 0)
                    objectManager.TryGetId(mapEvent.MapEventSettlement, out defenderVictorySettlementId);
            }

            // The battle is over — drop its server-side troop reserves (ledger entry + flatten cache) so they
            // don't leak per battle. Done before FinalizeEventAux clears the parties, so the flatten-cache
            // cleanup can still enumerate them. No-op on a client (its ledger is never populated).
            reserveBuilder.ForgetMapEvent(mapEvent);

            // A siege assault that ends without a victor (attackers retreated or abandoned the fight)
            // keeps the siege in vanilla; a bare finalize would lift it. Victories finalize normally:
            // attacker victory captures the settlement, defender victory breaks the siege.
            if (mapEvent.IsSiegeAssault
                && mapEvent.BattleState != BattleState.AttackerVictory
                && mapEvent.BattleState != BattleState.DefenderVictory)
            {
                mapEvent._keepSiegeEvent = true;
                mapEvent.AttackerSide?.LeaderParty?.MobileParty?.RecalculateShortTermBehavior();
            }

            // Vanilla silently re-crowns AttackerSide.LeaderParty to whichever party is first in the
            // list if the leader's party ever left and rejoined the event; capture and the aftermath
            // prompt key on it, so re-assert the besieger camp leader before finalizing.
            if (mapEvent.IsSiegeAssault && mapEvent.AttackerSide != null)
            {
                var campLeader = mapEvent.MapEventSettlement?.SiegeEvent?.BesiegerCamp?.LeaderParty?.Party;
                if (campLeader != null && mapEvent.AttackerSide.LeaderParty != campLeader)
                {
                    mapEvent.AttackerSide.LeaderParty = campLeader;
                }
            }

            mapEvent.FinalizeEventAux();
            MoveRaidAttackersToSettlementGate(raidAttackers, raidSettlement);

            // After the destroy (same game thread, so behind it on the reliable-ordered channel).
            if (!string.IsNullOrEmpty(defenderVictorySettlementId))
                network.SendAll(new NetworkPromptSiegeDefenderVictory(defenderVictorySettlementId, defenderVictoryPartyIds));
        }, blocking: true, label: nameof(FinalizeAndCollectPlayers));
        return playerPartyIds ?? Array.Empty<string>();
    }

    private bool TryMarkFinalized(MapEvent mapEvent)
    {
        lock (finalizedMapEventsLock)
        {
            if (finalizedMapEvents.TryGetValue(mapEvent, out _))
            {
                objectManager.TryGetId(mapEvent, out var duplicateId);
                Logger.Warning("Ignoring duplicate finalize for already-finalized map event {MapEventId} (likely a post-migration second leave); not re-running the capture/roster forfeit.", duplicateId);
                return false;
            }
            finalizedMapEvents.Add(mapEvent, FinalizedMarker);
        }

        // The battle is over - release the mode claim so a later, unrelated battle on this event starts unclaimed.
        if (objectManager.TryGetId(mapEvent, out var mapEventIdForRelease))
            ServerBattleModeArbiter.Release(mapEventIdForRelease);

        return true;
    }

    // [Server, game thread] Player parties that must keep their encounter through the finalize:
    // a capturing leader enters the settlement-taken flow, and a winning inside defender sits on
    // its vanilla victory menu — the close's Finish + ExitToLast would tear either down.
    private HashSet<string> CollectExcludedPlayerPartyIds(MapEvent mapEvent)
    {
        var excluded = new HashSet<string>();
        if (mapEvent?.AttackerSide == null || mapEvent.DefenderSide == null) return excluded;

        SiegeAftermathPatches.TryGetPlayerCaptureLeader(mapEvent, out var capturingLeader, out _);

        foreach (var party in mapEvent.InvolvedParties)
        {
            bool isCapturingLeader = capturingLeader != null && party?.MobileParty == capturingLeader;
            bool isWinningInsideDefender = mapEvent.IsSiegeAssault
                && mapEvent.BattleState == BattleState.DefenderVictory
                && party?.Side == BattleSideEnum.Defender
                && party.MobileParty?.CurrentSettlement == mapEvent.MapEventSettlement;
            if (!isCapturingLeader && !isWinningInsideDefender) continue;

            if (objectManager.TryGetId(party, out var id)) excluded.Add(id);
        }

        return excluded;
    }

    // [Server, game thread] The winning inside-defender player parties, for the siege-defeated-menu prompt.
    private string[] CollectWinningInsideDefenderPartyIds(MapEvent mapEvent)
    {
        List<string> ids = null;
        foreach (var party in mapEvent.InvolvedParties)
        {
            if (party?.Side != BattleSideEnum.Defender) continue;
            if (party.MobileParty?.CurrentSettlement != mapEvent.MapEventSettlement) continue;
            if (!objectManager.TryGetId(party, out var id)) continue;

            if (ids == null) ids = new List<string>();
            ids.Add(id);
        }

        return ids?.ToArray() ?? Array.Empty<string>();
    }
    private bool TryContinueRaidAfterResistanceBattle(MapEvent mapEvent, Action markMutationBoundary = null)
    {
        var handled = false;
        string[] playerPartyIds = null;
        string settlementId = null;
        string continuedMapEventId = null;

        GameThread.Run(
            () =>
            {
                if (!ShouldContinueRaidAfterResistanceBattle(mapEvent))
                    return;

                var settlement = mapEvent.MapEventSettlement;
                if (!objectManager.TryGetIdWithLogging(settlement, out settlementId))
                    return;

                markMutationBoundary?.Invoke();
                if (!TryMarkFinalized(mapEvent))
                    return;

                var involvedParties = CollectInvolvedParties(mapEvent);
                var attackerParties = mapEvent.AttackerSide.Parties
                    .Select(mapEventParty => mapEventParty.Party)
                    .Where(party => party != null)
                    .ToArray();
                var attackerLeader = mapEvent.AttackerSide.LeaderParty;
                playerPartyIds = CollectRaidAttackerPlayerPartyIds(mapEvent);

                reserveBuilder.ForgetMapEvent(mapEvent);
                mapEvent.FinalizeEventAux();
                ClearMapEventBackReferences(involvedParties);
                ResetRaidSettlementState(settlement);
                ReturnPlayerPartiesToSettlement(playerPartyIds, settlement);

                var continuedMapEvent = MapEventBattleFactory.CreateMapEvent(
                    attackerLeader,
                    settlement.Party,
                    RaidBattleCreationFlags());
                if (continuedMapEvent != null)
                {
                    RejoinRaidAttackers(continuedMapEvent, attackerParties, attackerLeader);
                    objectManager.TryGetIdWithLogging(continuedMapEvent, out continuedMapEventId);
                }

                handled = true;
            },
            blocking: true,
            label: nameof(TryContinueRaidAfterResistanceBattle));

        if (!handled)
            return false;

        network.SendAll(new NetworkRaidBattleTransition(playerPartyIds, settlementId, continuedMapEventId));
        return true;
    }

    private void Handle_NetworkRaidBattleTransition(MessagePayload<NetworkRaidBattleTransition> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who)) return;

        var message = payload.What;

        GameThread.RunSafe(
            () =>
            {
                if (Campaign.Current == null) return;
                if (!objectManager.TryGetId(MobileParty.MainParty?.Party, out var myPartyId)) return;
                if (message.PartyIds == null || message.PartyIds.Length == 0) return;
                if (Array.IndexOf(message.PartyIds, myPartyId) < 0) return;
                if (!objectManager.TryGetObjectWithLogging<Settlement>(message.SettlementId, out var settlement)) return;

                BattleModeRegistry.End();
                if (!string.IsNullOrEmpty(message.MapEventId) &&
                    objectManager.TryGetObjectWithLogging<MapEvent>(message.MapEventId, out var continuedMapEvent))
                {
                    ContinueLocalRaid(continuedMapEvent, settlement);
                }
                else
                {
                    ResetLocalRaidBattleToVillage(settlement);
                }
            },
            context: nameof(Handle_NetworkRaidBattleTransition));
    }

    private void ContinueLocalRaid(MapEvent mapEvent, Settlement settlement)
    {
        var mainParty = MobileParty.MainParty;
        if (mainParty == null)
            return;

        var encounter = PlayerEncounter.Current;
        if (encounter == null)
        {
            using (new AllowedThread())
            {
                if (mainParty.CurrentSettlement != settlement)
                    settlementInterface.PartyEnterSettlement(mainParty, settlement);
                settlementInterface.StartSettlementEncounter(mainParty, settlement);
            }
            encounter = PlayerEncounter.Current;
        }

        if (encounter == null)
        {
            ResetLocalRaidBattleToVillage(settlement);
            return;
        }

        encounter._mapEvent = mapEvent;
        encounter.ForceRaid = true;
        mainParty.SetMoveModeHold();
        GameMenu.SwitchToMenu(mapEvent.IsActiveSlowVillageRaid() ? "raiding_village" : "encounter");
    }

    private void ResetLocalRaidBattleToVillage(Settlement settlement)
    {
        var mainParty = MobileParty.MainParty;
        if (mainParty == null)
            return;

        using (new AllowedThread())
        {
            mainParty.Party._mapEventSide = null;

            if (PlayerEncounter.Current != null)
                PlayerEncounter.Finish(false);

            if (mainParty.CurrentSettlement != settlement)
                settlementInterface.PartyEnterSettlement(mainParty, settlement);

            ResetRaidSettlementState(settlement);
            settlementInterface.StartSettlementEncounter(mainParty, settlement);
        }

        mainParty.SetMoveModeHold();
        GameMenu.SwitchToMenu("village");
    }

    private void ReturnPlayerPartiesToSettlement(string[] playerPartyIds, Settlement settlement)
    {
        foreach (var playerPartyId in playerPartyIds ?? Array.Empty<string>())
        {
            if (!objectManager.TryGetObject<PartyBase>(playerPartyId, out var party))
                continue;

            var mobileParty = party.MobileParty;
            if (mobileParty == null || mobileParty.CurrentSettlement == settlement)
                continue;

            settlementInterface.PartyEnterSettlement(mobileParty, settlement);
        }
    }

    private static PartyBase[] CollectInvolvedParties(MapEvent mapEvent)
    {
        if (mapEvent?.AttackerSide == null || mapEvent.DefenderSide == null)
            return Array.Empty<PartyBase>();

        return mapEvent.InvolvedParties.Where(party => party != null).ToArray();
    }

    private static void ClearMapEventBackReferences(PartyBase[] parties)
    {
        foreach (var party in parties)
        {
            if (party?.MapEventSide != null)
                party._mapEventSide = null;
        }
    }

    private string[] CollectRaidAttackerPlayerPartyIds(MapEvent mapEvent)
    {
        var ids = new List<string>();
        foreach (var mapEventParty in mapEvent?.AttackerSide?.Parties ?? Enumerable.Empty<MapEventParty>())
        {
            if (mapEventParty?.Party?.MobileParty?.IsPlayerParty() != true)
                continue;

            if (objectManager.TryGetId(mapEventParty.Party, out var partyId))
                ids.Add(partyId);
        }

        return ids.ToArray();
    }

    private static void RejoinRaidAttackers(
        MapEvent continuedMapEvent,
        PartyBase[] attackerParties,
        PartyBase attackerLeader)
    {
        foreach (var attackerParty in attackerParties ?? Array.Empty<PartyBase>())
        {
            if (attackerParty == null || attackerParty == attackerLeader || !attackerParty.IsActive)
                continue;

            if (attackerParty.MapEventSide == null)
                attackerParty.MapEventSide = continuedMapEvent.AttackerSide;
        }
    }

    private static BattleCreationFlags RaidBattleCreationFlags() => new BattleCreationFlags(
        forceRaid: true,
        forceSallyOut: false,
        forceVolunteers: false,
        forceSupplies: false,
        isSallyOutAmbush: false,
        forceBlockadeAttack: false,
        forceBlockadeSallyOutAttack: false,
        forceHideoutSendTroops: false);

    private static bool ShouldContinueRaidAfterResistanceBattle(MapEvent mapEvent)
    {
        if (!mapEvent.IsRaidHostileAction())
            return false;

        if (!IsAttackerVictory(mapEvent))
            return false;

        var settlement = mapEvent.MapEventSettlement;
        if (settlement?.Village == null)
            return false;

        if (settlement.SettlementHitPoints <= 0f || settlement.Village.VillageState == Village.VillageStates.Looted)
            return false;

        return true;
    }

    private static void ResetRaidSettlementState(Settlement settlement)
    {
        if (settlement?.Village == null)
            return;

        settlement.Village.VillageState = Village.VillageStates.Normal;
    }

    private static bool IsAttackerVictory(MapEvent mapEvent)
    {
        return mapEvent.WinningSide == BattleSideEnum.Attacker ||
               mapEvent.BattleState == BattleState.AttackerVictory;
    }

    private void RecoverFinalizedMapEvent(string mapEventId)
    {
        GameThread.RunSafe(() =>
        {
            if (Campaign.Current == null) return;
            if (MissionState.Current != null || Mission.Current != null) return;
            if (PlayerEncounter.Current?.EncounterState == PlayerEncounterState.CaptureHeroes) return;

            var encounterMapEvent = PlayerEncounter.Current?._mapEvent;
            if (encounterMapEvent != null &&
                (!objectManager.TryGetId(encounterMapEvent, out var encounterMapEventId) ||
                 !string.Equals(encounterMapEventId, mapEventId, StringComparison.Ordinal)))
                return;

            // The local player's battle has ended — clear the recorded mode so the encounter-menu gate
            // (BattleModeEncounterOptionsPatch) no longer treats this event as claimed.
            BattleModeRegistry.End();

            // When this battle ended with the local player captured, the captivity flow owns the UI:
            // PlayerCaptivityClientHandler has switched to the prisoner menu and leaves the encounter
            // itself. Exiting menus here would close the capture screen.
            if (PlayerCaptivity.IsCaptive) return;

            var mainParty = MobileParty.MainParty;
            MoveLocalRaidPartyToSettlementGate(mainParty, GetLocalRaidFinalizationSettlement(mainParty));

            if (PlayerEncounter.Current != null)
            {
                // TODO determine force out of settlement
                PlayerEncounter.Finish(true);
            }

            GameMenu.ExitToLast();
        });
    }

    private void MoveRaidAttackersToSettlementGate(MobileParty[] raidAttackers, Settlement settlement)
    {
        if (settlement?.Village == null)
            return;

        foreach (var mobileParty in raidAttackers)
        {
            if (mobileParty == null)
                continue;

            mobileParty.Position = settlement.GatePosition;
            if (mobileParty.CurrentSettlement == settlement)
                settlementInterface.PartyLeaveSettlement(mobileParty);

            using (new AllowedThread())
            {
                mobileParty.SetMoveModeHold();
                mobileParty.ResetNavigationToHold();
            }
            PublishPartyBehaviorUpdate(mobileParty);
        }
    }

    private void MoveLocalRaidPartyToSettlementGate(MobileParty mainParty, Settlement settlement)
    {
        if (mainParty == null || settlement?.Village == null)
            return;

        using (new AllowedThread())
        {
            mainParty.Position = settlement.GatePosition;
            if (mainParty.CurrentSettlement == settlement)
                settlementInterface.PartyLeaveSettlement(mainParty);

            mainParty.ResetNavigationToHold();
        }
    }

    private void PublishPartyBehaviorUpdate(MobileParty mobileParty)
    {
        if (!mobilePartyBehaviorSnapshot.TryCreate(
                mobileParty,
                out PartyBehaviorUpdateData data))
            return;

        data.ForcePosition = true;
        data.ResetMovementToHold = true;

        // The gate reset must reach clients before the encounter close allows another map command.
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    private static MobileParty[] GetRaidAttackerPlayerParties(MapEvent mapEvent)
    {
        if (mapEvent?.IsRaidHostileAction() != true || mapEvent.AttackerSide == null)
            return Array.Empty<MobileParty>();

        return mapEvent.AttackerSide.Parties
            .Select(mapEventParty => mapEventParty.Party?.MobileParty)
            .Where(mobileParty => mobileParty?.IsPlayerParty() == true)
            .ToArray();
    }

    private static Settlement GetLocalRaidFinalizationSettlement(MobileParty mainParty)
    {
        var settlement = GetRaidFinalizationSettlement(PlayerEncounter.Battle ?? mainParty?.MapEvent);
        if (settlement != null)
            return settlement;

        if (PlayerEncounter.Current?.ForceRaid == true && mainParty?.CurrentSettlement?.Village != null)
            return mainParty.CurrentSettlement;

        return null;
    }

    private static Settlement GetRaidFinalizationSettlement(MapEvent mapEvent)
    {
        if (mapEvent?.IsRaidHostileAction() != true)
            return null;

        if (mapEvent.MapEventSettlement?.Village != null)
            return mapEvent.MapEventSettlement;

        if (mapEvent.DefenderSide?.LeaderParty?.Settlement?.Village != null)
            return mapEvent.DefenderSide.LeaderParty.Settlement;

        if (mapEvent.DefenderSide == null)
            return null;

        foreach (var mapEventParty in mapEvent.DefenderSide.Parties)
        {
            if (mapEventParty.Party?.Settlement?.Village != null)
                return mapEventParty.Party.Settlement;
        }

        return null;
    }
}
