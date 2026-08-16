using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Services.Settlements;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.BesiegerCamps.Messages;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEvents.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace Coop.Core.Server.Services.SiegeEvents.Handlers;

/// <summary>
/// Runs client siege entry and exit requests authoritatively. The approval is sent from inside the
/// game-thread closure after the world change, so the reliable-ordered channel delivers the siege
/// object creates and camp writes to the requester before its local menu continuation runs.
/// </summary>
internal class ServerSiegeEntryHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerSiegeEntryHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISettlementEncounterDistanceValidator distanceValidator;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly ISettlementInterface settlementInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkBesiegeSettlementApproved> besiegeRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkJoinSiegeCampApproved> joinRoute;
    private readonly IAuthorityRouteHandle<SiegeBreakIntent, NetworkBreakSiegeApproved> breakRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkSiegeAssaultApproved> assaultRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkBreakInContinuationApproved> breakInRoute;

    public ServerSiegeEntryHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        ISettlementEncounterDistanceValidator distanceValidator,
        ISiegeEventInterface siegeEventInterface,
        ISettlementInterface settlementInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.distanceValidator = distanceValidator;
        this.siegeEventInterface = siegeEventInterface;
        this.settlementInterface = settlementInterface;
        this.configAuthority = configAuthority;
        besiegeRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEntryIntent, NetworkRequestBesiegeSettlement, NetworkBesiegeSettlementApproved>.Define(
                "siege.besiege-settlement", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBesiegeSettlement(intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateBesiegeWireShape, BuildBesiegeCommandKey,
                ValidateHeader, ExecuteBesiege, CreateBesiegeTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedBesiegeResult));
        joinRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEntryIntent, NetworkRequestJoinSiegeCamp, NetworkJoinSiegeCampApproved>.Define(
                "siege.join-camp", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestJoinSiegeCamp(intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateJoinWireShape, BuildJoinCommandKey,
                ValidateHeader, ExecuteJoin, CreateJoinTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedJoinResult));
        breakRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeBreakIntent, NetworkRequestBreakSiege, NetworkBreakSiegeApproved>.Define(
                "siege.break", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBreakSiege(intent.PartyId, intent.FinishLocalMenus, header),
                request => request.Header, result => result.Header, ValidateBreakWireShape, BuildBreakCommandKey,
                ValidateHeader, ExecuteBreak, CreateBreakTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedBreakResult));
        assaultRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEntryIntent, NetworkRequestSiegeAssault, NetworkSiegeAssaultApproved>.Define(
                "siege.assault", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestSiegeAssault(intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateAssaultWireShape, BuildAssaultCommandKey,
                ValidateHeader, ExecuteAssault, CreateAssaultTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedAssaultResult));
        breakInRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEntryIntent, NetworkRequestBreakInContinuation, NetworkBreakInContinuationApproved>.Define(
                "siege.break-in-continuation", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBreakInContinuation(null, intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateBreakInWireShape, BuildBreakInCommandKey,
                ValidateHeader, ExecuteBreakInContinuation, CreateBreakInTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedBreakInResult));
        messageBroker.Subscribe<SiegeAssaultStarted>(HandleAssaultStarted);
        messageBroker.Subscribe<SiegePreparationStarted>(HandlePreparationStarted);
        messageBroker.Subscribe<SiegeEndedWithoutBattle>(HandleSiegeEnded);
        messageBroker.Subscribe<SiegeCampPositionRolled>(HandleCampPosition);
    }

    private static bool CanApplyBreakInContinuation(
        MobileParty party,
        Settlement settlement,
        bool alreadyEntered)
    {
        if (!party.IsActive) return false;
        if (party.CurrentSettlement != null && !alreadyEntered) return false;
        if (party.BesiegerCamp != null) return false;

        var partyBase = party.Party;
        var mapEventSide = partyBase.MapEventSide;
        var validEnteredMapEvent = alreadyEntered &&
            ReferenceEquals(party.MapEvent, settlement.Party?.MapEvent) &&
            partyBase.Side == BattleSideEnum.Defender;
        if (mapEventSide != null && !validEnteredMapEvent) return false;

        var siegeEvent = settlement.SiegeEvent;
        return siegeEvent != null &&
            siegeEvent.CanPartyJoinSide(partyBase, BattleSideEnum.Defender);
    }

    // Runs on the game thread already; joins defenders with patches live before broadcasting the prompts.
    private void HandleAssaultStarted(MessagePayload<SiegeAssaultStarted> payload)
    {
        var obj = payload.What;

        JoinConnectedSettlementDefenders(obj.AttackerParty, obj.Settlement);

        if (!objectManager.TryGetIdWithLogging(obj.AttackerParty, out var attackerPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        // Broadcast; each client checks locally whether its party is inside the settlement.
        network.SendAll(new NetworkPromptSiegeDefense(attackerPartyId, settlementId));
        // Also prompt the besieging players to adopt the replicated assault as their encounter so they can enter it.
        network.SendAll(new NetworkPromptSiegeAssault(attackerPartyId, settlementId));
    }

    private void JoinConnectedSettlementDefenders(MobileParty attackerParty, Settlement settlement)
    {
        var mapEvent = attackerParty?.MapEvent;
        var defenderSide = mapEvent?.DefenderSide;
        if (defenderSide == null) return;

        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player)) continue;
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(player.MobilePartyId, out var party)) continue;
            if (party.CurrentSettlement != settlement || party.Party.MapEventSide != null) continue;
            if (!mapEvent.CanPartyJoinBattle(party.Party, BattleSideEnum.Defender)) continue;

            party.Party.MapEventSide = defenderSide;
        }
    }

    // Runs on the game thread already — published from the StartSiegeEvent postfix, after the whole siege
    // graph was broadcast, so the prompt arrives behind it on the reliable-ordered channel.
    private void HandlePreparationStarted(MessagePayload<SiegePreparationStarted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.BesiegerParty, out var attackerPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        // Broadcast; each client checks locally whether its party is inside the settlement.
        network.SendAll(new NetworkPromptSiegePreparation(attackerPartyId, settlementId));
    }

    // Runs on the game thread already — published from the FinalizeSiegeEvent finalizer, behind the
    // replicated siege teardown.
    private void HandleSiegeEnded(MessagePayload<SiegeEndedWithoutBattle> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        string leaderPartyId = null;
        if (obj.LeaderParty != null && playerManager.Contains(obj.LeaderParty))
            objectManager.TryGetIdWithLogging(obj.LeaderParty, out leaderPartyId);

        network.SendAll(new NetworkPromptSiegeEnded(
            settlementId,
            obj.BesiegerDefeated,
            leaderPartyId,
            GetPlayerPartyIds(obj.AttackerParties),
            GetPlayerPartyIds(obj.DefenderParties),
            obj.InterruptedActiveAssault));
    }

    private string[] GetPlayerPartyIds(IEnumerable<MobileParty> parties)
    {
        var ids = new List<string>();
        foreach (var party in parties)
        {
            if (party == null || !playerManager.Contains(party)) continue;
            if (objectManager.TryGetIdWithLogging(party, out var partyId))
                ids.Add(partyId);
        }

        return ids.ToArray();
    }

    // Runs on the game thread already — published from the party-joined-siege patch; only resolves an id and broadcasts, so no GameThread.RunSafe.
    private void HandleCampPosition(MessagePayload<SiegeCampPositionRolled> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;

        network.SendAll(new NetworkSnapSiegeCampPartyPosition(partyId, obj.Position));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateBesiegeWireShape(NetworkRequestBesiegeSettlement request) =>
        ValidateEntryIdentifiers(request.PartyId, request.SettlementId);

    private static string ValidateJoinWireShape(NetworkRequestJoinSiegeCamp request) =>
        ValidateEntryIdentifiers(request.PartyId, request.SettlementId);

    private static string ValidateBreakWireShape(NetworkRequestBreakSiege request) =>
        string.IsNullOrWhiteSpace(request.PartyId) || request.PartyId.Length > 256
            ? "invalid-siege-break-party" : null;

    private static string ValidateAssaultWireShape(NetworkRequestSiegeAssault request) =>
        ValidateEntryIdentifiers(request.PartyId, request.SettlementId);

    private static string ValidateBreakInWireShape(NetworkRequestBreakInContinuation request) =>
        ValidateEntryIdentifiers(request.PartyId, request.SettlementId);

    private static string ValidateEntryIdentifiers(string partyId, string settlementId) =>
        string.IsNullOrWhiteSpace(partyId) || partyId.Length > 256 ||
        string.IsNullOrWhiteSpace(settlementId) || settlementId.Length > 256
            ? "invalid-siege-entry-identifiers" : null;

    private static string BuildBesiegeCommandKey(NetworkRequestBesiegeSettlement request) =>
        BuildEntryCommandKey(request.PartyId, request.SettlementId);

    private static string BuildJoinCommandKey(NetworkRequestJoinSiegeCamp request) =>
        BuildEntryCommandKey(request.PartyId, request.SettlementId);

    private static string BuildBreakCommandKey(NetworkRequestBreakSiege request) =>
        string.Concat(request.PartyId.Length, ":", request.PartyId, ":", request.FinishLocalMenus);

    private static string BuildAssaultCommandKey(NetworkRequestSiegeAssault request) =>
        BuildEntryCommandKey(request.PartyId, request.SettlementId);

    private static string BuildBreakInCommandKey(NetworkRequestBreakInContinuation request) =>
        BuildEntryCommandKey(request.PartyId, request.SettlementId);

    private static string BuildEntryCommandKey(string partyId, string settlementId) =>
        string.Concat(partyId.Length, ":", partyId, ":", settlementId.Length, ":", settlementId);

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

    private AuthorityServerReply<NetworkSiegeAssaultApproved> ExecuteAssault(
        AuthorityServerContext context,
        NetworkRequestSiegeAssault request)
    {
        if (!string.Equals(context.Player.MobilePartyId, request.PartyId, StringComparison.Ordinal))
            return RejectAssault(context, request, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return RejectAssault(context, request, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            return RejectAssault(context, request, "settlement-not-found");

        var camp = settlement.SiegeEvent?.BesiegerCamp;
        if (!TryValidateAssault(party, settlement, camp, out var rejectionReason))
            return RejectAssault(context, request, rejectionReason);

        var currentMapEvent = settlement.Party?.MapEvent;
        bool mutationBoundaryCrossed = false;
        try
        {
            if (currentMapEvent == null)
            {
                // Crossing this call can create and globally publish the MapEvent. There is no safe
                // rollback boundary after it starts, so all replicas must be isolated on ambiguity.
                mutationBoundaryCrossed = true;
                StartBattleAction.ApplyStartAssaultAgainstWalls(camp.LeaderParty, settlement);
            }

            var appliedMapEvent = settlement.Party?.MapEvent;
            if (appliedMapEvent == null || !appliedMapEvent.IsSiegeAssault ||
                !ReferenceEquals(party.MapEvent, appliedMapEvent))
                throw new InvalidOperationException("Canonical siege assault was not established after the command.");
            if (!objectManager.TryGetId(camp.LeaderParty, out var leaderId))
                throw new InvalidOperationException("Could not resolve the canonical siege assault leader.");
            if (!objectManager.TryGetId(appliedMapEvent, out var mapEventId))
                throw new InvalidOperationException("Could not resolve the canonical siege assault map event.");

            // The native mutation has already queued global MapEvent replication and its ordinary prompts.
            // Queue an exact requester proof behind them before the Accepted result.
            network.Send(context.Peer, new NetworkPromptSiegeAssault(
                leaderId,
                request.SettlementId,
                AcceptedHeader(context.Header),
                request.PartyId,
                mapEventId));
            return new AuthorityServerReply<NetworkSiegeAssaultApproved>(
                CreateAssaultResult(context.Header, request, AuthorityResultStatus.Accepted, null), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Siege assault failed after entering authority mutation. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Settlement={SettlementId}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, request.SettlementId);
            if (mutationBoundaryCrossed)
                DisconnectAllConnectedPeers("ambiguous siege assault mutation", context);
            else
                DisconnectPeer(context.Peer, "failed siege assault idempotent acknowledgement", context);
            return new AuthorityServerReply<NetworkSiegeAssaultApproved>(
                CreateAssaultResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, "siege-assault-isolated"),
                statePublished: false,
                suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkBreakInContinuationApproved> ExecuteBreakInContinuation(
        AuthorityServerContext context,
        NetworkRequestBreakInContinuation request)
    {
        if (!string.Equals(context.Player.MobilePartyId, request.PartyId, StringComparison.Ordinal))
            return RejectBreakIn(context, request, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return RejectBreakIn(context, request, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            return RejectBreakIn(context, request, "settlement-not-found");

        bool alreadyEntered = ReferenceEquals(party.CurrentSettlement, settlement);
        if (!CanApplyBreakInContinuation(party, settlement, alreadyEntered))
            return RejectBreakIn(context, request, "cannot-continue-break-in");

        bool mutationBoundaryCrossed = false;
        try
        {
            if (!alreadyEntered)
            {
                // This prefix can publish PartyEnterSettlement to every replica before the native
                // operation completes. Any later failure has global, unrecoverable reach.
                mutationBoundaryCrossed = true;
                settlementInterface.PartyEnterSettlement(party, settlement);
            }

            if (!ReferenceEquals(party.CurrentSettlement, settlement))
                throw new InvalidOperationException("Canonical settlement entry was not established after break-in continuation.");

            // Both the fresh and idempotent branches get an exact requester proof after the generic
            // replicated entry, and before the terminal result.
            network.Send(context.Peer, new NetworkPartyEnterSettlement(
                Compact(request.SettlementId, typeof(Settlement)),
                Compact(request.PartyId, typeof(MobileParty)),
                AcceptedHeader(context.Header)));
            return new AuthorityServerReply<NetworkBreakInContinuationApproved>(
                CreateBreakInResult(context.Header, request, AuthorityResultStatus.Accepted, null), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Break-in continuation failed. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Settlement={SettlementId} AlreadyEntered={AlreadyEntered}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, request.SettlementId, alreadyEntered);
            if (mutationBoundaryCrossed)
                DisconnectAllConnectedPeers("ambiguous break-in continuation mutation", context);
            else
                DisconnectPeer(context.Peer, "failed break-in idempotent acknowledgement", context);

            return new AuthorityServerReply<NetworkBreakInContinuationApproved>(
                CreateBreakInResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, "break-in-isolated"),
                statePublished: false,
                suppressReply: true);
        }
    }

    private static AuthorityResultHeader AcceptedHeader(AuthorityRequestHeader header) =>
        new(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted, header.ExpectedRevision, null);

    private bool TryValidateAssault(
        MobileParty party,
        Settlement settlement,
        BesiegerCamp camp,
        out string reason)
    {
        reason = null;
        if (!party.IsActive || party.Party == null)
            reason = "party-inactive";
        else if (settlement.Party == null)
            reason = "settlement-unavailable";
        else if (camp == null || !ReferenceEquals(party.BesiegerCamp, camp))
            reason = "not-siege-participant";
        else if (!ReferenceEquals(camp.LeaderParty, party))
            reason = "not-siege-leader";
        else if (settlement.Party.MapEvent != null && !settlement.Party.MapEvent.IsSiegeAssault)
            reason = "invalid-battle-phase";
        else if (settlement.Party.MapEvent != null &&
            (!ReferenceEquals(party.MapEvent, settlement.Party.MapEvent) || party.Party.Side != BattleSideEnum.Attacker))
            reason = "assault-state-mismatch";
        else if (settlement.Party.MapEvent == null && !camp.IsPreparationComplete)
            reason = "preparation-incomplete";
        return reason == null;
    }

    private AuthorityServerReply<NetworkSiegeAssaultApproved> RejectAssault(
        AuthorityServerContext context,
        NetworkRequestSiegeAssault request,
        string reason)
    {
        network.Send(context.Peer, new SendInformationMessage($"Unable to start the siege assault: {GetAssaultFailureMessage(reason)}."));
        return new AuthorityServerReply<NetworkSiegeAssaultApproved>(
            CreateAssaultResult(context.Header, request, AuthorityResultStatus.Rejected, reason), statePublished: false);
    }

    private static NetworkSiegeAssaultApproved CreateAssaultTerminalResult(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reason) =>
        new(status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null, null);

    private static NetworkSiegeAssaultApproved CreateAssaultResult(
        AuthorityRequestHeader header,
        NetworkRequestSiegeAssault request,
        AuthorityResultStatus status,
        string reason) =>
        new(status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason),
            request.PartyId,
            request.SettlementId);

    private static bool IsExpectedAssaultResult(
        NetworkRequestSiegeAssault request,
        NetworkSiegeAssaultApproved result) =>
        result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private AuthorityServerReply<NetworkBreakInContinuationApproved> RejectBreakIn(
        AuthorityServerContext context,
        NetworkRequestBreakInContinuation request,
        string reason)
    {
        network.Send(context.Peer, new SendInformationMessage($"Unable to continue the siege break-in: {GetBreakInFailureMessage(reason)}."));
        return new AuthorityServerReply<NetworkBreakInContinuationApproved>(
            CreateBreakInResult(context.Header, request, AuthorityResultStatus.Rejected, reason), statePublished: false);
    }

    private static NetworkBreakInContinuationApproved CreateBreakInTerminalResult(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reason) =>
        new(null, null, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null);

    private static NetworkBreakInContinuationApproved CreateBreakInResult(
        AuthorityRequestHeader header,
        NetworkRequestBreakInContinuation request,
        AuthorityResultStatus status,
        string reason) =>
        new(request.RequestId, request.SettlementId, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), request.PartyId);

    private static bool IsExpectedBreakInResult(
        NetworkRequestBreakInContinuation request,
        NetworkBreakInContinuationApproved result) =>
        result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static string GetAssaultFailureMessage(string reason) => reason switch
    {
        "invalid-requester" => "your party is not controlled by you",
        "party-not-found" or "settlement-not-found" => "your party or settlement is no longer available",
        "party-inactive" => "your party is inactive",
        "settlement-unavailable" => "the settlement is unavailable",
        "not-siege-participant" => "your party is no longer in this siege",
        "not-siege-leader" => "only the siege leader can command the assault",
        "invalid-battle-phase" => "the siege is already in another battle phase",
        "assault-state-mismatch" => "the assault state is still synchronizing",
        "preparation-incomplete" => "siege preparations are not complete",
        _ => "the server could not apply the request",
    };

    private static string GetBreakInFailureMessage(string reason) => reason switch
    {
        "invalid-requester" => "your party is not controlled by you",
        "party-not-found" or "settlement-not-found" => "your party or settlement is no longer available",
        "cannot-continue-break-in" => "the siege state no longer permits it",
        _ => "the server could not apply the request",
    };

    private static void DisconnectPeer(NetPeer peer, string reason, AuthorityServerContext context)
    {
        try { peer?.Disconnect(); }
        catch (Exception exception)
        {
            Logger.Fatal(exception,
                "Could not disconnect peer after {Reason}. Route={Route} SessionId={SessionId} RequestId={RequestId}",
                reason, context.RouteId, context.Header.SessionId, context.Header.RequestId);
        }
    }

    private void DisconnectAllConnectedPeers(string reason, AuthorityServerContext context)
    {
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player) || !playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            DisconnectPeer(peer, reason, context);
        }
    }

    private AuthorityServerReply<NetworkBesiegeSettlementApproved> ExecuteBesiege(
        AuthorityServerContext context, NetworkRequestBesiegeSettlement request)
    {
        var decision = ExecuteEntry(context, request.PartyId, request.SettlementId, SiegeEntryAction.Besiege);
        return new AuthorityServerReply<NetworkBesiegeSettlementApproved>(
            new NetworkBesiegeSettlementApproved(decision.Status == AuthorityResultStatus.Accepted,
                new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId, decision.Status,
                    context.Header.ExpectedRevision, decision.ReasonCode), request.PartyId, request.SettlementId),
            decision.StatePublished, decision.SuppressReply);
    }

    private AuthorityServerReply<NetworkJoinSiegeCampApproved> ExecuteJoin(
        AuthorityServerContext context, NetworkRequestJoinSiegeCamp request)
    {
        var decision = ExecuteEntry(context, request.PartyId, request.SettlementId, SiegeEntryAction.Join);
        return new AuthorityServerReply<NetworkJoinSiegeCampApproved>(
            new NetworkJoinSiegeCampApproved(request.SettlementId, decision.Status == AuthorityResultStatus.Accepted,
                new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId, decision.Status,
                    context.Header.ExpectedRevision, decision.ReasonCode), request.PartyId),
            decision.StatePublished, decision.SuppressReply);
    }

    private SiegeEntryDecision ExecuteEntry(AuthorityServerContext context, string partyId, string settlementId,
        SiegeEntryAction action)
    {
        if (!string.Equals(context.Player.MobilePartyId, partyId, StringComparison.Ordinal))
            return RejectEntry(context, action, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return RejectEntry(context, action, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging<Settlement>(settlementId, out var settlement))
            return RejectEntry(context, action, "settlement-not-found");

        var targetCamp = settlement.SiegeEvent?.BesiegerCamp;
        if (targetCamp != null && ReferenceEquals(party.BesiegerCamp, targetCamp))
        {
            bool matchesRole = action == SiegeEntryAction.Join || ReferenceEquals(targetCamp.LeaderParty, party);
            return matchesRole ? SiegeEntryDecision.Accepted() : RejectEntry(context, action, "already-in-siege-camp");
        }

        if (!TryValidateEntry(party, settlement, action, out var rejectionReason))
        {
            Logger.Warning("Rejected {Action} entry for party {PartyId} at {SettlementId}: {Reason}",
                action, partyId, settlementId, rejectionReason);
            return RejectEntry(context, action, rejectionReason);
        }

        bool mutationStarted = false;
        string stage = "apply-siege-entry";
        try
        {
            mutationStarted = true;
            if (action == SiegeEntryAction.Besiege)
                siegeEventInterface.StartSiegeEvent(party, settlement);
            else
                siegeEventInterface.JoinSiegeCamp(party, settlement);

            // StartSiegeEvent/JoinSiegeCamp invoke the existing synchronized graph/camp writes.
            // This result is queued only after those writes have been issued on the reliable channel.
            var camp = settlement.SiegeEvent?.BesiegerCamp;
            if (camp == null || !ReferenceEquals(party.BesiegerCamp, camp) ||
                (action == SiegeEntryAction.Besiege && !ReferenceEquals(camp.LeaderParty, party)))
                throw new InvalidOperationException("Canonical siege graph was not established after entry mutation.");
            return SiegeEntryDecision.Accepted();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed {Action} entry for party {PartyId} at {SettlementId}. Stage={Stage}",
                action, partyId, settlementId, stage);
            if (!mutationStarted) return SiegeEntryDecision.Failed("siege-entry-failed");

            // A start/join can publish an irreversible partial graph. Isolate this requester rather
            // than sending an approval or a misleading rejection into a divergent local campaign.
            try { context.Peer.Disconnect(); }
            catch (Exception disconnectException) { Logger.Fatal(disconnectException, "Could not isolate siege-entry peer"); }
            return SiegeEntryDecision.Isolated("siege-entry-isolated");
        }
    }

    private SiegeEntryDecision RejectEntry(AuthorityServerContext context, SiegeEntryAction action, string reason)
    {
        network.Send(context.Peer, new SendInformationMessage($"Unable to {(action == SiegeEntryAction.Besiege ? "begin the siege" : "join the siege")}: {GetEntryFailureMessage(reason)}."));
        return SiegeEntryDecision.Reject(reason);
    }

    private bool TryValidateEntry(
        MobileParty party,
        Settlement settlement,
        SiegeEntryAction action,
        out string rejectionReason)
    {
        rejectionReason = null;

        if (!party.IsActive || party.Party == null)
            rejectionReason = "party-inactive";
        else if (settlement.Party == null || !settlement.IsFortification)
            rejectionReason = "not-fortification";
        else if (party.MapEvent != null)
            rejectionReason = "already-in-map-event";
        else if (party.CurrentSettlement != null && party.CurrentSettlement != settlement)
            rejectionReason = "inside-other-settlement";
        else if (party.BesiegerCamp != null)
            rejectionReason = "already-in-siege-camp";
        else if (!distanceValidator.TryValidate(party, settlement, out var distanceRejectionReason))
            rejectionReason = "too-far-from-settlement";
        else if ((party.ActualClan != null && party.ActualClan == settlement.OwnerClan) ||
            (party.MapFaction != null && party.MapFaction == settlement.MapFaction))
            rejectionReason = "defending-faction";
        else if (party.MapFaction == null ||
            settlement.MapFaction == null ||
            !FactionManager.IsAtWarAgainstFaction(party.MapFaction, settlement.MapFaction))
            rejectionReason = "not-at-war";
        else if (action == SiegeEntryAction.Besiege &&
            (settlement.SiegeEvent != null || party.Party.NumberOfHealthyMembers <= 0))
            rejectionReason = "cannot-begin-siege";
        else if (action == SiegeEntryAction.Join &&
            (settlement.SiegeEvent == null ||
            !settlement.SiegeEvent.CanPartyJoinSide(party.Party, BattleSideEnum.Attacker)))
            rejectionReason = "cannot-join-attacking-side";

        return rejectionReason == null;
    }

    private static string GetEntryFailureMessage(string reason) => reason switch
    {
        "invalid-requester" => "your party is not controlled by you",
        "party-not-found" or "settlement-not-found" => "your party or the settlement is no longer available",
        "party-inactive" => "your party is inactive",
        "not-fortification" => "the target is not a fortification",
        "already-in-map-event" => "your party is already in a map event",
        "inside-other-settlement" => "your party is inside another settlement",
        "already-in-siege-camp" => "your party is already in another siege camp",
        "too-far-from-settlement" => "your party is too far from the settlement",
        "defending-faction" => "your party belongs to the defending faction",
        "not-at-war" => "your party is not at war with the settlement",
        "cannot-begin-siege" => "your party cannot begin this siege",
        "cannot-join-attacking-side" => "your party cannot join the attacking side",
        _ => "the server could not apply the request",
    };

    private static NetworkBesiegeSettlementApproved CreateBesiegeTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null, null);

    private static NetworkJoinSiegeCampApproved CreateJoinTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null);

    private static bool IsExpectedBesiegeResult(NetworkRequestBesiegeSettlement request,
        NetworkBesiegeSettlementApproved result) =>
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static bool IsExpectedJoinResult(NetworkRequestJoinSiegeCamp request,
        NetworkJoinSiegeCampApproved result) =>
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static NetworkBreakSiegeApproved CreateBreakTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(SiegeBreakOutcome.Rejected, false, false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static bool IsExpectedBreakResult(NetworkRequestBreakSiege request,
        NetworkBreakSiegeApproved result) =>
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        request.FinishLocalMenus == result.FinishLocalMenus;

    private enum SiegeEntryAction
    {
        Besiege,
        Join,
    }

    private readonly struct SiegeEntryIntent
    {
        public SiegeEntryIntent(string partyId, string settlementId)
        {
            PartyId = partyId;
            SettlementId = settlementId;
        }

        public string PartyId { get; }
        public string SettlementId { get; }
    }

    private readonly struct SiegeBreakIntent
    {
        public SiegeBreakIntent(string partyId, bool finishLocalMenus)
        {
            PartyId = partyId;
            FinishLocalMenus = finishLocalMenus;
        }

        public string PartyId { get; }
        public bool FinishLocalMenus { get; }
    }

    private readonly struct SiegeEntryDecision
    {
        private SiegeEntryDecision(AuthorityResultStatus status, string reasonCode, bool statePublished, bool suppressReply)
        {
            Status = status;
            ReasonCode = reasonCode;
            StatePublished = statePublished;
            SuppressReply = suppressReply;
        }

        public AuthorityResultStatus Status { get; }
        public string ReasonCode { get; }
        public bool StatePublished { get; }
        public bool SuppressReply { get; }
        public static SiegeEntryDecision Accepted() => new(AuthorityResultStatus.Accepted, null, true, false);
        public static SiegeEntryDecision Reject(string reason) => new(AuthorityResultStatus.Rejected, reason, false, false);
        public static SiegeEntryDecision Failed(string reason) => new(AuthorityResultStatus.ExecutionFailed, reason, false, false);
        public static SiegeEntryDecision Isolated(string reason) => new(AuthorityResultStatus.ExecutionFailed, reason, false, true);
    }

    private AuthorityServerReply<NetworkBreakSiegeApproved> ExecuteBreak(
        AuthorityServerContext context,
        NetworkRequestBreakSiege request)
    {
        if (!string.Equals(context.Player.MobilePartyId, request.PartyId, StringComparison.Ordinal))
            return RejectBreak(context, request, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return RejectBreak(context, request, "party-not-found");
        if (!party.IsActive || party.Party == null)
            return RejectBreak(context, request, "party-inactive");

        bool mutationStarted = false;
        string stage = "validate-siege-break";
        try
        {
            if (party.MapEvent?.IsSiegeAssault == true &&
                party.Party.Side == BattleSideEnum.Attacker)
            {
                stage = "publish-battle-leave";
                mutationStarted = true;
                messageBroker.Publish(
                    party,
                    new PlayerLeaveBattleAttempted(party.Party, request.FinishLocalMenus));
                if (party.MapEvent != null || party.BesiegerCamp != null)
                    throw new InvalidOperationException("Siege assault leave did not remove the party from its battle and camp.");

                return AcceptBreak(context, request, battleLeaveApplied: true, siegeContinues: false);
            }

            if (party.MapEvent != null)
                return RejectBreak(context, request, "invalid-battle-phase");
            if (party.BesiegerCamp == null)
                return RejectBreak(context, request, "not-siege-participant");

            var camp = party.BesiegerCamp;
            stage = "remove-party-from-siege-camp";
            mutationStarted = true;
            // This intentionally isolates only the requester. In particular, an army leader's
            // attached AI/player parties must retain the siege graph for "leave it to the others".
            siegeEventInterface.BreakSiegeForPartyOnly(party);
            if (party.BesiegerCamp != null)
                throw new InvalidOperationException("Siege camp membership remained after party-only leave.");

            bool siegeContinues = camp.SiegeEvent?.BesiegerCamp?.LeaderParty != null;
            return AcceptBreak(context, request, battleLeaveApplied: false, siegeContinues);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Siege break failed. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Stage={Stage}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, stage);
            if (!mutationStarted)
                return FailedBreak(context, request, "siege-break-failed");

            // The native setters can publish graph changes before an exception. Do not report an
            // ambiguous departure to a client whose replica could now be divergent.
            try { context.Peer.Disconnect(); }
            catch (Exception disconnectException) { Logger.Fatal(disconnectException, "Could not isolate siege-break peer"); }
            return new AuthorityServerReply<NetworkBreakSiegeApproved>(
                CreateBreakResult(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                    "siege-break-isolated", SiegeBreakOutcome.Rejected, false, false),
                statePublished: false,
                suppressReply: true);
        }
    }

    private static AuthorityServerReply<NetworkBreakSiegeApproved> AcceptBreak(
        AuthorityServerContext context,
        NetworkRequestBreakSiege request,
        bool battleLeaveApplied,
        bool siegeContinues) =>
        new(CreateBreakResult(context.Header, request, AuthorityResultStatus.Accepted, null,
                SiegeBreakOutcome.Applied, battleLeaveApplied, siegeContinues), statePublished: true);

    private static AuthorityServerReply<NetworkBreakSiegeApproved> FailedBreak(
        AuthorityServerContext context,
        NetworkRequestBreakSiege request,
        string reason) =>
        new(CreateBreakResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, reason,
            SiegeBreakOutcome.Rejected, false, false), statePublished: false);

    private AuthorityServerReply<NetworkBreakSiegeApproved> RejectBreak(
        AuthorityServerContext context,
        NetworkRequestBreakSiege request,
        string reason)
    {
        network.Send(context.Peer, new SendInformationMessage($"Unable to leave the siege: {GetBreakFailureMessage(reason)}."));
        return new AuthorityServerReply<NetworkBreakSiegeApproved>(
            CreateBreakResult(context.Header, request, AuthorityResultStatus.Rejected, reason,
                SiegeBreakOutcome.Rejected, false, false), statePublished: false);
    }

    private static NetworkBreakSiegeApproved CreateBreakResult(
        AuthorityRequestHeader header,
        NetworkRequestBreakSiege request,
        AuthorityResultStatus status,
        string reason,
        SiegeBreakOutcome outcome,
        bool battleLeaveApplied,
        bool siegeContinues) =>
        new(outcome, request.FinishLocalMenus, battleLeaveApplied,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason),
            request.PartyId, siegeContinues);

    private static string GetBreakFailureMessage(string reason) => reason switch
    {
        "invalid-requester" => "your party is not controlled by you",
        "party-not-found" => "your party is no longer available",
        "party-inactive" => "your party is inactive",
        "invalid-battle-phase" => "your party cannot leave this battle through the siege menu",
        "not-siege-participant" => "your party is no longer participating in this siege",
        _ => "the server could not apply the request",
    };

    public void Dispose()
    {
        besiegeRoute.Dispose();
        joinRoute.Dispose();
        breakRoute.Dispose();
        assaultRoute.Dispose();
        breakInRoute.Dispose();
        messageBroker.Unsubscribe<SiegeAssaultStarted>(HandleAssaultStarted);
        messageBroker.Unsubscribe<SiegePreparationStarted>(HandlePreparationStarted);
        messageBroker.Unsubscribe<SiegeEndedWithoutBattle>(HandleSiegeEnded);
        messageBroker.Unsubscribe<SiegeCampPositionRolled>(HandleCampPosition);
    }
}
