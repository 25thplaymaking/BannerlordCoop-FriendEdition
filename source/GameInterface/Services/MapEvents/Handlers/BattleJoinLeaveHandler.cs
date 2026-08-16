using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MapEvents.Initialization;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MapEventSides.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.Villages.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

internal readonly struct BattleJoinIntent
{
    public BattleJoinIntent(string mapEventId, string partyId, BattleSideEnum side)
    {
        MapEventId = mapEventId;
        PartyId = partyId;
        Side = side;
    }

    public string MapEventId { get; }
    public string PartyId { get; }
    public BattleSideEnum Side { get; }
}

internal readonly struct BattleLeaveIntent
{
    public BattleLeaveIntent(string partyId, string mapEventId, bool finishLocalMenus)
    {
        PartyId = partyId;
        MapEventId = mapEventId;
        FinishLocalMenus = finishLocalMenus;
    }

    public string PartyId { get; }
    public string MapEventId { get; }
    public bool FinishLocalMenus { get; }
}

internal readonly struct BattleJoinProof
{
    public BattleJoinProof(string sessionId, long requestId, string mapEventId, string partyId, int side)
    {
        SessionId = sessionId;
        RequestId = requestId;
        MapEventId = mapEventId;
        PartyId = partyId;
        Side = side;
    }

    public string SessionId { get; }
    public long RequestId { get; }
    public string MapEventId { get; }
    public string PartyId { get; }
    public int Side { get; }
}

internal readonly struct BattleLeaveProof
{
    public BattleLeaveProof(string sessionId, long requestId, string mapEventId, string partyId, bool leaveSiege)
    {
        SessionId = sessionId;
        RequestId = requestId;
        MapEventId = mapEventId;
        PartyId = partyId;
        LeaveSiege = leaveSiege;
    }

    public string SessionId { get; }
    public long RequestId { get; }
    public string MapEventId { get; }
    public string PartyId { get; }
    public bool LeaveSiege { get; }
}

