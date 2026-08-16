using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEvents.Messages;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;

namespace Coop.Core.Client.Services.SiegeEvents.Handlers;

/// <summary>
/// Sends the local player's siege entry and exit requests to the server and runs the player-local
/// menu continuation when the approval arrives.
/// </summary>
internal class ClientSiegeEntryHandler : IHandler
{
    private static readonly Serilog.ILogger Logger = LogManager.GetLogger<ClientSiegeEntryHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkBesiegeSettlementApproved> besiegeRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkJoinSiegeCampApproved> joinRoute;
    private PendingInterruptedAssault pendingInterruptedAssault;
    private PendingBreakInContinuation pendingBreakInContinuation;

    // Game-thread only: prompts and CampaignTick continuations both run through the campaign queue.
    private sealed class PendingInterruptedAssault
    {
        public Settlement Settlement { get; }
        public bool BesiegerDefeated { get; }
        public SiegeTerminationRole Role { get; }

        public PendingInterruptedAssault(
            Settlement settlement,
            bool besiegerDefeated,
            SiegeTerminationRole role)
        {
            Settlement = settlement;
            BesiegerDefeated = besiegerDefeated;
            Role = role;
        }
    }

    internal TimeSpan BreakInContinuationTimeout { get; set; }

    // Kept for focused legacy unit tests that exercise only the unrelated break/termination UI path.
    internal ClientSiegeEntryHandler(
        IMessageBroker messageBroker,
        INetwork network,
        INetworkConfig configuration,
        IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface)
        : this(messageBroker, network, configuration, objectManager, siegeEventInterface, null, null)
    {
    }

