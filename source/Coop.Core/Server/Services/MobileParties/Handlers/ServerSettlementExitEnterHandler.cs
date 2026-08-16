using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Server.Services.MobileParties.Messages;
using Coop.Core.Server.Services.Settlements;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace Coop.Core.Server.Services.MobileParties.Handlers;

/// <summary>Owns authoritative settlement membership mutations and their exact requester proofs.</summary>
public class ServerSettlementExitEnterHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISettlementInterface settlementInterface;
    private readonly IKingdomCreationSettlementTracker settlementTracker;
    private readonly ISettlementEncounterDistanceValidator distanceValidator;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<StartIntent, NetworkStartSettlementEncounter> startRoute;
    private readonly IAuthorityRouteHandle<EndIntent, NetworkSettlementEncounterLeaveResult> endRoute;
    private static readonly ILogger Logger = LogManager.GetLogger<ServerSettlementExitEnterHandler>();

    public ServerSettlementExitEnterHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        ISettlementInterface settlementInterface,
        IKingdomCreationSettlementTracker settlementTracker,
        ISettlementEncounterDistanceValidator distanceValidator,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.settlementInterface = settlementInterface;
        this.settlementTracker = settlementTracker;
        this.distanceValidator = distanceValidator;
        this.configAuthority = configAuthority;

        startRoute = authorityRequestRouter.Register(
            AuthorityRoute<StartIntent, NetworkRequestStartSettlementEncounter, NetworkStartSettlementEncounter>.Define(
                "settlement.encounter.start", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestStartSettlementEncounter(intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateStartWireShape,
                request => Key(request.SettlementId), ValidateHeader, ExecuteStart,
                CreateStartTerminalResult, _ => AuthorityCommitProbeResult.Pending, _ => { }, _ => { },
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedStartResult));
        endRoute = authorityRequestRouter.Register(
            AuthorityRoute<EndIntent, NetworkRequestEndSettlementEncounter, NetworkSettlementEncounterLeaveResult>.Define(
                "settlement.encounter.end", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestEndSettlementEncounter(intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateEndWireShape,
                request => Key(request.SettlementId), ValidateHeader, ExecuteEnd,
                CreateEndTerminalResult, _ => AuthorityCommitProbeResult.Pending, _ => { }, _ => { },
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedEndResult));

        // Host-originated native callbacks retain their existing replication ownership.
        messageBroker.Subscribe<PartyEnterSettlementAttempted>(Handle);
        messageBroker.Subscribe<PartyLeaveSettlementAttempted>(Handle);
    }

    private AuthorityServerReply<NetworkStartSettlementEncounter> ExecuteStart(
        AuthorityServerContext context,
        NetworkRequestStartSettlementEncounter request)
    {
        string partyId = context.Player.MobilePartyId;
        if (string.IsNullOrWhiteSpace(partyId) ||
            !objectManager.TryGetObjectWithLogging(partyId, out MobileParty party))
            return RejectStart(context.Header, partyId, request.SettlementId, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging(request.SettlementId, out Settlement settlement))
            return RejectStart(context.Header, partyId, request.SettlementId, "settlement-not-found");
        if (party.Party?.MapEventSide != null)
            return RejectStart(context.Header, partyId, request.SettlementId, "already-in-map-event");

        if (party.CurrentSettlement != null)
        {
            if (!ReferenceEquals(party.CurrentSettlement, settlement))
                return RejectStart(context.Header, partyId, request.SettlementId, "already-in-another-settlement");
            return PublishIdempotentStart(context, partyId, request.SettlementId,
                SettlementEncounterStartMode.EnteredSettlement);
        }

        if (!distanceValidator.TryValidate(party, settlement, out var rejectionReason))
            return RejectStart(context.Header, partyId, request.SettlementId,
                NormalizeDistanceReason(rejectionReason));
        if (IsHideoutOccupiedByAnotherPlayer(party, settlement))
            return RejectStart(context.Header, partyId, request.SettlementId, "hideout-occupied");

        bool encounterOnly = settlement.IsUnderSiege || (settlement.IsVillage && settlement.IsUnderRaid);
        if (encounterOnly)
            return PublishIdempotentStart(context, partyId, request.SettlementId,
                SettlementEncounterStartMode.EncounterOnly);

        bool mutationBoundaryCrossed = false;
        try
        {
            mutationBoundaryCrossed = true;
            settlementInterface.PartyEnterSettlement(party, settlement);
            if (!ReferenceEquals(party.CurrentSettlement, settlement))
                throw new InvalidOperationException("Party did not enter the requested settlement.");

            network.SendAllBut(context.Peer, new NetworkPartyEnterSettlement(
                Compact(request.SettlementId, typeof(Settlement)),
                Compact(partyId, typeof(MobileParty))));
            var result = AcceptedStart(context.Header, partyId, request.SettlementId,
                SettlementEncounterStartMode.EnteredSettlement);
            return new AuthorityServerReply<NetworkStartSettlementEncounter>(result, statePublished: true);
        }
        catch (Exception exception)
        {
            return IsolateStartAfterMutation(context, partyId, request.SettlementId,
                mutationBoundaryCrossed, exception);
        }
    }

    private AuthorityServerReply<NetworkStartSettlementEncounter> PublishIdempotentStart(
        AuthorityServerContext context,
        string partyId,
        string settlementId,
        SettlementEncounterStartMode mode)
    {
        var result = AcceptedStart(context.Header, partyId, settlementId, mode);
        return new AuthorityServerReply<NetworkStartSettlementEncounter>(result, statePublished: true);
    }

    private AuthorityServerReply<NetworkSettlementEncounterLeaveResult> ExecuteEnd(
        AuthorityServerContext context,
        NetworkRequestEndSettlementEncounter request)
    {
        string partyId = context.Player.MobilePartyId;
        if (string.IsNullOrWhiteSpace(partyId) ||
            !objectManager.TryGetObjectWithLogging(partyId, out MobileParty party))
            return RejectEnd(context.Header, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.Suppressed, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging(request.SettlementId, out Settlement expectedSettlement))
            return RejectEnd(context.Header, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.Suppressed, "settlement-not-found");

        if (party.CurrentSettlement != null && !ReferenceEquals(party.CurrentSettlement, expectedSettlement))
            return RejectEnd(context.Header, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.Suppressed, "stale-settlement-context");

        if (settlementTracker.TryConsumeLeave(party, partyId))
            return RejectEnd(context.Header, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.Suppressed, "leave-suppressed");

        if (party.CurrentSettlement == null)
            return PublishIdempotentEnd(context, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.AlreadyOutside);
        var priorMapEvent = party.Party?.MapEvent;
        bool leavingHideout = priorMapEvent?.EventType == MapEvent.BattleTypes.Hideout;
        bool mutationBoundaryCrossed = false;
        try
        {
            mutationBoundaryCrossed = true;
            LeaveHideoutMapEvent(party);
            settlementInterface.PartyLeaveSettlement(party);
            if (party.CurrentSettlement != null ||
                (leavingHideout && ReferenceEquals(party.Party?.MapEvent, priorMapEvent)))
                throw new InvalidOperationException("Party remained in its settlement encounter after leave.");

            network.SendAllBut(context.Peer,
                new NetworkPartyLeaveSettlement(Compact(partyId, typeof(MobileParty))));
            var result = AcceptedEnd(context.Header, partyId, request.SettlementId,
                SettlementEncounterLeaveOutcome.Applied);
            return new AuthorityServerReply<NetworkSettlementEncounterLeaveResult>(result, statePublished: true);
        }
        catch (Exception exception)
        {
            return IsolateEndAfterMutation(context, partyId, request.SettlementId,
                mutationBoundaryCrossed, exception);
        }
    }

    private AuthorityServerReply<NetworkSettlementEncounterLeaveResult> PublishIdempotentEnd(
        AuthorityServerContext context,
        string partyId,
        string settlementId,
        SettlementEncounterLeaveOutcome outcome)
    {
        var result = AcceptedEnd(context.Header, partyId, settlementId, outcome);
        return new AuthorityServerReply<NetworkSettlementEncounterLeaveResult>(result, statePublished: true);
    }

    private AuthorityServerReply<NetworkStartSettlementEncounter> IsolateStartAfterMutation(
        AuthorityServerContext context, string partyId, string settlementId, bool boundary, Exception exception)
    {
        Logger.Fatal(exception,
            "Ambiguous settlement entry; isolating campaign peers. Route={Route} Session={SessionId} Request={RequestId} Party={PartyId} Settlement={SettlementId} Boundary={Boundary}",
            context.RouteId, context.Header.SessionId, context.Header.RequestId, partyId, settlementId, boundary);
        DisconnectAllPeers("ambiguous settlement entry", context);
        return new AuthorityServerReply<NetworkStartSettlementEncounter>(
            CreateStartTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed,
                "settlement-start-isolated"), statePublished: false, suppressReply: true);
    }

    private AuthorityServerReply<NetworkSettlementEncounterLeaveResult> IsolateEndAfterMutation(
        AuthorityServerContext context, string partyId, string settlementId, bool boundary, Exception exception)
    {
        Logger.Fatal(exception,
            "Ambiguous settlement leave; isolating campaign peers. Route={Route} Session={SessionId} Request={RequestId} Party={PartyId} Settlement={SettlementId} Boundary={Boundary}",
            context.RouteId, context.Header.SessionId, context.Header.RequestId, partyId, settlementId, boundary);
        DisconnectAllPeers("ambiguous settlement leave", context);
        return new AuthorityServerReply<NetworkSettlementEncounterLeaveResult>(
            CreateEndTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed,
                "settlement-leave-isolated"), statePublished: false, suppressReply: true);
    }

    private bool IsHideoutOccupiedByAnotherPlayer(MobileParty enteringParty, Settlement settlement)
    {
        if (!settlement.IsHideout) return false;
        foreach (var player in playerManager.Players)
        {
            if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var playerParty) ||
                ReferenceEquals(playerParty, enteringParty)) continue;
            if (ReferenceEquals(playerParty.CurrentSettlement, settlement)) return true;
        }
        return false;
    }

    private void LeaveHideoutMapEvent(MobileParty party)
    {
        var partyBase = party?.Party;
        var mapEvent = partyBase?.MapEvent;
        if (mapEvent?.EventType != MapEvent.BattleTypes.Hideout) return;
        if (partyBase.MapEventSide?.LeaderParty == partyBase)
            messageBroker.Publish(this, new MapEventFinalizeAttempted(mapEvent));
        else
            messageBroker.Publish(this, new PlayerLeaveBattleAttempted(partyBase));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

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

    private static string ValidateStartWireShape(NetworkRequestStartSettlementEncounter request) =>
        ValidateSettlementId(request.SettlementId);
    private static string ValidateEndWireShape(NetworkRequestEndSettlementEncounter request) =>
        ValidateSettlementId(request.SettlementId);
    private static string ValidateSettlementId(string id) =>
        string.IsNullOrWhiteSpace(id) || id.Length > 256 ? "invalid-settlement-id" : null;
    private static string NormalizeDistanceReason(string reason) => reason switch
    {
        "your party is too far from the settlement" => "too-far",
        _ => "distance-validation-unavailable",
    };
    private static string Key(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty);

    private static AuthorityResultHeader ResultHeader(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason = null) =>
        new(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason);

    private static NetworkStartSettlementEncounter AcceptedStart(
        AuthorityRequestHeader header, string partyId, string settlementId, SettlementEncounterStartMode mode) =>
        new(partyId, settlementId, mode, ResultHeader(header, AuthorityResultStatus.Accepted));

    private static AuthorityServerReply<NetworkStartSettlementEncounter> RejectStart(
        AuthorityRequestHeader header, string partyId, string settlementId, string reason) =>
        new(new NetworkStartSettlementEncounter(partyId, settlementId,
            SettlementEncounterStartMode.EnteredSettlement,
            ResultHeader(header, AuthorityResultStatus.Rejected, reason)), statePublished: false);

    private static NetworkSettlementEncounterLeaveResult AcceptedEnd(
        AuthorityRequestHeader header, string partyId, string settlementId, SettlementEncounterLeaveOutcome outcome) =>
        new(partyId, settlementId, outcome, ResultHeader(header, AuthorityResultStatus.Accepted));

    private static AuthorityServerReply<NetworkSettlementEncounterLeaveResult> RejectEnd(
        AuthorityRequestHeader header, string partyId, string settlementId,
        SettlementEncounterLeaveOutcome outcome, string reason) =>
        new(new NetworkSettlementEncounterLeaveResult(partyId, settlementId, outcome,
            ResultHeader(header, AuthorityResultStatus.Rejected, reason)), statePublished: false);

    private static NetworkStartSettlementEncounter CreateStartTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, SettlementEncounterStartMode.EnteredSettlement, ResultHeader(header, status, reason));

    private static NetworkSettlementEncounterLeaveResult CreateEndTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, SettlementEncounterLeaveOutcome.Suppressed, ResultHeader(header, status, reason));

    private static bool IsExpectedStartResult(
        NetworkRequestStartSettlementEncounter request, NetworkStartSettlementEncounter result) =>
        request.Header.RequestId == result.Header.RequestId &&
        request.Header.ExpectedRevision == result.Header.CommittedRevision &&
        string.Equals(request.Header.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static bool IsExpectedEndResult(
        NetworkRequestEndSettlementEncounter request, NetworkSettlementEncounterLeaveResult result) =>
        request.Header.RequestId == result.Header.RequestId &&
        request.Header.ExpectedRevision == result.Header.CommittedRevision &&
        string.Equals(request.Header.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private void DisconnectAllPeers(string reason, AuthorityServerContext context)
    {
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player) ||
                !playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            DisconnectPeer(peer, reason, context);
        }
    }

    private static void DisconnectPeer(NetPeer peer, string reason, AuthorityServerContext context)
    {
        try { peer?.Disconnect(); }
        catch (Exception exception)
        {
            Logger.Fatal(exception,
                "Could not disconnect peer after {Reason}. Route={Route} Session={SessionId} Request={RequestId}",
                reason, context.RouteId, context.Header.SessionId, context.Header.RequestId);
        }
    }

    private void Handle(MessagePayload<PartyEnterSettlementAttempted> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.Settlement, out var settlementId) ||
            !objectManager.TryGetIdWithLogging(payload.What.MobileParty, out var partyId)) return;
        network.SendAll(new NetworkPartyEnterSettlement(
            Compact(settlementId, typeof(Settlement)), Compact(partyId, typeof(MobileParty))));
        settlementInterface.OnPartyEnteredSettlement(payload.What.Settlement, payload.What.MobileParty);
    }

    private void Handle(MessagePayload<PartyLeaveSettlementAttempted> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.MobileParty, out var partyId)) return;
        if (settlementTracker.TryConsumeLeave(payload.What.MobileParty, partyId)) return;
        network.SendAll(new NetworkPartyLeaveSettlement(Compact(partyId, typeof(MobileParty))));
        settlementInterface.OnPartyLeftSettlement(payload.What.MobileParty);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyEnterSettlementAttempted>(Handle);
        messageBroker.Unsubscribe<PartyLeaveSettlementAttempted>(Handle);
        startRoute.Dispose();
        endRoute.Dispose();
    }

    private sealed class StartIntent
    {
        public StartIntent(string settlementId) => SettlementId = settlementId;
        public string SettlementId { get; }
    }

    private sealed class EndIntent
    {
        public EndIntent(string settlementId) => SettlementId = settlementId;
        public string SettlementId { get; }
    }
}
