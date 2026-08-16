using Common.Messaging;
using Common.Network;
using Common;
using Common.Logging;
using Common.Util;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Server.Services.Kingdoms.Messages;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using static GameInterface.Services.ObjectManager.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using Serilog;

namespace Coop.Core.Server.Services.Kingdoms.Handlers;

/// <summary>
/// Handles network related data for Kingdoms
/// </summary>
public class ServerKingdomHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerKingdomHandler>();
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IKingdomCreationSettlementTracker settlementTracker;
    private readonly IKingdomDecisionDataConverter kingdomDecisionDataConverter;
    private readonly IKingdomCreator kingdomCreator;
    private readonly IKingdomDecisionVoteManager decisionVoteManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<CreateKingdomIntent, NetworkCreateKingdomResult> createRoute;
    private readonly IAuthorityRouteHandle<RenameKingdomIntent, NetworkKingdomRenameResult> renameRoute;
    private readonly IAuthorityRouteHandle<VoteIntent, NetworkKingdomDecisionVoteResult> voteRoute;
    private readonly Dictionary<string, PendingSettlementRestore> pendingKingdomCreationSettlements = new();
    private ActiveAuthorityMutation activeMutation;
    private NetworkPlayerKingdomCreated capturedCreatePublication;
    private NetworkKingdomNameChanged capturedRenamePublication;
    private NetworkChangeKingdomDecisionVote capturedVotePublication;
    private NetworkKingdomDecisionResolved capturedResolutionPublication;
    private NetworkRemoveDecision capturedDecisionRemoval;

    public ServerKingdomHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IKingdomCreationSettlementTracker settlementTracker,
        IKingdomDecisionDataConverter kingdomDecisionDataConverter,
        IKingdomCreator kingdomCreator,
        IKingdomDecisionVoteManager decisionVoteManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.settlementTracker = settlementTracker;
        this.kingdomDecisionDataConverter = kingdomDecisionDataConverter;
        this.kingdomCreator = kingdomCreator;
        this.decisionVoteManager = decisionVoteManager;
        this.configAuthority = configAuthority;
        createRoute = authorityRequestRouter.Register(
            AuthorityRoute<CreateKingdomIntent, NetworkRequestCreateKingdom, NetworkCreateKingdomResult>.Define(
                "kingdom.create", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestCreateKingdom(intent.ControllerId, intent.KingdomName,
                    intent.CultureId, intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateCreateWireShape, BuildCreateCommandKey,
                ValidateHeader, ExecuteCreate, CreateCreateTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedCreateResult));
        renameRoute = authorityRequestRouter.Register(
            AuthorityRoute<RenameKingdomIntent, NetworkRequestChangeKingdomName, NetworkKingdomRenameResult>.Define(
                "kingdom.rename", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestChangeKingdomName(intent.KingdomId, intent.Name, header),
                request => request.Header, result => result.Header, ValidateRenameWireShape, BuildRenameCommandKey,
                ValidateHeader, ExecuteRename, CreateRenameTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedRenameResult));
        voteRoute = authorityRequestRouter.Register(
            AuthorityRoute<VoteIntent, NetworkRequestKingdomDecisionVote, NetworkKingdomDecisionVoteResult>.Define(
                "kingdom.decision.vote", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestKingdomDecisionVote(intent.ControllerId, intent.VoteData, header),
                request => request.Header, result => result.Header, ValidateVoteWireShape, BuildVoteCommandKey,
                ValidateHeader, ExecuteVote, CreateVoteTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedVoteResult));
        messageBroker.Subscribe<DecisionAdded>(HandleLocalDecisionAdded);
        messageBroker.Subscribe<DecisionRemoved>(HandleLocalDecisionRemoved);
        messageBroker.Subscribe<KingdomPolicyChanged>(HandleLocalKingdomPolicyChanged);
        messageBroker.Subscribe<KingdomDecisionVoteChanged>(HandleLocalKingdomDecisionVoteChanged);
        messageBroker.Subscribe<KingdomDecisionResolved>(HandleLocalKingdomDecisionResolved);
        messageBroker.Subscribe<PlayerKingdomCreated>(HandleLocalPlayerKingdomCreated);
        messageBroker.Subscribe<NetworkAddDecision>(HandleNetworkAddDecision);
        messageBroker.Subscribe<KingdomNameChanged>(HandleLocalKingdomNameChanged);
    }

    private void HandleLocalPlayerKingdomCreated(MessagePayload<PlayerKingdomCreated> obj)
    {
        var payload = obj.What;

        TryGetPlayerSettlementContext(payload.ControllerId, out var partyId, out var settlementId);

        if ((string.IsNullOrWhiteSpace(partyId) || string.IsNullOrWhiteSpace(settlementId)) &&
            pendingKingdomCreationSettlements.TryGetValue(payload.ControllerId, out var pending))
        {
            partyId = pending.PartyId;
            settlementId = pending.SettlementId;
        }

        RunSettlementMutation(() =>
        {
            RestoreCreatingPartySettlement(partyId, settlementId);
            settlementTracker.Complete(partyId);
            pendingKingdomCreationSettlements.Remove(payload.ControllerId);
        });

        var message = new NetworkPlayerKingdomCreated(
            payload.ControllerId,
            payload.KingdomId,
            payload.KingdomName,
            payload.ClanId,
            partyId,
            settlementId,
            payload.CultureId,
            IsActive("kingdom.create", payload.ControllerId) ? activeMutation.Header : default);
        if (IsActive("kingdom.create", payload.ControllerId)) capturedCreatePublication = message;
        else network.SendAll(message);
    }

    private void HandleLocalKingdomNameChanged(MessagePayload<KingdomNameChanged> obj)
    {
        var payload = obj.What;
        
        objectManager.TryGetObject<Kingdom>(payload.KingdomId, out var kingdom);
        string requestedName = IsActive("kingdom.rename", payload.ControllerId) ? activeMutation.RequestedName : null;
        var message = new NetworkKingdomNameChanged(
            payload.KingdomId,
            requestedName,
            kingdom?.Name?.ToString(),
            kingdom?.InformalName?.ToString(),
            IsActive("kingdom.rename", payload.ControllerId) ? payload.ControllerId : null,
            IsActive("kingdom.rename", payload.ControllerId) ? activeMutation.Header : default);
        if (IsActive("kingdom.rename", payload.ControllerId)) capturedRenamePublication = message;
        else network.SendAll(message);
    }

    private static bool TryCreatePendingSettlementRestore(
        string partyId,
        string settlementId,
        out PendingSettlementRestore pending)
    {
        pending = default;
        if (string.IsNullOrWhiteSpace(partyId) || string.IsNullOrWhiteSpace(settlementId)) return false;

        pending = new PendingSettlementRestore(partyId, settlementId);
        return true;
    }

    private void RestoreCreatingPartySettlement(string partyId, string settlementId)
    {
        if (string.IsNullOrWhiteSpace(partyId) || string.IsNullOrWhiteSpace(settlementId)) return;
        if (!objectManager.TryGetObject<MobileParty>(partyId, out var party)) return;
        if (!TryGetSettlement(settlementId, out var settlement)) return;
        settlementTracker.TrackParty(party, partyId, settlement, settlementId);
        if (party.CurrentSettlement == settlement) return;

        RunSettlementMutation(() =>
        {
            using (new AllowedThread())
            {
                try
                {
                    party.CurrentSettlement = settlement;
                }
                catch (NullReferenceException)
                {
                    party.SetCurrentSettlementDirectly(settlement);
                }
            }
        });

        network.SendAll(new NetworkPartyEnterSettlement(
            Compact(settlementId, typeof(Settlement)),
            Compact(partyId, typeof(MobileParty))));
    }

    private static void RunSettlementMutation(Action action)
    {
        if (!GameThread.Instance.IsInitialized)
        {
            action();
            return;
        }

        GameThread.RunSafe(action, blocking: true, context: nameof(ServerKingdomHandler));
    }

    private bool TryGetSettlement(string settlementId, out Settlement settlement)
    {
        return objectManager.TryGetObjectWithLogging(settlementId, out settlement);
    }

    private bool TryGetPlayerSettlementContext(
        string controllerId,
        out string partyId,
        out string settlementId)
    {
        partyId = null;
        settlementId = null;

        if (!playerManager.TryGetPlayer(controllerId, out var player)) return false;
        if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party)) return false;
        partyId = player.MobilePartyId;
        if (party.CurrentSettlement == null) return false;
        if (!TryGetSettlementId(party.CurrentSettlement, out settlementId)) return false;
        return true;
    }

    private bool TryGetSettlementId(Settlement settlement, out string settlementId)
    {
        return objectManager.TryGetIdWithLogging(settlement, out settlementId);
    }

    private void HandleLocalKingdomDecisionResolved(MessagePayload<KingdomDecisionResolved> obj)
    {
        var payload = obj.What;
        bool correlated = activeMutation != null &&
            string.Equals(activeMutation.RouteId, "kingdom.decision.vote", StringComparison.Ordinal);

        var message = new NetworkKingdomDecisionResolved(
            payload.KingdomId,
            payload.DecisionIndex,
            payload.OutcomeIndex,
            payload.IsPlayerDecision,
            payload.OutcomeKey,
            payload.NotificationText,
            correlated ? activeMutation.ControllerId : null,
            correlated ? activeMutation.Header : default);
        if (correlated) capturedResolutionPublication = message;
        else network.SendAll(message);
    }

    private void HandleLocalKingdomDecisionVoteChanged(MessagePayload<KingdomDecisionVoteChanged> obj)
    {
        var payload = obj.What;
        bool correlated = activeMutation != null &&
            string.Equals(activeMutation.RouteId, "kingdom.decision.vote", StringComparison.Ordinal);

        var message = new NetworkChangeKingdomDecisionVote(
            payload.ClanId,
            payload.VoteData,
            correlated ? activeMutation.ControllerId : null,
            correlated ? activeMutation.Header : default);
        if (correlated) capturedVotePublication = message;
        else network.SendAll(message);
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

    private AuthorityServerReply<NetworkCreateKingdomResult> ExecuteCreate(
        AuthorityServerContext context, NetworkRequestCreateKingdom request)
    {
        if (!string.Equals(request.ControllerId, context.Player.ControllerId, StringComparison.Ordinal))
            return RejectCreate(context.Header, "invalid-requester");
        if (!objectManager.TryGetObject<Clan>(context.Player.ClanId, out var clan))
            return RejectCreate(context.Header, "clan-not-found");
        if (clan.Kingdom != null) return RejectCreate(context.Header, "kingdom-conflict");
        if (clan.Culture == null || !objectManager.TryGetId(clan.Culture, out string cultureId) ||
            !string.Equals(cultureId, request.CultureId, StringComparison.Ordinal))
            return RejectCreate(context.Header, "stale-culture");

        string kingdomName = request.KingdomName.Trim();
        if (!IsNameAvailable(null, kingdomName)) return RejectCreate(context.Header, "kingdom-name-unavailable");
        string partyId = context.Player.MobilePartyId;
        if (!string.Equals(request.PartyId, partyId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject<MobileParty>(partyId, out var party))
            return RejectCreate(context.Header, "invalid-party-context");

        Settlement canonicalSettlement = party.CurrentSettlement;
        if (canonicalSettlement == null) settlementTracker.TryGetTrackedSettlement(party, out canonicalSettlement);
        string settlementId = null;
        if (canonicalSettlement != null && !objectManager.TryGetId(canonicalSettlement, out settlementId))
            return RejectCreate(context.Header, "settlement-not-registered");
        if (!string.Equals(settlementId, request.SettlementId, StringComparison.Ordinal))
        {
            return RejectCreate(context.Header, "stale-settlement-context");
        }

        activeMutation = new ActiveAuthorityMutation(
            "kingdom.create", context.Header, context.Player.ControllerId, kingdomName, null, -1);
        capturedCreatePublication = null;
        bool mutationBoundaryCrossed = false;
        try
        {
            if (canonicalSettlement != null && !string.IsNullOrWhiteSpace(settlementId))
            {
                mutationBoundaryCrossed = true;
                pendingKingdomCreationSettlements[context.Player.ControllerId] =
                    new PendingSettlementRestore(partyId, settlementId);
                settlementTracker.TrackParty(party, partyId, canonicalSettlement, settlementId);
                RestoreCreatingPartySettlement(partyId, settlementId);
            }

            mutationBoundaryCrossed = true;
            if (!kingdomCreator.TryCreateKingdom(
                    clan, kingdomName, clan.Culture, context.Player.ControllerId, out string kingdomId, out string createError,
                    allowCoopFallback: false))
            {
                if (clan.Kingdom != null || string.Equals(createError, "native kingdom creation failed", StringComparison.Ordinal))
                    return IsolatedCreate(context, "ambiguous-kingdom-create");
                return RejectCreate(context.Header, "kingdom-create-refused");
            }

            if (!objectManager.TryGetObject<Kingdom>(kingdomId, out var kingdom) ||
                !ReferenceEquals(kingdom.RulingClan, clan) || !ReferenceEquals(clan.Kingdom, kingdom) ||
                !ReferenceEquals(kingdom.Culture, clan.Culture) || kingdom.Clans?.Contains(clan) != true ||
                !string.Equals(kingdom.Name?.ToString(), kingdomName, StringComparison.Ordinal) ||
                !string.Equals(kingdom.InformalName?.ToString(), kingdomName, StringComparison.Ordinal) ||
                capturedCreatePublication == null ||
                !string.Equals(capturedCreatePublication.KingdomId, kingdomId, StringComparison.Ordinal) ||
                !string.Equals(capturedCreatePublication.PartyId, partyId, StringComparison.Ordinal) ||
                !string.Equals(capturedCreatePublication.SettlementId, settlementId, StringComparison.Ordinal))
                return IsolatedCreate(context, "invalid-kingdom-create-commit");

            network.SendAll(capturedCreatePublication);
            return new AuthorityServerReply<NetworkCreateKingdomResult>(
                new NetworkCreateKingdomResult(AcceptedHeader(context.Header), kingdomId, kingdomName,
                    context.Player.ClanId, cultureId, partyId, settlementId, context.Player.ControllerId),
                statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Kingdom creation failed after authority boundary for {ControllerId}",
                context.Player.ControllerId);
            if (mutationBoundaryCrossed) return IsolatedCreate(context, "kingdom-create-isolated");
            return RejectCreate(context.Header, "kingdom-create-failed");
        }
        finally { ClearActiveMutation(); }
    }

    private AuthorityServerReply<NetworkKingdomRenameResult> ExecuteRename(
        AuthorityServerContext context, NetworkRequestChangeKingdomName request)
    {
        if (!objectManager.TryGetObject<Clan>(context.Player.ClanId, out var clan) || clan.Kingdom == null)
            return RejectRename(context.Header, "kingdom-not-found");
        Kingdom kingdom = clan.Kingdom;
        if (!objectManager.TryGetId(kingdom, out string kingdomId) ||
            !string.Equals(kingdomId, request.KingdomId, StringComparison.Ordinal))
            return RejectRename(context.Header, "stale-kingdom");
        if (!ReferenceEquals(kingdom.RulingClan, clan))
            return RejectRename(context.Header, "not-ruling-clan");
        string requestedName = request.Name.Trim();
        if (!IsNameAvailable(kingdom, requestedName))
            return RejectRename(context.Header, "kingdom-name-unavailable");

        activeMutation = new ActiveAuthorityMutation(
            "kingdom.rename", context.Header, context.Player.ControllerId, requestedName, kingdomId, -1);
        capturedRenamePublication = null;
        bool mutationBoundaryCrossed = false;
        try
        {
            var rawName = new TextObject(requestedName);
            var fullName = GameTexts.FindText("str_generic_kingdom_name", null);
            fullName.SetTextVariable("KINGDOM_NAME", rawName);
            var informalName = GameTexts.FindText("str_generic_kingdom_short_name", null);
            informalName.SetTextVariable("KINGDOM_SHORT_NAME", rawName);

            mutationBoundaryCrossed = true;
            kingdom.ChangeKingdomName(fullName, informalName);
            messageBroker.Publish(this, new KingdomNameChanged(context.Player.ControllerId, kingdomId));

            string effectiveFullName = kingdom.Name?.ToString();
            string effectiveInformalName = kingdom.InformalName?.ToString();
            if (!string.Equals(effectiveFullName, fullName.ToString(), StringComparison.Ordinal) ||
                !string.Equals(effectiveInformalName, informalName.ToString(), StringComparison.Ordinal) ||
                capturedRenamePublication == null)
                return IsolatedRename(context, "invalid-kingdom-rename-commit");

            network.SendAll(capturedRenamePublication);
            return new AuthorityServerReply<NetworkKingdomRenameResult>(
                new NetworkKingdomRenameResult(AcceptedHeader(context.Header), kingdomId, requestedName,
                    effectiveFullName, effectiveInformalName, context.Player.ControllerId), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Kingdom rename failed after authority boundary for {ControllerId}",
                context.Player.ControllerId);
            if (mutationBoundaryCrossed) return IsolatedRename(context, "kingdom-rename-isolated");
            return RejectRename(context.Header, "kingdom-rename-failed");
        }
        finally { ClearActiveMutation(); }
    }

    private AuthorityServerReply<NetworkKingdomDecisionVoteResult> ExecuteVote(
        AuthorityServerContext context, NetworkRequestKingdomDecisionVote request)
    {
        if (!string.Equals(request.ControllerId, context.Player.ControllerId, StringComparison.Ordinal))
            return RejectVote(context.Header, "invalid-requester");
        if (!objectManager.TryGetObject<Clan>(context.Player.ClanId, out var clan) || clan.Kingdom == null)
            return RejectVote(context.Header, "kingdom-not-found");
        if (!objectManager.TryGetId(clan.Kingdom, out string kingdomId) ||
            !string.Equals(kingdomId, request.VoteData.KingdomId, StringComparison.Ordinal))
            return RejectVote(context.Header, "stale-kingdom");
        if (clan.Kingdom.UnresolvedDecisions == null || request.VoteData.DecisionIndex < 0 ||
            request.VoteData.DecisionIndex >= clan.Kingdom.UnresolvedDecisions.Count)
            return RejectVote(context.Header, "stale-decision");
        if (!Enum.IsDefined(typeof(Supporter.SupportWeights), request.VoteData.SupportWeight))
            return RejectVote(context.Header, "invalid-support-weight");

        activeMutation = new ActiveAuthorityMutation(
            "kingdom.decision.vote", context.Header, context.Player.ControllerId, null,
            kingdomId, request.VoteData.DecisionIndex);
        capturedVotePublication = null;
        capturedResolutionPublication = null;
        capturedDecisionRemoval = null;
        bool mutationBoundaryCrossed = false;
        try
        {
            // HandleVoteRequest resolves all identity, eligibility and outcome references before its
            // support reset. From this call onward any throw or publication gap is ambiguous.
            mutationBoundaryCrossed = true;
            if (!decisionVoteManager.HandleVoteRequest(context.Player.ControllerId, request.VoteData))
                return RejectVote(context.Header, "kingdom-vote-refused");
            if (capturedVotePublication == null)
                return IsolatedVote(context, "missing-kingdom-vote-publication");

            bool resolved = capturedResolutionPublication != null;
            if (resolved && capturedDecisionRemoval == null)
                return IsolatedVote(context, "missing-kingdom-resolution-removal");

            network.SendAll(capturedVotePublication);
            if (resolved)
            {
                network.SendAll(capturedResolutionPublication);
                network.SendAll(capturedDecisionRemoval);
            }

            return new AuthorityServerReply<NetworkKingdomDecisionVoteResult>(
                new NetworkKingdomDecisionVoteResult(AcceptedHeader(context.Header),
                    capturedVotePublication.ClanId, capturedVotePublication.VoteData, resolved,
                    capturedResolutionPublication?.OutcomeIndex ?? -1,
                    capturedResolutionPublication?.OutcomeKey,
                    context.Player.ControllerId), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Kingdom vote failed after authority boundary for {ControllerId}",
                context.Player.ControllerId);
            if (mutationBoundaryCrossed) return IsolatedVote(context, "kingdom-vote-isolated");
            return RejectVote(context.Header, "kingdom-vote-failed");
        }
        finally { ClearActiveMutation(); }
    }

    private bool IsNameAvailable(Kingdom current, string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return false;
        return Kingdom.All?.Any(other => !ReferenceEquals(other, current) &&
            (string.Equals(other?.Name?.ToString(), requestedName, StringComparison.InvariantCultureIgnoreCase) ||
             string.Equals(other?.InformalName?.ToString(), requestedName, StringComparison.InvariantCultureIgnoreCase))) != true;
    }

    private bool IsActive(string routeId, string controllerId) => activeMutation != null &&
        string.Equals(activeMutation.RouteId, routeId, StringComparison.Ordinal) &&
        string.Equals(activeMutation.ControllerId, controllerId, StringComparison.Ordinal);

    private void ClearActiveMutation()
    {
        activeMutation = null;
        capturedCreatePublication = null;
        capturedRenamePublication = null;
        capturedVotePublication = null;
        capturedResolutionPublication = null;
        capturedDecisionRemoval = null;
    }

    private void IsolateAll(string reason, AuthorityServerContext context)
    {
        Logger.Fatal("Isolating campaign peers after ambiguous kingdom mutation. Route={Route} RequestId={RequestId} Reason={Reason}",
            context.RouteId, context.Header.RequestId, reason);
        foreach (var player in playerManager.Players)
        {
            if (playerManager.IsConnected(player) && playerManager.TryGetPeer(player.ControllerId, out var peer))
            {
                try { peer.Disconnect(); }
                catch (Exception exception) { Logger.Fatal(exception, "Failed to isolate kingdom mutation peer"); }
            }
        }
    }

    private AuthorityServerReply<NetworkCreateKingdomResult> IsolatedCreate(AuthorityServerContext context, string reason)
    {
        IsolateAll(reason, context);
        return new AuthorityServerReply<NetworkCreateKingdomResult>(
            CreateCreateTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, reason), false, true);
    }
    private AuthorityServerReply<NetworkKingdomRenameResult> IsolatedRename(AuthorityServerContext context, string reason)
    {
        IsolateAll(reason, context);
        return new AuthorityServerReply<NetworkKingdomRenameResult>(
            CreateRenameTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, reason), false, true);
    }
    private AuthorityServerReply<NetworkKingdomDecisionVoteResult> IsolatedVote(AuthorityServerContext context, string reason)
    {
        IsolateAll(reason, context);
        return new AuthorityServerReply<NetworkKingdomDecisionVoteResult>(
            CreateVoteTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, reason), false, true);
    }

    private static AuthorityServerReply<NetworkCreateKingdomResult> RejectCreate(AuthorityRequestHeader header, string reason) =>
        new(CreateCreateTerminalResult(header, AuthorityResultStatus.Rejected, reason), false);
    private static AuthorityServerReply<NetworkKingdomRenameResult> RejectRename(AuthorityRequestHeader header, string reason) =>
        new(CreateRenameTerminalResult(header, AuthorityResultStatus.Rejected, reason), false);
    private static AuthorityServerReply<NetworkKingdomDecisionVoteResult> RejectVote(AuthorityRequestHeader header, string reason) =>
        new(CreateVoteTerminalResult(header, AuthorityResultStatus.Rejected, reason), false);

    private static string ValidateCreateWireShape(NetworkRequestCreateKingdom request)
    {
        if (!Bounded(request.ControllerId) || !Bounded(request.KingdomName) || !Bounded(request.CultureId))
            return "invalid-kingdom-create";
        if (!OptionalBounded(request.PartyId) || !OptionalBounded(request.SettlementId))
            return "invalid-kingdom-create-context";
        return null;
    }

    private static string ValidateRenameWireShape(NetworkRequestChangeKingdomName request) =>
        !Bounded(request.KingdomId) || !Bounded(request.Name) ? "invalid-kingdom-rename" : null;

    private static string ValidateVoteWireShape(NetworkRequestKingdomDecisionVote request)
    {
        var vote = request.VoteData;
        if (!Bounded(request.ControllerId) || vote == null || !Bounded(vote.KingdomId) ||
            vote.DecisionIndex < 0 || vote.OutcomeIndex < -1 || vote.SupportWeight < 0 ||
            !OptionalBounded(vote.OutcomeKey)) return "invalid-kingdom-vote";
        if (vote.IsAbstain != (vote.OutcomeIndex < 0)) return "invalid-kingdom-vote-outcome";
        return null;
    }

    private static bool Bounded(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool OptionalBounded(string value) => value == null || value.Length <= 256;
    private static string KeyPart(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty, ":");
    private static string BuildCreateCommandKey(NetworkRequestCreateKingdom request) => string.Concat(
        KeyPart(request.ControllerId), KeyPart(request.KingdomName), KeyPart(request.CultureId),
        KeyPart(request.PartyId), KeyPart(request.SettlementId));
    private static string BuildRenameCommandKey(NetworkRequestChangeKingdomName request) =>
        string.Concat(KeyPart(request.KingdomId), KeyPart(request.Name));
    private static string BuildVoteCommandKey(NetworkRequestKingdomDecisionVote request) =>
        string.Concat(KeyPart(request.ControllerId), VoteSemantics(null, request.VoteData));
    private static string VoteSemantics(string clanId, GameInterface.Services.Kingdoms.Data.KingdomDecisionVoteData vote) =>
        vote == null ? "<null>" : string.Concat(KeyPart(clanId), KeyPart(vote.KingdomId), vote.DecisionIndex, ":",
            vote.OutcomeIndex, ":", vote.SupportWeight, ":", vote.IsAbstain ? "1:" : "0:",
            vote.IsFinal ? "1:" : "0:", KeyPart(vote.OutcomeKey));

    private static AuthorityResultHeader AcceptedHeader(AuthorityRequestHeader header) =>
        new(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted, header.ExpectedRevision, null);
    private static AuthorityResultHeader ResultHeader(AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason);
    private static NetworkCreateKingdomResult CreateCreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(ResultHeader(header, status, reason), null, null, null, null, null, null, null);
    private static NetworkKingdomRenameResult CreateRenameTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(ResultHeader(header, status, reason), null, null, null, null, null);
    private static NetworkKingdomDecisionVoteResult CreateVoteTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(ResultHeader(header, status, reason), null, null, false, -1, null, null);

    private static bool IsExpectedCreateResult(NetworkRequestCreateKingdom request, NetworkCreateKingdomResult result) =>
        string.Equals(request.ControllerId, result.ControllerId, StringComparison.Ordinal) &&
        string.Equals(request.KingdomName.Trim(), result.KingdomName, StringComparison.Ordinal) &&
        string.Equals(request.CultureId, result.CultureId, StringComparison.Ordinal) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
        Bounded(result.KingdomId) && Bounded(result.ClanId);
    private static bool IsExpectedRenameResult(NetworkRequestChangeKingdomName request, NetworkKingdomRenameResult result) =>
        Bounded(result.ControllerId) && string.Equals(request.KingdomId, result.KingdomId, StringComparison.Ordinal) &&
        string.Equals(request.Name.Trim(), result.Name, StringComparison.Ordinal) &&
        Bounded(result.FullName) && Bounded(result.InformalName);
    private static bool IsExpectedVoteResult(NetworkRequestKingdomDecisionVote request, NetworkKingdomDecisionVoteResult result) =>
        string.Equals(request.ControllerId, result.ControllerId, StringComparison.Ordinal) && Bounded(result.ClanId) &&
        VoteEquals(request.VoteData, result.VoteData) && (!result.DecisionResolved || result.ResolutionOutcomeIndex >= 0);
    private static bool VoteEquals(
        GameInterface.Services.Kingdoms.Data.KingdomDecisionVoteData left,
        GameInterface.Services.Kingdoms.Data.KingdomDecisionVoteData right) =>
        left != null && right != null && left.DecisionIndex == right.DecisionIndex &&
        left.OutcomeIndex == right.OutcomeIndex && left.SupportWeight == right.SupportWeight &&
        left.IsAbstain == right.IsAbstain && left.IsFinal == right.IsFinal &&
        string.Equals(left.KingdomId, right.KingdomId, StringComparison.Ordinal) &&
        string.Equals(left.OutcomeKey, right.OutcomeKey, StringComparison.Ordinal);

    private void HandleNetworkAddDecision(MessagePayload<NetworkAddDecision> obj)
    {
        var payload = obj.What;

        messageBroker.Publish(
            this,
            new AddDecision(payload.KingdomId, payload.Data, payload.IgnoreInfluenceCost, payload.RandomNumber));

        var message = new NetworkAddDecision(
            payload.KingdomId,
            payload.Data,
            payload.IgnoreInfluenceCost,
            payload.RandomNumber);

        if (obj.Who is NetPeer peer)
        {
            network.SendAllBut(peer, message);
            return;
        }

        network.SendAll(message);
    }

    private void HandleLocalKingdomPolicyChanged(MessagePayload<KingdomPolicyChanged> obj)
    {
        var payload = obj.What;

        if (!objectManager.TryGetIdWithLogging(payload.Kingdom, out var kingdomId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.Policy, out var policyId)) return;

        var message = new NetworkChangeKingdomPolicy(kingdomId, policyId, payload.IsAdd);
        network.SendAll(message);
    }

    private void HandleLocalDecisionRemoved(MessagePayload<DecisionRemoved> obj)
    {
        var payload = obj.What;

        if (!objectManager.TryGetIdWithLogging(payload.Kingdom, out var kingdomId)) return;

        var message = new NetworkRemoveDecision(kingdomId, payload.Index);
        if (activeMutation != null &&
            string.Equals(activeMutation.RouteId, "kingdom.decision.vote", StringComparison.Ordinal) &&
            string.Equals(activeMutation.KingdomId, kingdomId, StringComparison.Ordinal) &&
            activeMutation.DecisionIndex == payload.Index)
        {
            capturedDecisionRemoval = message;
            return;
        }
        network.SendAll(message);
    }

    private void HandleLocalDecisionAdded(MessagePayload<DecisionAdded> obj)
    {
        var payload = obj.What;

        if (!TryGetKingdomId(payload.Kingdom, out var kingdomId)) return;

        var data = kingdomDecisionDataConverter.Convert(payload.Decision);
        var message = new NetworkAddDecision(kingdomId, data, payload.IgnoreInfluenceCost, payload.RandomNumber);
        network.SendAll(message);
    }

    private bool TryGetKingdomId(Kingdom kingdom, out string kingdomId)
    {
        return objectManager.TryGetIdWithLogging(kingdom, out kingdomId);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<DecisionAdded>(HandleLocalDecisionAdded);
        messageBroker.Unsubscribe<DecisionRemoved>(HandleLocalDecisionRemoved);
        messageBroker.Unsubscribe<KingdomPolicyChanged>(HandleLocalKingdomPolicyChanged);
        messageBroker.Unsubscribe<KingdomDecisionVoteChanged>(HandleLocalKingdomDecisionVoteChanged);
        messageBroker.Unsubscribe<KingdomDecisionResolved>(HandleLocalKingdomDecisionResolved);
        messageBroker.Unsubscribe<PlayerKingdomCreated>(HandleLocalPlayerKingdomCreated);
        messageBroker.Unsubscribe<NetworkAddDecision>(HandleNetworkAddDecision);
        messageBroker.Unsubscribe<KingdomNameChanged>(HandleLocalKingdomNameChanged);
        createRoute.Dispose();
        renameRoute.Dispose();
        voteRoute.Dispose();
    }

    private readonly struct PendingSettlementRestore
    {
        public readonly string PartyId;
        public readonly string SettlementId;

        public PendingSettlementRestore(string partyId, string settlementId)
        {
            PartyId = partyId;
            SettlementId = settlementId;
        }
    }

    private sealed class ActiveAuthorityMutation
    {
        public ActiveAuthorityMutation(string routeId, AuthorityRequestHeader header, string controllerId,
            string requestedName, string kingdomId, int decisionIndex)
        {
            RouteId = routeId;
            Header = header;
            ControllerId = controllerId;
            RequestedName = requestedName;
            KingdomId = kingdomId;
            DecisionIndex = decisionIndex;
        }
        public string RouteId { get; }
        public AuthorityRequestHeader Header { get; }
        public string ControllerId { get; }
        public string RequestedName { get; }
        public string KingdomId { get; }
        public int DecisionIndex { get; }
    }

    private sealed class CreateKingdomIntent
    {
        public CreateKingdomIntent(string controllerId, string kingdomName, string cultureId, string partyId, string settlementId)
        { ControllerId = controllerId; KingdomName = kingdomName; CultureId = cultureId; PartyId = partyId; SettlementId = settlementId; }
        public string ControllerId { get; }
        public string KingdomName { get; }
        public string CultureId { get; }
        public string PartyId { get; }
        public string SettlementId { get; }
    }

    private sealed class RenameKingdomIntent
    {
        public RenameKingdomIntent(string kingdomId, string name) { KingdomId = kingdomId; Name = name; }
        public string KingdomId { get; }
        public string Name { get; }
    }

    private sealed class VoteIntent
    {
        public VoteIntent(string controllerId, GameInterface.Services.Kingdoms.Data.KingdomDecisionVoteData voteData)
        { ControllerId = controllerId; VoteData = voteData; }
        public string ControllerId { get; }
        public GameInterface.Services.Kingdoms.Data.KingdomDecisionVoteData VoteData { get; }
    }
}