    public ClientSiegeEntryHandler(
        IMessageBroker messageBroker,
        INetwork network,
        INetworkConfig configuration,
        IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.siegeEventInterface = siegeEventInterface;
        this.configAuthority = configAuthority;
        BreakInContinuationTimeout = configuration.ObjectCreationTimeout;
        if (authorityRequestRouter != null && configAuthority != null)
        {
            besiegeRoute = authorityRequestRouter.Register(
                AuthorityRoute<SiegeEntryIntent, NetworkRequestBesiegeSettlement, NetworkBesiegeSettlementApproved>.Define(
                    "siege.besiege-settlement", AuthorityRouteKind.Command, CreateHeader,
                    (intent, header) => new NetworkRequestBesiegeSettlement(intent.PartyId, intent.SettlementId, header),
                    request => request.Header, result => result.Header, ValidateBesiegeWireShape, BuildBesiegeCommandKey,
                    ValidateHeader, (_, __) => throw new InvalidOperationException("Siege entry routes execute only on the server."),
                    CreateBesiegeTerminalResult, ProbeBesiegeCommit, _ => { }, PresentBesiegeOutcome,
                    configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                        configuration.ObjectCreationTimeout, retryCount: 1), failClosedOnApplyFailure: true,
                    isExpectedClientResult: IsExpectedBesiegeResult));
            joinRoute = authorityRequestRouter.Register(
                AuthorityRoute<SiegeEntryIntent, NetworkRequestJoinSiegeCamp, NetworkJoinSiegeCampApproved>.Define(
                    "siege.join-camp", AuthorityRouteKind.Command, CreateHeader,
                    (intent, header) => new NetworkRequestJoinSiegeCamp(intent.PartyId, intent.SettlementId, header),
                    request => request.Header, result => result.Header, ValidateJoinWireShape, BuildJoinCommandKey,
                    ValidateHeader, (_, __) => throw new InvalidOperationException("Siege entry routes execute only on the server."),
                    CreateJoinTerminalResult, ProbeJoinCommit, _ => { }, PresentJoinOutcome,
                    configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                        configuration.ObjectCreationTimeout, retryCount: 1), failClosedOnApplyFailure: true,
                    isExpectedClientResult: IsExpectedJoinResult));
        }
        messageBroker.Subscribe<BesiegeSettlementAttempted>(HandleBesiegeAttempt);
        messageBroker.Subscribe<JoinSiegeCampAttempted>(HandleJoinAttempt);
        messageBroker.Subscribe<BreakSiegeAttempted>(HandleBreakAttempt);
        messageBroker.Subscribe<NetworkBreakSiegeApproved>(HandleBreakApproved);
        messageBroker.Subscribe<NetworkPromptSiegeDefense>(HandleDefensePrompt);
        messageBroker.Subscribe<NetworkPromptSiegePreparation>(HandlePreparationPrompt);
        messageBroker.Subscribe<NetworkPromptSiegeEnded>(HandleSiegeEndedPrompt);
        messageBroker.Subscribe<AssaultSiegeAttempted>(HandleAssaultAttempt);
        messageBroker.Subscribe<NetworkPromptSiegeAssault>(HandleAssaultPrompt);
        messageBroker.Subscribe<NetworkSnapSiegeCampPartyPosition>(HandleCampPositionSnap);
        messageBroker.Subscribe<CampaignTick>(HandleCampaignTick);
        messageBroker.Subscribe<BreakInContinuationAttempted>(HandleBreakInContinuationAttempt);
        messageBroker.Subscribe<NetworkBreakInContinuationApproved>(HandleBreakInContinuationApproved);
    }

    private void HandleBreakInContinuationAttempt(MessagePayload<BreakInContinuationAttempted> payload)
    {
        var now = DateTime.UtcNow;
        var pending = pendingBreakInContinuation;
        if (pending != null)
        {
            if (pending.ExpiresAtUtc > now)
            {
                Logger.Information(
                    "Ignoring break-in continuation attempt while request {RequestId} is pending",
                    pending.RequestId);
                return;
            }

            Logger.Warning(
                "Retrying break-in continuation after request {RequestId} timed out",
                pending.RequestId);
            ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);
        }

        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        var requestId = Guid.NewGuid().ToString();
        var previousLocationEncounter = PlayerEncounter.LocationEncounter;
        siegeEventInterface.PrepareLocalPlayerBreakIn(obj.Settlement);
        var stagedLocationEncounter = PlayerEncounter.LocationEncounter;
        pendingBreakInContinuation = new PendingBreakInContinuation(
            requestId,
            settlementId,
            PlayerEncounter.Current,
            Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId,
            previousLocationEncounter,
            stagedLocationEncounter,
            now + BreakInContinuationTimeout);

        network.SendAll(new NetworkRequestBreakInContinuation(requestId, partyId, settlementId));
    }

    private void HandleBreakInContinuationApproved(MessagePayload<NetworkBreakInContinuationApproved> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            var pending = pendingBreakInContinuation;
            if (pending == null ||
                pending.RequestId != obj.RequestId ||
                pending.SettlementId != obj.SettlementId)
                return;

            if (!obj.Approved)
            {
                var rejectionMenuId = Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId;
                var shouldRecoverFromDebrief =
                    ReferenceEquals(PlayerEncounter.Current, pending.Encounter) &&
                    rejectionMenuId == pending.MenuId;
                ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);
                if (shouldRecoverFromDebrief)
                {
                    siegeEventInterface.FinishLocalPlayerSiegeLeave();
                    Logger.Information("Server rejected the break-in continuation; returning to the campaign map");
                }
                else
                {
                    Logger.Information("Server rejected the break-in continuation after the encounter changed");
                }
                return;
            }

            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement))
            {
                ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);
                return;
            }

            var currentMenuId = Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId;
            if (!ReferenceEquals(PlayerEncounter.Current, pending.Encounter) ||
                !ReferenceEquals(PlayerEncounter.EncounterSettlement, settlement) ||
                currentMenuId != pending.MenuId)
            {
                ClearPendingBreakInContinuation(pending, restoreLocationEncounter: false);
                Logger.Warning("Ignoring break-in approval because the encounter or menu changed");
                return;
            }

            if (!ReferenceEquals(MobileParty.MainParty?.CurrentSettlement, settlement))
            {
                ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);
                Logger.Error("Ignoring break-in approval because the settlement entry was not applied");
                return;
            }

            ClearPendingBreakInContinuation(pending, restoreLocationEncounter: false);
            siegeEventInterface.ContinueLocalPlayerBreakIn(settlement);
        }, context: nameof(HandleBreakInContinuationApproved));
    }

    private void ClearPendingBreakInContinuation(
        PendingBreakInContinuation pending,
        bool restoreLocationEncounter)
    {
        if (!ReferenceEquals(pendingBreakInContinuation, pending))
            return;

        pendingBreakInContinuation = null;
        if (restoreLocationEncounter &&
            Campaign.Current != null &&
            ReferenceEquals(PlayerEncounter.LocationEncounter, pending.StagedLocationEncounter))
        {
            PlayerEncounter.LocationEncounter = pending.PreviousLocationEncounter;
        }
    }

    private void HandleCampPositionSnap(MessagePayload<NetworkSnapSiegeCampPartyPosition> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.PartyId, out var party)) return;

            using (new AllowedThread())
            {
                party.Position = obj.Position;
            }
        });
    }

    private void HandlePreparationPrompt(MessagePayload<NetworkPromptSiegePreparation> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.AttackerPartyId, out var attackerParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;

            siegeEventInterface.PromptSiegePreparation(attackerParty, settlement);
        });
    }

    private void HandleSiegeEndedPrompt(MessagePayload<NetworkPromptSiegeEnded> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;
            if (MobileParty.MainParty == null) return;
            if (!objectManager.TryGetIdWithLogging(MobileParty.MainParty, out var mainPartyId)) return;

            var role = ResolveTerminationRole(obj, mainPartyId);
            if (obj.InterruptedActiveAssault && role != SiegeTerminationRole.None)
            {
                pendingInterruptedAssault = new PendingInterruptedAssault(
                    settlement,
                    obj.BesiegerDefeated,
                    role);

                if (siegeEventInterface.IsCampaignMissionActive)
                {
                    siegeEventInterface.EndCampaignMission();
                    return;
                }

                FinishPendingInterruptedAssault();
                return;
            }

            siegeEventInterface.PromptSiegeEnded(
                settlement,
                obj.BesiegerDefeated,
                role,
                interruptedActiveAssault: false);
        });
    }

    private void HandleCampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (pendingInterruptedAssault == null ||
            siegeEventInterface.IsCampaignMissionActive)
            return;

        GameThread.RunSafe(
            FinishPendingInterruptedAssault,
            context: nameof(FinishPendingInterruptedAssault));
    }

    private void FinishPendingInterruptedAssault()
    {
        var pending = pendingInterruptedAssault;
        if (pending == null) return;

        pendingInterruptedAssault = null;
        siegeEventInterface.PromptSiegeEnded(
            pending.Settlement,
            pending.BesiegerDefeated,
            pending.Role,
            interruptedActiveAssault: true);
    }

    internal static SiegeTerminationRole ResolveTerminationRole(
        NetworkPromptSiegeEnded message,
        string mainPartyId)
    {
        if (message.LeaderPartyId == mainPartyId)
            return SiegeTerminationRole.AttackerLeader;

        if (message.DefenderPartyIds?.Contains(mainPartyId) == true)
            return SiegeTerminationRole.Defender;

        return message.AttackerPartyIds?.Contains(mainPartyId) == true
            ? SiegeTerminationRole.AttackerMember
            : SiegeTerminationRole.None;
    }

    private void HandleDefensePrompt(MessagePayload<NetworkPromptSiegeDefense> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.AttackerPartyId, out var attackerParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;

            // No AllowedThread wrapper: the method scopes it per section, so the non-joinable
            // defender's settlement leave routes through the normal co-op leave flow.
            siegeEventInterface.PromptSiegeDefense(attackerParty, settlement);
        });
    }

    // Runs on the game thread already — SiegeEntryFlowPatches publishes AssaultSiegeAttempted from the assault
    // menu consequence, and this only resolves ids and sends the request, so no GameThread.RunSafe is needed.
    private void HandleAssaultAttempt(MessagePayload<AssaultSiegeAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        network.SendAll(new NetworkRequestSiegeAssault(partyId, settlementId));
    }

    private void HandleAssaultPrompt(MessagePayload<NetworkPromptSiegeAssault> payload)
    {
        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.AttackerPartyId, out var attackerParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement)) return;

            siegeEventInterface.PromptSiegeAssault(attackerParty, settlement);
        });
    }

    // Runs on the game thread already — SiegeEntryFlowPatches publishes the *Attempted message from the besiege menu consequence, and this only resolves ids and sends the request, so no GameThread.RunSafe is needed.
    private void HandleBesiegeAttempt(MessagePayload<BesiegeSettlementAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        besiegeRoute?.Submit(new SiegeEntryIntent(partyId, settlementId));
    }

    // Runs on the game thread already — published from the join-siege menu consequence; only resolves ids and sends, so no GameThread.RunSafe.
    private void HandleJoinAttempt(MessagePayload<JoinSiegeCampAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        joinRoute?.Submit(new SiegeEntryIntent(partyId, settlementId));
    }

    // Runs on the game thread already — published from the leave-siege consequence; only resolves an id and sends, so no GameThread.RunSafe.
    private void HandleBreakAttempt(MessagePayload<BreakSiegeAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;

        network.SendAll(new NetworkRequestBreakSiege(partyId, obj.FinishLocalMenus));
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

    private static NetworkBesiegeSettlementApproved CreateBesiegeTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null, null);

    private static NetworkJoinSiegeCampApproved CreateJoinTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null);

    private static bool IsExpectedBesiegeResult(NetworkRequestBesiegeSettlement request,
        NetworkBesiegeSettlementApproved result) => result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static bool IsExpectedJoinResult(NetworkRequestJoinSiegeCamp request,
        NetworkJoinSiegeCampApproved result) => result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private AuthorityCommitProbeResult ProbeBesiegeCommit(NetworkBesiegeSettlementApproved result) =>
        HasCanonicalCampMembership(result.PartyId, result.SettlementId, requireLeader: true)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private AuthorityCommitProbeResult ProbeJoinCommit(NetworkJoinSiegeCampApproved result) =>
        HasCanonicalCampMembership(result.PartyId, result.SettlementId, requireLeader: false)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private bool HasCanonicalCampMembership(string partyId, string settlementId, bool requireLeader)
    {
        if (!objectManager.TryGetObject<MobileParty>(partyId, out var party) ||
            !objectManager.TryGetObject<Settlement>(settlementId, out var settlement))
            return false;

        var camp = settlement.SiegeEvent?.BesiegerCamp;
        return camp != null && ReferenceEquals(party.BesiegerCamp, camp) &&
            (!requireLeader || ReferenceEquals(camp.LeaderParty, party));
    }

    private void PresentBesiegeOutcome(AuthorityClientOutcome<NetworkBesiegeSettlementApproved> outcome)
    {
        if (outcome.Applied)
        {
            using (new AllowedThread()) siegeEventInterface.StartLocalPlayerSiegePreparation();
            return;
        }
        UnwindSiegeEntry(outcome.ReasonCode);
    }

    private void PresentJoinOutcome(AuthorityClientOutcome<NetworkJoinSiegeCampApproved> outcome)
    {
        if (outcome.Applied && objectManager.TryGetObject<Settlement>(outcome.Result.SettlementId, out var settlement))
        {
            using (new AllowedThread()) siegeEventInterface.StartLocalPlayerJoinedSiege(settlement);
            return;
        }
        UnwindSiegeEntry(outcome.ReasonCode ?? "canonical-siege-state-missing");
    }

    private static void UnwindSiegeEntry(string reason)
    {
        // Entry attempts do not make a local siege write. Clear the menu/loading residue exactly once
        // when their authority lifecycle reaches any non-applied terminal outcome.
        LoadingWindow.DisableGlobalLoadingWindow();
        PlayerEncounter.LeaveEncounter = true;
        GameMenu.ExitToLast();
        Logger.Information("Siege entry did not apply: {Reason}", reason);
    }

    private void HandleBreakApproved(MessagePayload<NetworkBreakSiegeApproved> payload)
    {
        if (payload.What.Outcome == SiegeBreakOutcome.Rejected)
        {
            Logger.Information("Server rejected the break-siege request; staying at the current menu");
            return;
        }

        if (payload.What.BattleLeaveApplied || !payload.What.FinishLocalMenus) return;

        GameThread.RunSafe(() =>
        {
            using (new AllowedThread())
            {
                siegeEventInterface.FinishLocalPlayerSiegeLeave();
            }
        });
    }

    public void Dispose()
    {
        var pending = pendingBreakInContinuation;
        if (pending != null)
            ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);

        messageBroker.Unsubscribe<BesiegeSettlementAttempted>(HandleBesiegeAttempt);
        messageBroker.Unsubscribe<JoinSiegeCampAttempted>(HandleJoinAttempt);
        messageBroker.Unsubscribe<BreakSiegeAttempted>(HandleBreakAttempt);
        besiegeRoute?.Dispose();
        joinRoute?.Dispose();
        messageBroker.Unsubscribe<NetworkBreakSiegeApproved>(HandleBreakApproved);
        messageBroker.Unsubscribe<NetworkPromptSiegeDefense>(HandleDefensePrompt);
        messageBroker.Unsubscribe<NetworkPromptSiegePreparation>(HandlePreparationPrompt);
        messageBroker.Unsubscribe<NetworkPromptSiegeEnded>(HandleSiegeEndedPrompt);
        messageBroker.Unsubscribe<AssaultSiegeAttempted>(HandleAssaultAttempt);
        messageBroker.Unsubscribe<NetworkPromptSiegeAssault>(HandleAssaultPrompt);
        messageBroker.Unsubscribe<NetworkSnapSiegeCampPartyPosition>(HandleCampPositionSnap);
        messageBroker.Unsubscribe<CampaignTick>(HandleCampaignTick);
        pendingInterruptedAssault = null;
        messageBroker.Unsubscribe<BreakInContinuationAttempted>(HandleBreakInContinuationAttempt);
        messageBroker.Unsubscribe<NetworkBreakInContinuationApproved>(HandleBreakInContinuationApproved);
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

    private sealed class PendingBreakInContinuation
    {
        public readonly string RequestId;
        public readonly string SettlementId;
        public readonly PlayerEncounter Encounter;
        public readonly string MenuId;
        public readonly LocationEncounter PreviousLocationEncounter;
        public readonly LocationEncounter StagedLocationEncounter;
        public readonly DateTime ExpiresAtUtc;

        public PendingBreakInContinuation(
            string requestId,
            string settlementId,
            PlayerEncounter encounter,
            string menuId,
            LocationEncounter previousLocationEncounter,
            LocationEncounter stagedLocationEncounter,
            DateTime expiresAtUtc)
        {
            RequestId = requestId;
            SettlementId = settlementId;
            Encounter = encounter;
            MenuId = menuId;
            PreviousLocationEncounter = previousLocationEncounter;
            StagedLocationEncounter = stagedLocationEncounter;
            ExpiresAtUtc = expiresAtUtc;
        }
    }
}
