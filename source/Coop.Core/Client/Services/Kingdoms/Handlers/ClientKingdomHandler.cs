using Common.Messaging;
using Common.Network;
using Common;
using Common.Util;
using Coop.Core.Server.Services.Kingdoms.Messages;
using GameInterface.Services.Entity;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Data;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ScreenSystem;
using SandBox.GauntletUI;

namespace Coop.Core.Client.Services.Kingdoms.Handlers;

/// <summary>
/// Client side handler for Kingdom internal and network messages
/// </summary>
public class ClientKingdomHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IKingdomCreationSettlementTracker settlementTracker;
    private readonly IKingdomDecisionDataConverter kingdomDecisionDataConverter;
    private readonly IKingdomDecisionVoteManager decisionVoteManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<CreateKingdomIntent, NetworkCreateKingdomResult> createRoute;
    private readonly IAuthorityRouteHandle<RenameKingdomIntent, NetworkKingdomRenameResult> renameRoute;
    private readonly IAuthorityRouteHandle<VoteIntent, NetworkKingdomDecisionVoteResult> voteRoute;
    private readonly Dictionary<string, string> appliedVoteCommits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> appliedCreateCommits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> appliedRenameCommits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingResolutionProof> pendingResolutionProofs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> appliedResolutionCommits = new(StringComparer.Ordinal);
    private PendingSettlementRestore? pendingKingdomCreationSettlement;

    public ClientKingdomHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IControllerIdProvider controllerIdProvider,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IKingdomCreationSettlementTracker settlementTracker,
        IKingdomDecisionDataConverter kingdomDecisionDataConverter,
        IKingdomDecisionVoteManager decisionVoteManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.controllerIdProvider = controllerIdProvider;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.settlementTracker = settlementTracker;
        this.kingdomDecisionDataConverter = kingdomDecisionDataConverter;
        this.decisionVoteManager = decisionVoteManager;
        this.configAuthority = configAuthority;
        createRoute = authorityRequestRouter.Register(
            AuthorityRoute<CreateKingdomIntent, NetworkRequestCreateKingdom, NetworkCreateKingdomResult>.Define(
                "kingdom.create", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestCreateKingdom(intent.ControllerId, intent.KingdomName,
                    intent.CultureId, intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateCreateWireShape, BuildCreateCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Kingdom creation executes only on the server."),
                CreateCreateTerminalResult, ProbeCreateCommit, _ => { }, PresentCreateOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedCreateResult));
        renameRoute = authorityRequestRouter.Register(
            AuthorityRoute<RenameKingdomIntent, NetworkRequestChangeKingdomName, NetworkKingdomRenameResult>.Define(
                "kingdom.rename", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestChangeKingdomName(intent.KingdomId, intent.Name, header),
                request => request.Header, result => result.Header, ValidateRenameWireShape, BuildRenameCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Kingdom rename executes only on the server."),
                CreateRenameTerminalResult, ProbeRenameCommit, _ => { }, PresentRenameOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedRenameResult));
        voteRoute = authorityRequestRouter.Register(
            AuthorityRoute<VoteIntent, NetworkRequestKingdomDecisionVote, NetworkKingdomDecisionVoteResult>.Define(
                "kingdom.decision.vote", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestKingdomDecisionVote(intent.ControllerId, intent.VoteData, header),
                request => request.Header, result => result.Header, ValidateVoteWireShape, BuildVoteCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Kingdom vote executes only on the server."),
                CreateVoteTerminalResult, ProbeVoteCommit, _ => { }, PresentVoteOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedVoteResult));
        messageBroker.Subscribe<NetworkAddDecision>(HandleNetworkAddDecision);
        messageBroker.Subscribe<NetworkRemoveDecision>(HandleNetworkRemoveDecision);
        messageBroker.Subscribe<NetworkChangeKingdomPolicy>(HandleNetworkChangeKingdomPolicy);
        messageBroker.Subscribe<NetworkChangeKingdomDecisionVote>(HandleNetworkChangeKingdomDecisionVote);
        messageBroker.Subscribe<NetworkKingdomDecisionResolved>(HandleNetworkKingdomDecisionResolved);
        messageBroker.Subscribe<NetworkPlayerKingdomCreated>(HandleNetworkPlayerKingdomCreated);
        messageBroker.Subscribe<KingdomDecisionVoteRequested>(HandleKingdomDecisionVoteRequested);
        messageBroker.Subscribe<KingdomCreationRequested>(HandleKingdomCreationRequested);
        messageBroker.Subscribe<DecisionAdded>(HandleLocalDecisionAdded);
        messageBroker.Subscribe<DestroyKingdom>(HandleDestroyKingdom);
        messageBroker.Subscribe<RulingClanChanged>(HandleRulingClanChanged);
        messageBroker.Subscribe<KingdomNameChangeRequested>(HandleKingdomNameChangeRequested);
        messageBroker.Subscribe<NetworkKingdomNameChanged>(HandleNetworkKingdomNameChanged);
    }

    private void HandleKingdomCreationRequested(MessagePayload<KingdomCreationRequested> obj)
    {
        var payload = obj.What;
        pendingKingdomCreationSettlement = CapturePendingSettlementRestore();
        string partyId = null;
        string settlementId = null;
        if (pendingKingdomCreationSettlement.HasValue)
        {
            partyId = pendingKingdomCreationSettlement.Value.PartyId;
            settlementId = pendingKingdomCreationSettlement.Value.SettlementId;
            settlementTracker.Track(partyId, settlementId);
            RestoreSettlementContext(pendingKingdomCreationSettlement.Value, notifyServer: false);
        }
        else if (playerManager.TryGetPlayer(controllerIdProvider.ControllerId, out var player))
        {
            partyId = player.MobilePartyId;
        }

        createRoute.Submit(new CreateKingdomIntent(
            controllerIdProvider.ControllerId, payload.KingdomName, payload.CultureId, partyId, settlementId));
    }

    private void HandleKingdomNameChangeRequested(MessagePayload<KingdomNameChangeRequested> obj)
    {
        var payload = obj.What;

        if (!TryGetKingdomId(payload.Kingdom, out var kingdomId)) return;

        renameRoute.Submit(new RenameKingdomIntent(kingdomId, payload.Name));
    }

    private void HandleNetworkKingdomNameChanged(MessagePayload<NetworkKingdomNameChanged> obj)
    {
        if (obj.What.AuthorityRequestId > 0 &&
            (!configAuthority.IsTrustedServer(obj.Who) ||
             !TryValidateCorrelation(obj.What.SessionId, obj.What.AuthorityRequestId,
                 obj.What.CommittedRevision, obj.What.AuthorityControllerId))) return;
        var kingdomId = obj.What.KingdomId;
        if (obj.What.AuthorityRequestId > 0)
        {
            appliedRenameCommits[CommitKey(obj.What.SessionId, obj.What.AuthorityRequestId,
                obj.What.CommittedRevision, obj.What.AuthorityControllerId)] =
                RenameSemantics(obj.What.KingdomId, obj.What.Name, obj.What.FullName, obj.What.InformalName);
        }

        GameThread.RunSafe(() =>
        {
            if (ScreenManager.TopScreen is not GauntletKingdomScreen kingdomScreen) return;

            var dataSource = kingdomScreen.DataSource;
            if (dataSource == null) return;

            if (!objectManager.TryGetObject(kingdomId, out Kingdom kingdom)) return;

            if (!ReferenceEquals(dataSource.Kingdom, kingdom)) return;
            
            dataSource.OnRefresh();
            dataSource.RefreshValues();
        }, context: nameof(ClientKingdomHandler));
    }

    private void HandleNetworkPlayerKingdomCreated(MessagePayload<NetworkPlayerKingdomCreated> obj)
    {
        var payload = obj.What;
        if (payload.AuthorityRequestId > 0 &&
            (!configAuthority.IsTrustedServer(obj.Who) ||
             !TryValidateCorrelation(payload.SessionId, payload.AuthorityRequestId,
                 payload.CommittedRevision, payload.ControllerId))) return;
        var message = new PlayerKingdomCreated(
            payload.ControllerId,
            payload.KingdomId,
            payload.KingdomName,
            payload.ClanId,
            payload.CultureId);
        messageBroker.Publish(this, message);

        RunSettlementMutation(() =>
        {
            if (payload.ControllerId == controllerIdProvider.ControllerId)
            {
                RestorePendingSettlementAfterKingdomCreation(payload.PartyId, payload.SettlementId);
            }
            else if (TryCreatePendingSettlementRestore(payload.PartyId, payload.SettlementId, out var notificationRestore))
            {
                RestoreSettlementContext(notificationRestore, notifyServer: false);
                ClearRemoteSettlementRestore(notificationRestore);
            }
        });

        if (payload.AuthorityRequestId > 0)
        {
            appliedCreateCommits[CommitKey(payload.SessionId, payload.AuthorityRequestId,
                payload.CommittedRevision, payload.ControllerId)] = CreateSemantics(payload.ControllerId,
                payload.KingdomId, payload.KingdomName, payload.ClanId, payload.CultureId,
                payload.PartyId, payload.SettlementId);
        }
    }

    private void HandleLocalDecisionAdded(MessagePayload<DecisionAdded> obj)
    {
        var payload = obj.What;
        if (!TryGetKingdomId(payload.Kingdom, out var kingdomId)) return;

        var data = kingdomDecisionDataConverter.Convert(payload.Decision);
        var message = new NetworkAddDecision(
            kingdomId,
            data,
            payload.IgnoreInfluenceCost,
            payload.RandomNumber);
        network.SendAll(message);
    }

    private bool TryGetKingdomId(Kingdom kingdom, out string kingdomId)
    {
        return objectManager.TryGetIdWithLogging(kingdom, out kingdomId);
    }

    private PendingSettlementRestore? CapturePendingSettlementRestore()
    {
        var party = ResolveCreatingPlayerParty();
        var settlement = GetPartyCurrentSettlement(party)
            ?? GetCurrentSettlement()
            ?? GetEncounterSettlement();

        if (party == null || settlement == null) return null;
        if (!TryGetPartyId(party, out var partyId)) return null;
        if (!TryGetSettlementId(settlement, out var settlementId)) return null;

        settlementTracker.TrackParty(party, partyId, settlement, settlementId);
        return new PendingSettlementRestore(partyId, settlementId);
    }

    private bool TryGetPartyId(MobileParty party, out string partyId)
    {
        return objectManager.TryGetIdWithLogging(party, out partyId);
    }

    private bool TryGetSettlementId(Settlement settlement, out string settlementId)
    {
        return objectManager.TryGetIdWithLogging(settlement, out settlementId);
    }

    private static Settlement? GetPartyCurrentSettlement(MobileParty? party)
    {
        try
        {
            return party?.CurrentSettlement;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static Settlement? GetCurrentSettlement()
    {
        try
        {
            return Settlement.CurrentSettlement;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static Settlement? GetEncounterSettlement()
    {
        try
        {
            return PlayerEncounter.EncounterSettlement;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private MobileParty? ResolveCreatingPlayerParty()
    {
        if (playerManager.TryGetPlayer(controllerIdProvider.ControllerId, out var player) &&
            objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party))
        {
            return party;
        }

        try
        {
            return MobileParty.MainParty;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private void RestorePendingSettlementAfterKingdomCreation(string partyId, string settlementId)
    {
        var pending = pendingKingdomCreationSettlement;
        pendingKingdomCreationSettlement = null;

        if (!pending.HasValue &&
            TryCreatePendingSettlementRestore(partyId, settlementId, out var notificationPending))
        {
            pending = notificationPending;
        }

        if (!pending.HasValue)
        {
            return;
        }

        RunSettlementMutation(() =>
        {
            RestoreSettlementContext(pending.Value, notifyServer: true);
            settlementTracker.Complete(pending.Value.PartyId);
        });
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

    private void RestoreSettlementContext(PendingSettlementRestore pending, bool notifyServer)
    {
        if (!objectManager.TryGetObject<MobileParty>(pending.PartyId, out var party)) return;
        if (!TryGetSettlement(pending.SettlementId, out var settlement)) return;

        settlementTracker.TrackParty(party, pending.PartyId, settlement, pending.SettlementId);

        bool restoredSettlement = party.CurrentSettlement != settlement;
        if (restoredSettlement)
        {
            EnsurePartySettlement(party, settlement);
        }

        bool needsEncounter = PlayerEncounter.Current == null;
        if (notifyServer && (restoredSettlement || needsEncounter))
        {
            messageBroker.Publish(this, new StartSettlementEncounterAttempted(party, settlement));
        }
    }

    private void ClearRemoteSettlementRestore(PendingSettlementRestore pending)
    {
        if (objectManager.TryGetObject<MobileParty>(pending.PartyId, out var party))
        {
            settlementTracker.Clear(party, pending.PartyId);
            return;
        }

        settlementTracker.Clear(null, pending.PartyId);
    }

    private bool TryGetSettlement(string settlementId, out Settlement settlement)
    {
        return objectManager.TryGetObjectWithLogging(settlementId, out settlement);
    }

    private static void EnsurePartySettlement(MobileParty party, Settlement settlement)
    {
        if (party.CurrentSettlement == settlement) return;

        RunSettlementMutation(() =>
        {
            using (new AllowedThread())
            {
                party.CurrentSettlement = settlement;
            }
        });
    }

    private static void RunSettlementMutation(Action action)
    {
        if (!GameThread.Instance.IsInitialized)
        {
            action();
            return;
        }

        GameThread.RunSafe(action, blocking: true, context: nameof(ClientKingdomHandler));
    }

    private void HandleKingdomDecisionVoteRequested(MessagePayload<KingdomDecisionVoteRequested> obj)
    {
        var payload = obj.What;
        voteRoute.Submit(new VoteIntent(controllerIdProvider.ControllerId, payload.VoteData));
    }

    private void HandleNetworkKingdomDecisionResolved(MessagePayload<NetworkKingdomDecisionResolved> obj)
    {
        var payload = obj.What;
        bool correlated = payload.AuthorityRequestId > 0;
        if (correlated && (!configAuthority.IsTrustedServer(obj.Who) ||
            !TryValidateCorrelation(payload.SessionId, payload.AuthorityRequestId,
                payload.CommittedRevision, payload.AuthorityControllerId))) return;
        bool applied = false;
        RunKingdomMutation(() => applied = decisionVoteManager.ApplyResolved(
            payload.KingdomId, payload.DecisionIndex, payload.OutcomeIndex, payload.IsPlayerDecision,
            payload.OutcomeKey, payload.NotificationText));
        if (!applied || !correlated) return;

        string domainKey = ResolutionDomainKey(payload.KingdomId, payload.DecisionIndex);
        pendingResolutionProofs[domainKey] = new PendingResolutionProof(
            CommitKey(payload.SessionId, payload.AuthorityRequestId, payload.CommittedRevision,
                payload.AuthorityControllerId),
            ResolutionSemantics(payload.KingdomId, payload.DecisionIndex, payload.OutcomeIndex, payload.OutcomeKey));
    }

    private void HandleNetworkChangeKingdomDecisionVote(MessagePayload<NetworkChangeKingdomDecisionVote> obj)
    {
        var payload = obj.What;
        bool correlated = payload.AuthorityRequestId > 0;
        if (correlated && (!configAuthority.IsTrustedServer(obj.Who) ||
            !TryValidateCorrelation(payload.SessionId, payload.AuthorityRequestId,
                payload.CommittedRevision, payload.AuthorityControllerId))) return;
        bool applied = false;
        RunKingdomMutation(() => applied = decisionVoteManager.ApplyRemoteVote(payload.ClanId, payload.VoteData));
        if (!applied || !correlated) return;

        appliedVoteCommits[CommitKey(payload.SessionId, payload.AuthorityRequestId,
            payload.CommittedRevision, payload.AuthorityControllerId)] = VoteSemantics(payload.ClanId, payload.VoteData);
    }

    private void HandleNetworkChangeKingdomPolicy(MessagePayload<NetworkChangeKingdomPolicy> obj)
    {
        var payload = obj.What;
        var message = new ChangeKingdomPolicy(payload.KingdomId, payload.PolicyId, payload.IsAdd);
        messageBroker.Publish(this, message);
    }

    private void HandleNetworkRemoveDecision(MessagePayload<NetworkRemoveDecision> obj)
    {
        var payload = obj.What;

        // Same gate as HandleNetworkAddDecision: decisions are only materialized for the
        // player's own kingdom, so a remove for any other kingdom targets a list that was
        // never populated here - previously that fell through to a guaranteed
        // "Index is out of bounds" warning (and, when the id no longer resolves to a
        // Kingdom on this client, an ObjectManager cast error) on every broadcast.
        // A kingdom that still holds locally-materialized decisions (the clan was a member
        // when they were added) keeps receiving removes so its list is cleaned up.
        if (!ShouldApplyNetworkDecision(payload.KingdomId) &&
            !HasLocallyMaterializedDecisions(payload.KingdomId)) return;

        var message = new RemoveDecision(payload.KingdomId, payload.Index);
        messageBroker.Publish(this, message);

        string domainKey = ResolutionDomainKey(payload.KingdomId, payload.Index);
        if (pendingResolutionProofs.TryGetValue(domainKey, out PendingResolutionProof proof))
        {
            pendingResolutionProofs.Remove(domainKey);
            appliedResolutionCommits[proof.CommitKey] = proof.Semantics;
        }
    }

    private void HandleNetworkAddDecision(MessagePayload<NetworkAddDecision> obj)
    {
        var payload = obj.What;
        if (!ShouldApplyNetworkDecision(payload.KingdomId)) return;

        var message = new AddDecision(payload.KingdomId, payload.Data, payload.IgnoreInfluenceCost, payload.RandomNumber);
        messageBroker.Publish(this, message);
    }

    private bool ShouldApplyNetworkDecision(string kingdomId)
    {
        if (string.IsNullOrWhiteSpace(kingdomId)) return false;
        // Fail CLOSED when the id does not resolve to a Kingdom on this client (destroyed
        // locally, or registered as another type - the live Clan-under-a-kingdom-id cast
        // errors): it cannot be the player's kingdom, and applying anyway just reproduces the
        // downstream lookup error this gate exists to prevent.
        if (!objectManager.TryGetObject(kingdomId, out Kingdom kingdom)) return false;
        // Pre-registration grace: before this client's player registration lands, membership is
        // unknowable - keep applying (the pre-gate behavior) so a joining player's own-kingdom
        // decisions arriving in the same flush are not starved.
        if (!playerManager.TryGetPlayer(controllerIdProvider.ControllerId, out var player)) return true;
        if (string.IsNullOrWhiteSpace(player.ClanId)) return false;
        if (!objectManager.TryGetObject(player.ClanId, out Clan clan)) return false;

        return clan.Kingdom == kingdom;
    }

    /// <summary>
    /// Removes must also pass for a kingdom whose decisions THIS client materialized while the
    /// clan was still a member - after leaving, the membership gate alone would drop the cleanup
    /// broadcasts and stale unresolved decisions would linger until reload.
    /// </summary>
    private bool HasLocallyMaterializedDecisions(string kingdomId)
    {
        return !string.IsNullOrWhiteSpace(kingdomId) &&
               objectManager.TryGetObject(kingdomId, out Kingdom kingdom) &&
               kingdom.UnresolvedDecisions != null &&
               kingdom.UnresolvedDecisions.Count > 0;
    }

    private void HandleDestroyKingdom(MessagePayload<DestroyKingdom> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Kingdom, out var kingdomId)) return;

        network.SendAll(new NetworkDestroyKingdom(kingdomId));
    }

    private void HandleRulingClanChanged(MessagePayload<RulingClanChanged> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Kingdom, out var kingdomId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.Clan, out var clanId)) return;

        network.SendAll(new NetworkRulingClanChanged(kingdomId, clanId));
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
        KingdomDecisionVoteData vote = request.VoteData;
        if (!Bounded(request.ControllerId) || vote == null || !Bounded(vote.KingdomId) ||
            vote.DecisionIndex < 0 || vote.OutcomeIndex < -1 || vote.SupportWeight < 0 ||
            !OptionalBounded(vote.OutcomeKey)) return "invalid-kingdom-vote";
        if (vote.IsAbstain != (vote.OutcomeIndex < 0)) return "invalid-kingdom-vote-outcome";
        return null;
    }

    private static bool Bounded(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool OptionalBounded(string value) => value == null || value.Length <= 256;

    private static string BuildCreateCommandKey(NetworkRequestCreateKingdom request) => string.Concat(
        KeyPart(request.ControllerId), KeyPart(request.KingdomName), KeyPart(request.CultureId),
        KeyPart(request.PartyId), KeyPart(request.SettlementId));
    private static string BuildRenameCommandKey(NetworkRequestChangeKingdomName request) =>
        string.Concat(KeyPart(request.KingdomId), KeyPart(request.Name));
    private static string BuildVoteCommandKey(NetworkRequestKingdomDecisionVote request) =>
        string.Concat(KeyPart(request.ControllerId), VoteSemantics(null, request.VoteData));
    private static string KeyPart(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty, ":");

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
        string.Equals(request.ControllerId, result.ControllerId, StringComparison.Ordinal) &&
        Bounded(result.ClanId) && VoteEquals(request.VoteData, result.VoteData) &&
        (!result.DecisionResolved || result.ResolutionOutcomeIndex >= 0);

    private AuthorityCommitProbeResult ProbeCreateCommit(NetworkCreateKingdomResult result)
    {
        string commitKey = CommitKey(result.Header.SessionId, result.Header.RequestId,
            result.Header.CommittedRevision, result.ControllerId);
        if (!appliedCreateCommits.TryGetValue(commitKey, out string proof) ||
            !string.Equals(proof, CreateSemantics(result.ControllerId, result.KingdomId, result.KingdomName,
                result.ClanId, result.CultureId, result.PartyId, result.SettlementId), StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Pending;
        if (!string.Equals(result.ControllerId, controllerIdProvider.ControllerId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject<Kingdom>(result.KingdomId, out var kingdom) ||
            !objectManager.TryGetObject<Clan>(result.ClanId, out var clan) ||
            !objectManager.TryGetObject<CultureObject>(result.CultureId, out var culture) ||
            !ReferenceEquals(kingdom.RulingClan, clan) || !ReferenceEquals(clan.Kingdom, kingdom) ||
            !ReferenceEquals(kingdom.Culture, culture) || kingdom.Clans?.Contains(clan) != true ||
            !string.Equals(kingdom.Name?.ToString(), result.KingdomName, StringComparison.Ordinal) ||
            !string.Equals(kingdom.InformalName?.ToString(), result.KingdomName, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Pending;

        if (!string.IsNullOrWhiteSpace(result.PartyId))
        {
            if (!playerManager.TryGetPlayer(result.ControllerId, out var player) ||
                !string.Equals(player.MobilePartyId, result.PartyId, StringComparison.Ordinal) ||
                !objectManager.TryGetObject<MobileParty>(result.PartyId, out var party))
                return AuthorityCommitProbeResult.Pending;
            if (!string.IsNullOrWhiteSpace(result.SettlementId) &&
                (!objectManager.TryGetObject<Settlement>(result.SettlementId, out var settlement) ||
                 !ReferenceEquals(party.CurrentSettlement, settlement)))
                return AuthorityCommitProbeResult.Pending;
        }

        return AuthorityCommitProbeResult.Applied;
    }

    private AuthorityCommitProbeResult ProbeRenameCommit(NetworkKingdomRenameResult result)
    {
        if (!string.Equals(result.ControllerId, controllerIdProvider.ControllerId, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Invalid;
        if (!objectManager.TryGetObject<Kingdom>(result.KingdomId, out var kingdom))
            return AuthorityCommitProbeResult.Pending;
        string commitKey = CommitKey(result.Header.SessionId, result.Header.RequestId,
            result.Header.CommittedRevision, result.ControllerId);
        if (!appliedRenameCommits.TryGetValue(commitKey, out string proof) ||
            !string.Equals(proof, RenameSemantics(result.KingdomId, result.Name, result.FullName,
                result.InformalName), StringComparison.Ordinal)) return AuthorityCommitProbeResult.Pending;
        return string.Equals(kingdom.Name?.ToString(), result.FullName, StringComparison.Ordinal) &&
               string.Equals(kingdom.InformalName?.ToString(), result.InformalName, StringComparison.Ordinal)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeVoteCommit(NetworkKingdomDecisionVoteResult result)
    {
        if (!string.Equals(result.ControllerId, controllerIdProvider.ControllerId, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Invalid;
        string commitKey = CommitKey(result.Header.SessionId, result.Header.RequestId,
            result.Header.CommittedRevision, result.ControllerId);
        if (!result.DecisionResolved)
            return appliedVoteCommits.TryGetValue(commitKey, out string vote) &&
                   string.Equals(vote, VoteSemantics(result.ClanId, result.VoteData), StringComparison.Ordinal)
                ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

        return appliedResolutionCommits.TryGetValue(commitKey, out string resolution) &&
               string.Equals(resolution, ResolutionSemantics(result.VoteData.KingdomId,
                   result.VoteData.DecisionIndex, result.ResolutionOutcomeIndex, result.ResolutionOutcomeKey),
                   StringComparison.Ordinal)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void PresentCreateOutcome(AuthorityClientOutcome<NetworkCreateKingdomResult> outcome)
    {
        if (outcome.Applied) return;
        if (pendingKingdomCreationSettlement.HasValue)
            settlementTracker.Clear(null, pendingKingdomCreationSettlement.Value.PartyId);
        pendingKingdomCreationSettlement = null;
        messageBroker.Publish(this, new SendInformationMessage("Unable to create kingdom: the server did not apply the request."));
    }

    private void PresentRenameOutcome(AuthorityClientOutcome<NetworkKingdomRenameResult> outcome)
    {
        if (!outcome.Applied)
            messageBroker.Publish(this, new SendInformationMessage("Unable to rename kingdom: the server rejected the request."));
    }

    private void PresentVoteOutcome(AuthorityClientOutcome<NetworkKingdomDecisionVoteResult> outcome)
    {
        if (!outcome.Applied)
            messageBroker.Publish(this, new SendInformationMessage("Unable to submit kingdom vote: the server rejected the request."));
    }

    private bool TryValidateCorrelation(string sessionId, long requestId, long revision, string controllerId) =>
        Bounded(controllerId) && requestId > 0 && configAuthority.TryGetCurrent(out ModConfigSnapshot current) &&
        string.Equals(sessionId, current.SessionId, StringComparison.Ordinal) && revision == current.Revision;

    private static string CommitKey(string sessionId, long requestId, long revision, string controllerId) =>
        string.Concat(KeyPart(sessionId), requestId, ":", revision, ":", KeyPart(controllerId));
    private static string ResolutionDomainKey(string kingdomId, int decisionIndex) =>
        string.Concat(KeyPart(kingdomId), decisionIndex);
    private static string VoteSemantics(string clanId, KingdomDecisionVoteData vote) => vote == null ? "<null>" : string.Concat(
        KeyPart(clanId), KeyPart(vote.KingdomId), vote.DecisionIndex, ":", vote.OutcomeIndex, ":",
        vote.SupportWeight, ":", vote.IsAbstain ? "1:" : "0:", vote.IsFinal ? "1:" : "0:", KeyPart(vote.OutcomeKey));
    private static string ResolutionSemantics(string kingdomId, int decisionIndex, int outcomeIndex, string outcomeKey) =>
        string.Concat(KeyPart(kingdomId), decisionIndex, ":", outcomeIndex, ":", KeyPart(outcomeKey));
    private static string CreateSemantics(string controllerId, string kingdomId, string kingdomName, string clanId,
        string cultureId, string partyId, string settlementId) => string.Concat(KeyPart(controllerId),
        KeyPart(kingdomId), KeyPart(kingdomName), KeyPart(clanId), KeyPart(cultureId), KeyPart(partyId), KeyPart(settlementId));
    private static string RenameSemantics(string kingdomId, string name, string fullName, string informalName) =>
        string.Concat(KeyPart(kingdomId), KeyPart(name), KeyPart(fullName), KeyPart(informalName));
    private static bool VoteEquals(KingdomDecisionVoteData left, KingdomDecisionVoteData right) =>
        left != null && right != null && left.DecisionIndex == right.DecisionIndex &&
        left.OutcomeIndex == right.OutcomeIndex && left.SupportWeight == right.SupportWeight &&
        left.IsAbstain == right.IsAbstain && left.IsFinal == right.IsFinal &&
        string.Equals(left.KingdomId, right.KingdomId, StringComparison.Ordinal) &&
        string.Equals(left.OutcomeKey, right.OutcomeKey, StringComparison.Ordinal);

    private static void RunKingdomMutation(Action action)
    {
        if (!GameThread.Instance.IsInitialized) action();
        else GameThread.RunSafe(action, blocking: true, context: nameof(ClientKingdomHandler));
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAddDecision>(HandleNetworkAddDecision);
        messageBroker.Unsubscribe<NetworkRemoveDecision>(HandleNetworkRemoveDecision);
        messageBroker.Unsubscribe<NetworkChangeKingdomPolicy>(HandleNetworkChangeKingdomPolicy);
        messageBroker.Unsubscribe<NetworkChangeKingdomDecisionVote>(HandleNetworkChangeKingdomDecisionVote);
        messageBroker.Unsubscribe<NetworkKingdomDecisionResolved>(HandleNetworkKingdomDecisionResolved);
        messageBroker.Unsubscribe<NetworkPlayerKingdomCreated>(HandleNetworkPlayerKingdomCreated);
        messageBroker.Unsubscribe<KingdomDecisionVoteRequested>(HandleKingdomDecisionVoteRequested);
        messageBroker.Unsubscribe<KingdomCreationRequested>(HandleKingdomCreationRequested);
        messageBroker.Unsubscribe<DecisionAdded>(HandleLocalDecisionAdded);
        messageBroker.Unsubscribe<DestroyKingdom>(HandleDestroyKingdom);
        messageBroker.Unsubscribe<RulingClanChanged>(HandleRulingClanChanged);
        messageBroker.Unsubscribe<KingdomNameChangeRequested>(HandleKingdomNameChangeRequested);
        messageBroker.Unsubscribe<NetworkKingdomNameChanged>(HandleNetworkKingdomNameChanged);
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
        public VoteIntent(string controllerId, KingdomDecisionVoteData voteData) { ControllerId = controllerId; VoteData = voteData; }
        public string ControllerId { get; }
        public KingdomDecisionVoteData VoteData { get; }
    }

    private readonly struct PendingResolutionProof
    {
        public PendingResolutionProof(string commitKey, string semantics) { CommitKey = commitKey; Semantics = semantics; }
        public string CommitKey { get; }
        public string Semantics { get; }
    }
}


