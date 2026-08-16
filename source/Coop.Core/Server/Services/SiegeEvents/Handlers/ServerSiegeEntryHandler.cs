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
        messageBroker.Subscribe<NetworkRequestBreakSiege>(HandleBreak);
        messageBroker.Subscribe<NetworkRequestSiegeAssault>(HandleAssault);
        messageBroker.Subscribe<SiegeAssaultStarted>(HandleAssaultStarted);
        messageBroker.Subscribe<SiegePreparationStarted>(HandlePreparationStarted);
        messageBroker.Subscribe<SiegeEndedWithoutBattle>(HandleSiegeEnded);
        messageBroker.Subscribe<SiegeCampPositionRolled>(HandleCampPosition);
        messageBroker.Subscribe<NetworkRequestBreakInContinuation>(HandleBreakInContinuation);
    }

    private void HandleBreakInContinuation(MessagePayload<NetworkRequestBreakInContinuation> payload)
    {
        var obj = payload.What;
        if (!(payload.Who is NetPeer peer))
        {
            Logger.Error("Received {Message} with no originating peer", nameof(NetworkRequestBreakInContinuation));
            return;
        }

        GameThread.RunSafe(() =>
        {
            bool approved;
            try
            {
                approved = TryApplyBreakInContinuation(peer, obj);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply break-in continuation request {RequestId}", obj.RequestId);
                approved = false;
            }

            SendBreakInContinuationResult(peer, obj, approved);
        }, context: nameof(HandleBreakInContinuation));
    }

    private bool TryApplyBreakInContinuation(
        NetPeer peer,
        NetworkRequestBreakInContinuation request)
    {
        if (!DoesPeerControlParty(peer, request)) return false;

        if (!objectManager.TryGetObjectWithLogging<MobileParty>(request.PartyId, out var party) ||
            !objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            return false;

        var alreadyEntered = ReferenceEquals(party.CurrentSettlement, settlement);
        if (!CanApplyBreakInContinuation(party, settlement, alreadyEntered))
        {
            Logger.Warning("Rejecting break-in request {RequestId} for party {PartyId} and settlement {SettlementId}",
                request.RequestId, request.PartyId, request.SettlementId);
            return false;
        }

        if (alreadyEntered)
        {
            network.Send(peer, new NetworkPartyEnterSettlement(
                Compact(request.SettlementId, typeof(Settlement)),
                Compact(request.PartyId, typeof(MobileParty))));
        }
        else
        {
            settlementInterface.PartyEnterSettlement(party, settlement);
        }

        return ReferenceEquals(party.CurrentSettlement, settlement);
    }

    private bool DoesPeerControlParty(
        NetPeer peer,
        NetworkRequestBreakInContinuation request)
    {
        if (playerManager.TryGetPlayer(peer, out var player) &&
            player.MobilePartyId == request.PartyId)
            return true;

        Logger.Warning("Rejecting break-in request {RequestId} from a peer that does not control party {PartyId}",
            request.RequestId, request.PartyId);
        return false;
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

    private void SendBreakInContinuationResult(
        NetPeer peer,
        NetworkRequestBreakInContinuation request,
        bool approved)
    {
        network.Send(peer, new NetworkBreakInContinuationApproved(
            request.RequestId,
            request.SettlementId,
            approved));
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

    private void HandleAssault(MessagePayload<NetworkRequestSiegeAssault> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.PartyId, out _)) return;
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;

            var camp = settlement.SiegeEvent?.BesiegerCamp;
            if (camp == null)
            {
                Logger.Error("Party {PartyId} tried to assault {SettlementId} which is not under siege", obj.PartyId, obj.SettlementId);
                return;
            }

            // Create the assault authoritatively with patches LIVE so the map event registers + replicates and
            // SiegeAssaultPromptPatches fires SiegeAssaultStarted (broadcasting the attacker/defender prompts). The
            // camp leader is the authoritative attacker, matching vanilla lead_assault_on_consequence.
            if (settlement.Party.MapEvent == null)
            {
                // SiegeEntryFlowPatches only reroutes the assault menu consequence, so the vanilla
                // preparation-complete on_condition is bypassed; enforce it authoritatively here.
                if (!camp.IsPreparationComplete)
                {
                    Logger.Warning("Party {PartyId} tried to assault {SettlementId} before siege preparations completed", obj.PartyId, obj.SettlementId);
                    return;
                }

                StartBattleAction.ApplyStartAssaultAgainstWalls(camp.LeaderParty, settlement);
                return;
            }

            // Assault already live (e.g. a repeat click): re-broadcast the prompt so a besieger still catching up enters it.
            if (settlement.Party.MapEvent.IsSiegeAssault && objectManager.TryGetId(camp.LeaderParty, out var leaderId))
            {
                network.SendAll(new NetworkPromptSiegeAssault(leaderId, obj.SettlementId));
            }
        });
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

    private static string ValidateEntryIdentifiers(string partyId, string settlementId) =>
        string.IsNullOrWhiteSpace(partyId) || partyId.Length > 256 ||
        string.IsNullOrWhiteSpace(settlementId) || settlementId.Length > 256
            ? "invalid-siege-entry-identifiers" : null;

    private static string BuildBesiegeCommandKey(NetworkRequestBesiegeSettlement request) =>
        BuildEntryCommandKey(request.PartyId, request.SettlementId);

    private static string BuildJoinCommandKey(NetworkRequestJoinSiegeCamp request) =>
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

    private void HandleBreak(MessagePayload<NetworkRequestBreakSiege> payload)
    {
        var obj = payload.What;
        var peer = (NetPeer)payload.Who;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.PartyId, out var party)) return;

            if (party.MapEvent?.IsSiegeAssault == true &&
                party.Party.Side == BattleSideEnum.Attacker)
            {
                messageBroker.Publish(
                    party,
                    new PlayerLeaveBattleAttempted(party.Party, obj.FinishLocalMenus));
                network.Send(peer, new NetworkBreakSiegeApproved(
                    SiegeBreakOutcome.Applied,
                    obj.FinishLocalMenus,
                    battleLeaveApplied: true));
                return;
            }

            if (party.BesiegerCamp == null)
            {
                Logger.Information("Party {PartyId} already left its siege camp", obj.PartyId);
                network.Send(peer, new NetworkBreakSiegeApproved(
                    SiegeBreakOutcome.AlreadyLeft,
                    obj.FinishLocalMenus));
                return;
            }

            siegeEventInterface.BreakSiege(party);

            network.Send(peer, new NetworkBreakSiegeApproved(
                SiegeBreakOutcome.Applied,
                obj.FinishLocalMenus));
        });
    }

    public void Dispose()
    {
        besiegeRoute.Dispose();
        joinRoute.Dispose();
        messageBroker.Unsubscribe<NetworkRequestBreakSiege>(HandleBreak);
        messageBroker.Unsubscribe<NetworkRequestSiegeAssault>(HandleAssault);
        messageBroker.Unsubscribe<SiegeAssaultStarted>(HandleAssaultStarted);
        messageBroker.Unsubscribe<SiegePreparationStarted>(HandlePreparationStarted);
        messageBroker.Unsubscribe<SiegeEndedWithoutBattle>(HandleSiegeEnded);
        messageBroker.Unsubscribe<SiegeCampPositionRolled>(HandleCampPosition);
        messageBroker.Unsubscribe<NetworkRequestBreakInContinuation>(HandleBreakInContinuation);
    }
}