/// <summary>
/// Owns a party joining or leaving a battle without ending it (split out of <see cref="BattleHandler"/>). A client
/// bridges its join/leave to a server request; the server performs it authoritatively and, for a single-party
/// removal that does not auto-replicate, broadcasts it. Also applies the server's involved-party snapshot on the
/// client (troop-upgrade tracking + position snap). The server-side involved-parties broadcast and the player-count
/// fast-forward bookkeeping stay in <see cref="BattleHandler"/> because they drive time control.
/// </summary>
internal class BattleJoinLeaveHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleJoinLeaveHandler>();
    private const float PositionSyncDriftSlack = 0.5f;

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IMapEventLogger mapEventLogger;
    private readonly IMapEventInitializationBarrier initializationBarrier;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<BattleJoinIntent, NetworkJoinBattleReply> joinRoute;
    private readonly IAuthorityRouteHandle<BattleLeaveIntent, NetworkLeaveBattleResult> leaveRoute;
    private readonly ConcurrentDictionary<string, BattleJoinProof> joinProofs = new();
    private readonly ConcurrentDictionary<string, BattleLeaveProof> leaveProofs = new();

    public BattleJoinLeaveHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        IMapEventLogger mapEventLogger,
        IMapEventInitializationBarrier initializationBarrier,
        ISiegeEventInterface siegeEventInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.mapEventLogger = mapEventLogger;
        this.initializationBarrier = initializationBarrier;
        this.siegeEventInterface = siegeEventInterface;
        this.configAuthority = configAuthority;

        joinRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleJoinIntent, NetworkRequestJoinBattle, NetworkJoinBattleReply>.Define(
                routeId: "battle.join",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkRequestJoinBattle(header, intent.MapEventId,
                    intent.PartyId, intent.Side),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateJoinWireShape,
                buildCommandKey: request => BuildJoinCommandKey(request.MapEventId, request.PartyId, request.Side),
                validateHeader: ValidateHeader,
                execute: ExecuteJoin,
                createTerminalResult: (header, status, reason) => new NetworkJoinBattleReply(header, status,
                    null, null, -1, reason),
                probeClientCommit: ProbeJoinCommit,
                requestResync: _ => { },
                presentTerminalOutcome: _ => { },
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) =>
                    string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal) &&
                    string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
                    (int)request.Side == result.Side));

        leaveRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleLeaveIntent, NetworkRequestLeaveBattle, NetworkLeaveBattleResult>.Define(
                routeId: "battle.leave",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkRequestLeaveBattle(header, intent.PartyId,
                    intent.MapEventId, intent.FinishLocalMenus),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateLeaveWireShape,
                buildCommandKey: request => BuildLeaveCommandKey(request.PartyId, request.MapEventId,
                    request.FinishLocalMenus),
                validateHeader: ValidateHeader,
                execute: ExecuteLeave,
                createTerminalResult: (header, status, reason) => new NetworkLeaveBattleResult(header, status,
                    null, null, false, false, reason),
                probeClientCommit: ProbeLeaveCommit,
                requestResync: _ => { },
                presentTerminalOutcome: _ => { },
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) =>
                    string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
                    string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal) &&
                    request.FinishLocalMenus == result.FinishLocalMenus));

        messageBroker.Subscribe<NetworkAddInvolvedParties>(Handle_NetworkAddInvolvedParties);
        messageBroker.Subscribe<NetworkAddBattleParty>(Handle_NetworkAddBattlePartyProof);
        messageBroker.Subscribe<PlayerJoinBattleAttempted>(Handle_PlayerJoinBattleAttempted);
        messageBroker.Subscribe<PlayerLeaveBattleAttempted>(Handle_PlayerLeaveBattleAttempted);
        messageBroker.Subscribe<NetworkPartyLeftBattle>(Handle_NetworkPartyLeftBattle);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAddInvolvedParties>(Handle_NetworkAddInvolvedParties);
        messageBroker.Unsubscribe<NetworkAddBattleParty>(Handle_NetworkAddBattlePartyProof);
        messageBroker.Unsubscribe<PlayerJoinBattleAttempted>(Handle_PlayerJoinBattleAttempted);
        messageBroker.Unsubscribe<PlayerLeaveBattleAttempted>(Handle_PlayerLeaveBattleAttempted);
        messageBroker.Unsubscribe<NetworkPartyLeftBattle>(Handle_NetworkPartyLeftBattle);
        joinRoute.Dispose();
        leaveRoute.Dispose();
        joinProofs.Clear();
        leaveProofs.Clear();
    }

    private void Handle_NetworkAddInvolvedParties(MessagePayload<NetworkAddInvolvedParties> payload)
    {
        var message = payload.What;

        GameThread.RunSafe(() =>
        {
            try
            {
                // The campaign can tear down (exit to menu, disconnect, save load) between
                // enqueuing this and the main thread draining it; bail before touching
                // campaign state (the position snap below dereferences Campaign.Current).
                if (Campaign.Current == null)
                    return;

                if (!objectManager.TryGetObjectWithLogging<MapEvent>(message.MapEventId, out var mapEvent))
                    return;

                mapEventLogger.DebugMapEvent(mapEvent, "Handling network add involved parties. Party count: {MapEventPartyCount}", message.MapEventPartyIds.Length);

                var positions = message.Positions;

                var trackParties = !initializationBarrier.IsPending(mapEvent);
                using (new AllowedThread())
                {
                    for (int i = 0; i < message.MapEventPartyIds.Length; i++)
                    {
                        var mapEventPartyId = message.MapEventPartyIds[i];
                        if (!objectManager.TryGetObjectWithLogging<MapEventParty>(mapEventPartyId, out var mapEventParty))
                            continue;

                        if (trackParties)
                            mapEvent.TroopUpgradeTracker.AddParty(mapEventParty);
                        var mobileParty = mapEventParty.Party.MobileParty;
                        if (mobileParty != null && positions != null && i < positions.Length)
                            mobileParty.Position = positions[i];
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkAddInvolvedParties));
            }
        });
    }

    private void Handle_NetworkAddBattlePartyProof(MessagePayload<NetworkAddBattleParty> payload)
    {
        var message = payload.What;
        if (!configAuthority.IsTrustedServer(payload.Who) || message.AuthorityRequestId <= 0 ||
            string.IsNullOrEmpty(message.SessionId))
            return;

        joinProofs[ProofKey(message.SessionId, message.AuthorityRequestId)] =
            new BattleJoinProof(message.SessionId, message.AuthorityRequestId,
                message.MapEventId, message.PartyId, message.Side);
    }

    /// <summary>[Client] Bridge the local player's battle join to a server request.</summary>
    private void Handle_PlayerJoinBattleAttempted(MessagePayload<PlayerJoinBattleAttempted> payload)
    {
        if (ModInformation.IsServer) return;

        var data = payload.What;

        if (!objectManager.TryGetIdWithLogging(data.MapEvent, out var mapEventId)) return;
        if (!objectManager.TryGetIdWithLogging(data.JoiningParty, out var partyId)) return;

        mapEventLogger.DebugMapEvent(data.MapEvent, "Requesting server to join battle. PartyId={PartyId}, Side={Side}", partyId, data.Side);

        joinRoute.Submit(new BattleJoinIntent(mapEventId, partyId, data.Side), PresentJoinOutcome);
    }

    private void PresentJoinOutcome(AuthorityClientOutcome<NetworkJoinBattleReply> outcome)
    {
        if (outcome.Applied) return;
        var reply = outcome.Result;
        GameThread.RunSafe(() =>
        {
            Logger.Warning("Server rejected battle join for party {PartyId} and map event {MapEventId}",
                reply.PartyId, reply.MapEventId);

            if (Campaign.Current == null || string.IsNullOrEmpty(reply.MapEventId) || string.IsNullOrEmpty(reply.PartyId))
                return;
            if (!objectManager.TryGetObjectWithLogging<MapEvent>(reply.MapEventId, out var mapEvent)) return;
            if (!objectManager.TryGetObjectWithLogging<PartyBase>(reply.PartyId, out var party)) return;

            var encounter = PlayerEncounter.Current;
            if (!ReferenceEquals(party, PartyBase.MainParty) ||
                party.MapEventSide != null ||
                mapEvent.FindMapEventParty(party) != null ||
                encounter == null ||
                !encounter.IsJoinedBattle ||
                !ReferenceEquals(encounter._mapEvent, mapEvent))
            {
                return;
            }

            // Capture this before LeaveBattle tears down the provisional join state. During a real map-menu
            // transition the engine can clear CurrentMenuContext as part of that teardown, but the rejected
            // player still needs to return to join_encounter so the same action is retryable.
            var shouldRestoreJoinMenu = Campaign.Current.CurrentMenuContext != null;
            PlayerEncounter.LeaveBattle();
            if (shouldRestoreJoinMenu || Campaign.Current.CurrentMenuContext != null)
                GameMenu.SwitchToMenu("join_encounter");
        }, context: nameof(PresentJoinOutcome));
    }

    private AuthorityServerReply<NetworkJoinBattleReply> ExecuteJoin(
        AuthorityServerContext context,
        NetworkRequestJoinBattle data)
    {
        bool mutationStarted = false;
        string reservedControllerId = null;
        var reservationId = Guid.NewGuid();
        MapEvent mapEvent = null;
        try
        {
            if (!objectManager.TryGetObject<MapEvent>(data.MapEventId, out mapEvent) || mapEvent == null)
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "map-event-not-found");
            if (initializationBarrier.IsPending(mapEvent))
                return JoinReply(context.Header, data, AuthorityResultStatus.Unavailable, "map-event-initializing");
            if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var mobileParty) ||
                mobileParty?.Party == null)
                return JoinReply(context.Header, data, AuthorityResultStatus.Unauthorized, "player-party-not-found");
            var party = mobileParty.Party;
            if (!objectManager.TryGetId(party, out var controlledPartyId) ||
                !string.Equals(data.PartyId, controlledPartyId, StringComparison.Ordinal))
                return JoinReply(context.Header, data, AuthorityResultStatus.Unauthorized, "party-not-controlled");
            if (mapEvent.BattleState != BattleState.None || mapEvent.IsFinalized || mapEvent.HasWinner)
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "map-event-finalized");

            var existing = mapEvent.FindMapEventParty(party);
            if (existing != null && ReferenceEquals(party.MapEventSide, mapEvent.GetMapEventSide(data.Side)))
            {
                PublishJoinProof(context, data, party.MapEventSide, existing);
                return JoinReply(context.Header, data, AuthorityResultStatus.Accepted, null, statePublished: true);
            }
            if (party.MapEventSide != null || party.MapEvent != null)
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "party-already-in-map-event");

            if (mapEvent.IsActiveSlowVillageRaid() && data.Side == BattleSideEnum.Defender &&
                !CanJoinActiveSlowRaidAsDefender(mapEvent, party))
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "raid-defender-ineligible");

            var side = mapEvent.GetMapEventSide(data.Side);
            if (side == null)
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "map-event-side-not-found");
            if (!mapEvent.CanPartyJoinBattle(party, data.Side))
                return JoinReply(context.Header, data, AuthorityResultStatus.Rejected, "party-cannot-join");

            reservedControllerId = context.Player.ControllerId;
            messageBroker.Publish(context.Peer,
                new BattleJoinAccepted(data.MapEventId, reservedControllerId, reservationId));

            mutationStarted = true;
            party.MapEventSide = side;
            if (!ReferenceEquals(party.MapEventSide, side) || mapEvent.FindMapEventParty(party) == null)
                return IsolateJoin(context, data, mapEvent, "battle-join-postcondition");

            if (mapEvent.IsVillageHostileAction() && data.Side == BattleSideEnum.Attacker)
                MapEventHostileActionConsequences.Apply(mapEvent, party, "village hostile action attacker join");

            PublishJoinProof(context, data, side, mapEvent.FindMapEventParty(party));

            if (ServerBattleModeArbiter.TryGetMode(data.MapEventId, out var mode))
                network.Send(context.Peer, new NetworkBattleModeSet(data.MapEventId, (int)mode));
            if (mapEvent.BattleObserver is ForwardingBattleObserver &&
                !mapEvent.IsUnsupportedMultiPlayerHostileAction())
                network.SendAll(new NetworkOpenBattleSimulation(data.MapEventId));

            return JoinReply(context.Header, data, AuthorityResultStatus.Accepted, null, statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed authoritative battle join. MapEvent={MapEventId} Party={PartyId}",
                data.MapEventId, data.PartyId);
            return mutationStarted
                ? IsolateJoin(context, data, mapEvent, "battle-join-isolated")
                : JoinReply(context.Header, data, AuthorityResultStatus.ExecutionFailed, "battle-join-failed");
        }
        finally
        {
            if (!mutationStarted && reservedControllerId != null)
            {
                messageBroker.Publish(context.Peer,
                    new BattleJoinCancelled(data.MapEventId, reservedControllerId, reservationId));
            }
        }
    }

    /// <summary>[Client] Bridge a joiner's leave to a server request; [Server] perform it directly.</summary>
    private void Handle_PlayerLeaveBattleAttempted(MessagePayload<PlayerLeaveBattleAttempted> payload)
    {
        var leavingParty = payload.What.LeavingParty;
        if (!objectManager.TryGetIdWithLogging(leavingParty, out var partyId)) return;

        if (ModInformation.IsServer)
            RemovePartyFromBattleAndBroadcast(partyId, payload.What.FinishLocalMenus);
        else
        {
            if (leavingParty?.MapEvent == null ||
                !objectManager.TryGetIdWithLogging(leavingParty.MapEvent, out var mapEventId))
                return;
            leaveRoute.Submit(new BattleLeaveIntent(partyId, mapEventId, payload.What.FinishLocalMenus));
        }
    }

    private AuthorityServerReply<NetworkLeaveBattleResult> ExecuteLeave(
        AuthorityServerContext context,
        NetworkRequestLeaveBattle request)
    {
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var mobileParty) ||
            mobileParty?.Party == null)
            return LeaveReply(context.Header, request, AuthorityResultStatus.Unauthorized, false,
                "player-party-not-found");
        var party = mobileParty.Party;
        if (!objectManager.TryGetId(party, out var controlledPartyId) ||
            !string.Equals(request.PartyId, controlledPartyId, StringComparison.Ordinal))
            return LeaveReply(context.Header, request, AuthorityResultStatus.Unauthorized, false,
                "party-not-controlled");
        var mapEvent = party.MapEvent;
        if (mapEvent == null || !objectManager.TryGetId(mapEvent, out var actualMapEventId) ||
            !string.Equals(actualMapEventId, request.MapEventId, StringComparison.Ordinal))
            return LeaveReply(context.Header, request, AuthorityResultStatus.StaleState, false,
                "map-event-mismatch");
        if (initializationBarrier.IsPending(mapEvent))
            return LeaveReply(context.Header, request, AuthorityResultStatus.Unavailable, false,
                "map-event-initializing");

        bool leaveSiege = IsAttackingSiegeAssault(party);
        bool mutationStarted = false;
        bool mutated = false;
        try
        {
            mutationStarted = true;
            ApplyAuthoritativeLeave(party);
            mutated = party.MapEventSide == null && mapEvent.FindMapEventParty(party) == null;
            if (!mutated)
                return IsolateLeave(context, request, mapEvent, leaveSiege, "battle-leave-postcondition");

            network.SendAll(new NetworkPartyLeftBattle(request.PartyId, leaveSiege,
                request.FinishLocalMenus, context.Header.SessionId, context.Header.RequestId,
                request.MapEventId));

            if (leaveSiege && party.MobileParty?.BesiegerCamp != null)
                party.MobileParty.BesiegerCamp = null;

            messageBroker.Publish(context.Peer,
                new BattleJoinCancelled(request.MapEventId, context.Player.ControllerId));

            return LeaveReply(context.Header, request, AuthorityResultStatus.Accepted, leaveSiege, null,
                statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed authoritative battle leave. MapEvent={MapEventId} Party={PartyId}",
                request.MapEventId, request.PartyId);
            return mutationStarted
                ? IsolateLeave(context, request, mapEvent, leaveSiege, "battle-leave-isolated")
                : LeaveReply(context.Header, request, AuthorityResultStatus.ExecutionFailed, leaveSiege,
                    "battle-leave-failed");
        }
    }

    // Single-party removal does not auto-replicate (RemovePartyInternal uses RemoveAt, bypassing the
    // collection sync), so remove authoritatively and broadcast the removal explicitly.
    private void RemovePartyFromBattleAndBroadcast(
        string partyId,
        bool finishLocalMenus = true,
        NetPeer requestingPeer = null)
    {
        GameThread.RunSafe(
            () =>
            {
                if (!objectManager.TryGetObjectWithLogging<PartyBase>(partyId, out var party)) return;

                var mapEvent = party.MapEvent;
                bool leaveSiege = IsAttackingSiegeAssault(party);
                ApplyAuthoritativeLeave(party);
                // Preserve the client's PlayerSiege reference until its explicit cleanup runs.
                network.SendAll(new NetworkPartyLeftBattle(
                    partyId,
                    leaveSiege,
                    finishLocalMenus));

                if (leaveSiege && party.MobileParty?.BesiegerCamp != null)
                    party.MobileParty.BesiegerCamp = null;

                if (mapEvent != null &&
                    objectManager.TryGetId(mapEvent, out var mapEventId) &&
                    TryGetRequestingPlayer(requestingPeer, party, out var controllerId))
                {
                    messageBroker.Publish(
                        requestingPeer,
                        new BattleJoinCancelled(mapEventId, controllerId));
                }
            },
            blocking: true,
            context: nameof(RemovePartyFromBattleAndBroadcast));
    }

    /// <summary>[Client] Apply a joiner's removal from its map event side.</summary>
    private void Handle_NetworkPartyLeftBattle(MessagePayload<NetworkPartyLeftBattle> payload)
    {
        var message = payload.What;

        GameThread.RunSafe(
            () =>
            {
                if (Campaign.Current == null) return;
                if (!objectManager.TryGetObjectWithLogging<PartyBase>(message.PartyId, out var party)) return;

                ApplyNetworkLeave(
                    party,
                    message.LeaveSiege,
                    message.FinishLocalMenus);

                if (configAuthority.IsTrustedServer(payload.Who) && message.AuthorityRequestId > 0 &&
                    !string.IsNullOrEmpty(message.SessionId))
                {
                    leaveProofs[ProofKey(message.SessionId, message.AuthorityRequestId)] =
                        new BattleLeaveProof(message.SessionId, message.AuthorityRequestId,
                            message.MapEventId, message.PartyId, message.LeaveSiege);
                }
            },
            context: nameof(Handle_NetworkPartyLeftBattle));
    }

    // Authoritative campaign logic runs with patches live so removal, finalization, and replication stay ordered.
    private static void ApplyAuthoritativeLeave(PartyBase party)
    {
        if (party.MapEventSide != null)
            party.MapEventSide = null;
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current)) return default;
        return new AuthorityRequestHeader(current.ProtocolVersion, current.SessionId, requestId, current.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion ||
            !string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private static string ValidateJoinWireShape(NetworkRequestJoinBattle request)
    {
        if (!IsBoundedId(request.MapEventId) || !IsBoundedId(request.PartyId))
            return "invalid-battle-join-identifiers";
        if (request.Side != BattleSideEnum.Attacker && request.Side != BattleSideEnum.Defender)
            return "invalid-battle-side";
        return null;
    }

    private static string ValidateLeaveWireShape(NetworkRequestLeaveBattle request) =>
        IsBoundedId(request.MapEventId) && IsBoundedId(request.PartyId)
            ? null : "invalid-battle-leave-identifiers";

    private static bool IsBoundedId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static string BuildJoinCommandKey(string mapEventId, string partyId, BattleSideEnum side) =>
        string.Concat(mapEventId.Length, ":", mapEventId, ":", partyId.Length, ":", partyId, ":", (int)side);

    private static string BuildLeaveCommandKey(string partyId, string mapEventId, bool finishLocalMenus) =>
        string.Concat(mapEventId.Length, ":", mapEventId, ":", partyId.Length, ":", partyId, ":",
            finishLocalMenus ? "1" : "0");

    private AuthorityServerReply<NetworkJoinBattleReply> JoinReply(AuthorityRequestHeader header,
        NetworkRequestJoinBattle request, AuthorityResultStatus status, string reasonCode,
        bool statePublished = false, bool suppressReply = false) =>
        new(new NetworkJoinBattleReply(header, status, request.MapEventId, request.PartyId, (int)request.Side,
            reasonCode), statePublished, suppressReply);

    private AuthorityServerReply<NetworkLeaveBattleResult> LeaveReply(AuthorityRequestHeader header,
        NetworkRequestLeaveBattle request, AuthorityResultStatus status, bool leaveSiege, string reasonCode,
        bool statePublished = false, bool suppressReply = false) =>
        new(new NetworkLeaveBattleResult(header, status, request.PartyId, request.MapEventId, leaveSiege,
            request.FinishLocalMenus, reasonCode), statePublished, suppressReply);

    private AuthorityCommitProbeResult ProbeJoinCommit(NetworkJoinBattleReply result)
    {
        var key = ProofKey(result.Header.SessionId, result.Header.RequestId);
        if (!joinProofs.TryGetValue(key, out var proof) || !IsExpectedJoinProof(proof, result))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject<MapEvent>(result.MapEventId, out var mapEvent) || mapEvent == null ||
            !objectManager.TryGetObject<PartyBase>(result.PartyId, out var party) || party == null)
            return AuthorityCommitProbeResult.Pending;
        if (result.Side != (int)BattleSideEnum.Attacker && result.Side != (int)BattleSideEnum.Defender)
            return AuthorityCommitProbeResult.Invalid;
        var expectedSide = mapEvent.GetMapEventSide((BattleSideEnum)result.Side);
        if (!ReferenceEquals(party.MapEventSide, expectedSide) || mapEvent.FindMapEventParty(party) == null)
            return AuthorityCommitProbeResult.Pending;
        joinProofs.TryRemove(key, out _);
        return AuthorityCommitProbeResult.Applied;
    }

    private AuthorityCommitProbeResult ProbeLeaveCommit(NetworkLeaveBattleResult result)
    {
        var key = ProofKey(result.Header.SessionId, result.Header.RequestId);
        if (!leaveProofs.TryGetValue(key, out var proof) || !IsExpectedLeaveProof(proof, result))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject<PartyBase>(result.PartyId, out var party) || party == null)
            return AuthorityCommitProbeResult.Pending;
        if (party.MapEventSide != null || party.MapEvent != null) return AuthorityCommitProbeResult.Pending;
        if (result.LeaveSiege && party.MobileParty?.BesiegerCamp != null)
            return AuthorityCommitProbeResult.Pending;
        leaveProofs.TryRemove(key, out _);
        return AuthorityCommitProbeResult.Applied;
    }

    private static string ProofKey(string sessionId, long requestId) =>
        string.Concat(sessionId ?? string.Empty, ":", requestId);

    internal static bool IsExpectedJoinProof(BattleJoinProof proof, NetworkJoinBattleReply result) =>
        string.Equals(proof.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        proof.RequestId == result.Header.RequestId &&
        string.Equals(proof.MapEventId, result.MapEventId, StringComparison.Ordinal) &&
        string.Equals(proof.PartyId, result.PartyId, StringComparison.Ordinal) &&
        proof.Side == result.Side;

    internal static bool IsExpectedLeaveProof(BattleLeaveProof proof, NetworkLeaveBattleResult result) =>
        string.Equals(proof.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        proof.RequestId == result.Header.RequestId &&
        string.Equals(proof.MapEventId, result.MapEventId, StringComparison.Ordinal) &&
        string.Equals(proof.PartyId, result.PartyId, StringComparison.Ordinal) &&
        proof.LeaveSiege == result.LeaveSiege;

    private AuthorityServerReply<NetworkJoinBattleReply> IsolateJoin(AuthorityServerContext context,
        NetworkRequestJoinBattle request, MapEvent mapEvent, string reasonCode)
    {
        Logger.Fatal("Isolating battle participants after ambiguous join. Route=battle.join RequestId={RequestId} MapEvent={MapEventId}",
            context.Header.RequestId, request.MapEventId);
        DisconnectAllCampaignPeers(context.Peer);
        return JoinReply(context.Header, request, AuthorityResultStatus.ExecutionFailed, reasonCode,
            suppressReply: true);
    }

    private AuthorityServerReply<NetworkLeaveBattleResult> IsolateLeave(AuthorityServerContext context,
        NetworkRequestLeaveBattle request, MapEvent mapEvent, bool leaveSiege, string reasonCode)
    {
        Logger.Fatal("Isolating battle participants after ambiguous leave. Route=battle.leave RequestId={RequestId} MapEvent={MapEventId}",
            context.Header.RequestId, request.MapEventId);
        DisconnectAllCampaignPeers(context.Peer);
        return LeaveReply(context.Header, request, AuthorityResultStatus.ExecutionFailed, leaveSiege, reasonCode,
            suppressReply: true);
    }

    private void PublishJoinProof(AuthorityServerContext context, NetworkRequestJoinBattle request,
        MapEventSide side, MapEventParty mapEventParty)
    {
        if (side == null || mapEventParty == null ||
            !objectManager.TryGetId(side, out var sideId) ||
            !objectManager.TryGetId(mapEventParty, out var mapEventPartyId))
            throw new InvalidOperationException("The authoritative battle join proof could not be identified.");

        network.Send(context.Peer, new NetworkAddBattleParty(sideId, mapEventPartyId,
            context.Header.SessionId, context.Header.RequestId, request.MapEventId, request.PartyId,
            (int)request.Side));
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

    private bool TryGetRequestingPlayer(
        NetPeer requestingPeer,
        PartyBase party,
        out string controllerId)
    {
        controllerId = null;
        if (requestingPeer == null ||
            !playerManager.TryGetPlayer(requestingPeer, out var player) ||
            !objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var playerParty) ||
            !ReferenceEquals(playerParty.Party, party))
        {
            return false;
        }

        controllerId = player.ControllerId;
        return true;
    }

    private static bool CanJoinActiveSlowRaidAsDefender(MapEvent mapEvent, PartyBase party)
    {
        var mobileParty = party?.MobileParty;
        var settlement = mapEvent?.MapEventSettlement;
        var encounterModel = Campaign.Current?.Models?.EncounterModel;
        var joiningFaction = party?.MapFaction;
        var attackerFaction = mapEvent?.AttackerSide?.LeaderParty?.MapFaction;
        if (mobileParty?.IsActive != true || settlement?.IsVillage != true || encounterModel == null ||
            joiningFaction == null || attackerFaction == null || attackerFaction.NotAttackableByPlayerUntilTime.IsFuture)
            return false;

        if (!IsFactionCompatible(mapEvent.DefenderSide, joiningFaction, hostile: false) ||
            !IsFactionCompatible(mapEvent.AttackerSide, joiningFaction, hostile: true))
        {
            return false;
        }

        var targetPosition = mobileParty.IsTargetingPort && settlement.HasPort
            ? settlement.PortPosition
            : settlement.GatePosition;
        return mobileParty.CurrentSettlement == settlement ||
            mobileParty.Position.Distance(targetPosition) <=
            encounterModel.NeededMaximumDistanceForEncounteringVillage + PositionSyncDriftSlack;
    }

    private static bool IsFactionCompatible(MapEventSide side, IFaction joiningFaction, bool hostile)
    {
        if (side?.Parties == null || side.Parties.Count == 0)
            return false;

        foreach (var involved in side.Parties)
        {
            var involvedParty = involved?.Party;
            if (involvedParty?.IsActive != true || involvedParty.MapFaction == null ||
                VillageHostileFactionStanceHelper.HasWarStance(involvedParty.MapFaction, joiningFaction) != hostile)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAttackingSiegeAssault(PartyBase party)
    {
        return party.MapEvent?.IsSiegeAssault == true && party.Side == BattleSideEnum.Attacker;
    }

    // Apply the received removal under AllowedThread and close this client's encounter UI when appropriate.
    private void ApplyNetworkLeave(PartyBase party, bool leaveSiege, bool finishLocalMenus)
    {
        using (new AllowedThread())
        {
            var mapEvent = party.MapEvent;
            bool isSiegeAssault = mapEvent?.IsSiegeAssault == true;
            var siegeSettlement = mapEvent?.MapEventSettlement;
            bool isMainParty = party == PartyBase.MainParty;
            var mobileParty = party.MobileParty;

            if (party.MapEventSide != null)
                party.MapEventSide = null;

            if (leaveSiege && mobileParty?.BesiegerCamp != null)
                mobileParty.BesiegerCamp = null;

            if (isMainParty && finishLocalMenus)
            {
                if (leaveSiege || isSiegeAssault)
                {
                    siegeEventInterface.FinishLocalPlayerSiegeLeave(
                        siegeSettlement,
                        forcePlayerOutFromSettlement: false);
                }
                else if (PlayerEncounter.Current != null)
                {
                    PlayerEncounter.Finish(false);
                }
            }

            if (leaveSiege && isMainParty)
                mobileParty?.SetMoveModeHold();
        }
    }
}
