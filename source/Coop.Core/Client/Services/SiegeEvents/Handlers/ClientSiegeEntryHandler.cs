using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEvents.Messages;
using GameInterface.Services.UI.Interfaces;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

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
    private readonly ILoadingInterface loadingInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkBesiegeSettlementApproved> besiegeRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkJoinSiegeCampApproved> joinRoute;
    private readonly IAuthorityRouteHandle<SiegeBreakIntent, NetworkBreakSiegeApproved> breakRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkSiegeAssaultApproved> assaultRoute;
    private readonly IAuthorityRouteHandle<SiegeEntryIntent, NetworkBreakInContinuationApproved> breakInRoute;
    private PendingInterruptedAssault pendingInterruptedAssault;
    private PendingBreakInContinuation pendingBreakInContinuation;
    private string pendingBreakPartyId;
    private CorrelatedAssaultState lastAssaultState;
    private CorrelatedBreakInState lastBreakInState;

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

    // Kept for focused legacy unit tests that exercise only the unrelated break/termination UI path.
    internal ClientSiegeEntryHandler(
        IMessageBroker messageBroker,
        INetwork network,
        INetworkConfig configuration,
        IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface)
        : this(messageBroker, network, configuration, objectManager, siegeEventInterface, null, null, null)
    {
    }

    public ClientSiegeEntryHandler(
        IMessageBroker messageBroker,
        INetwork network,
        INetworkConfig configuration,
        IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ILoadingInterface loadingInterface)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.siegeEventInterface = siegeEventInterface;
        this.loadingInterface = loadingInterface;
        this.configAuthority = configAuthority;
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
            breakRoute = authorityRequestRouter.Register(
                AuthorityRoute<SiegeBreakIntent, NetworkRequestBreakSiege, NetworkBreakSiegeApproved>.Define(
                    "siege.break", AuthorityRouteKind.Command, CreateHeader,
                    (intent, header) => new NetworkRequestBreakSiege(intent.PartyId, intent.FinishLocalMenus, header),
                    request => request.Header, result => result.Header, ValidateBreakWireShape, BuildBreakCommandKey,
                    ValidateHeader, (_, __) => throw new InvalidOperationException("Siege break routes execute only on the server."),
                    CreateBreakTerminalResult, ProbeBreakCommit, _ => { }, PresentBreakOutcome,
                    configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                        configuration.ObjectCreationTimeout, retryCount: 1), failClosedOnApplyFailure: true,
                    isExpectedClientResult: IsExpectedBreakResult));
            assaultRoute = authorityRequestRouter.Register(
                AuthorityRoute<SiegeEntryIntent, NetworkRequestSiegeAssault, NetworkSiegeAssaultApproved>.Define(
                    "siege.assault", AuthorityRouteKind.Command, CreateHeader,
                    (intent, header) => new NetworkRequestSiegeAssault(intent.PartyId, intent.SettlementId, header),
                    request => request.Header, result => result.Header, ValidateAssaultWireShape, BuildAssaultCommandKey,
                    ValidateHeader, (_, __) => throw new InvalidOperationException("Siege assault routes execute only on the server."),
                    CreateAssaultTerminalResult, ProbeAssaultCommit, _ => { }, PresentAssaultOutcome,
                    // An assault opens a siege mission, so the apply budget bounds a scene load, not
                    // a round trip. Same reason as map-event.battle-start; see MissionEntryTimeout.
                    configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                        configuration.MissionEntryTimeout, retryCount: 1), failClosedOnApplyFailure: true,
                    isExpectedClientResult: IsExpectedAssaultResult));
            breakInRoute = authorityRequestRouter.Register(
                AuthorityRoute<SiegeEntryIntent, NetworkRequestBreakInContinuation, NetworkBreakInContinuationApproved>.Define(
                    "siege.break-in-continuation", AuthorityRouteKind.Command, CreateHeader,
                    (intent, header) => new NetworkRequestBreakInContinuation(null, intent.PartyId, intent.SettlementId, header),
                    request => request.Header, result => result.Header, ValidateBreakInWireShape, BuildBreakInCommandKey,
                    ValidateHeader, (_, __) => throw new InvalidOperationException("Siege break-in routes execute only on the server."),
                    CreateBreakInTerminalResult, ProbeBreakInCommit, _ => { }, PresentBreakInOutcome,
                    configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                        configuration.ObjectCreationTimeout, retryCount: 1), failClosedOnApplyFailure: true,
                    isExpectedClientResult: IsExpectedBreakInResult));
        }
        messageBroker.Subscribe<BesiegeSettlementAttempted>(HandleBesiegeAttempt);
        messageBroker.Subscribe<JoinSiegeCampAttempted>(HandleJoinAttempt);
        messageBroker.Subscribe<BreakSiegeAttempted>(HandleBreakAttempt);
        messageBroker.Subscribe<NetworkPromptSiegeDefense>(HandleDefensePrompt);
        messageBroker.Subscribe<NetworkPromptSiegePreparation>(HandlePreparationPrompt);
        messageBroker.Subscribe<NetworkPromptSiegeEnded>(HandleSiegeEndedPrompt);
        messageBroker.Subscribe<AssaultSiegeAttempted>(HandleAssaultAttempt);
        messageBroker.Subscribe<NetworkPromptSiegeAssault>(HandleAssaultPrompt);
        messageBroker.Subscribe<NetworkSnapSiegeCampPartyPosition>(HandleCampPositionSnap);
        messageBroker.Subscribe<CampaignTick>(HandleCampaignTick);
        messageBroker.Subscribe<BreakInContinuationAttempted>(HandleBreakInContinuationAttempt);
        messageBroker.Subscribe<NetworkPartyEnterSettlement>(HandleCorrelatedSettlementEntry);
    }

    private void HandleBreakInContinuationAttempt(MessagePayload<BreakInContinuationAttempted> payload)
    {
        var pending = pendingBreakInContinuation;
        if (pending != null)
        {
            Logger.Information("Ignoring break-in continuation while the authority route is pending");
            return;
        }

        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        var previousLocationEncounter = PlayerEncounter.LocationEncounter;
        siegeEventInterface.PrepareLocalPlayerBreakIn(obj.Settlement);
        var stagedLocationEncounter = PlayerEncounter.LocationEncounter;
        pendingBreakInContinuation = new PendingBreakInContinuation(
            partyId,
            settlementId,
            PlayerEncounter.Current,
            Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId,
            previousLocationEncounter,
            stagedLocationEncounter);

        if (breakInRoute == null)
        {
            ClearPendingBreakInContinuation(pendingBreakInContinuation, restoreLocationEncounter: true);
            return;
        }

        breakInRoute.Submit(new SiegeEntryIntent(partyId, settlementId));
    }

    private void HandleCorrelatedSettlementEntry(MessagePayload<NetworkPartyEnterSettlement> payload)
    {
        var obj = payload.What;
        if (obj.Header.RequestId <= 0 || configAuthority?.IsTrustedServer(payload.Who) != true) return;
        lastBreakInState = new CorrelatedBreakInState(obj.Header, obj.PartyId, obj.SettlementId);
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

        if (assaultRoute == null)
        {
            UnwindSiegeEntry("authority-route-unavailable");
            return;
        }

        assaultRoute.Submit(new SiegeEntryIntent(partyId, settlementId));
    }

    private void HandleAssaultPrompt(MessagePayload<NetworkPromptSiegeAssault> payload)
    {
        var obj = payload.What;

        // A prompt can create a local encounter, so neither its presentation nor its route proof
        // may be accepted from a peer that has not passed the server trust gate.
        if (configAuthority?.IsTrustedServer(payload.Who) != true) return;

        GameThread.RunSafe(() =>
        {
            if (obj.Header.RequestId > 0)
                lastAssaultState = new CorrelatedAssaultState(
                    obj.Header,
                    obj.RequestingPartyId,
                    obj.SettlementId,
                    obj.MapEventId,
                    obj.AttackerPartyId);
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

        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId))
        {
            UnwindSiegeBreak("party-id-unavailable");
            return;
        }

        if (breakRoute == null)
        {
            UnwindSiegeBreak("authority-route-unavailable");
            return;
        }

        if (pendingBreakPartyId != null)
        {
            Logger.Information("Ignoring duplicate siege break while party {PartyId} is pending", pendingBreakPartyId);
            return;
        }

        pendingBreakPartyId = partyId;
        breakRoute.Submit(new SiegeBreakIntent(partyId, obj.FinishLocalMenus), _ => pendingBreakPartyId = null);
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

    private static NetworkBreakSiegeApproved CreateBreakTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(SiegeBreakOutcome.Rejected, false, false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkSiegeAssaultApproved CreateAssaultTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null, null);

    private static NetworkBreakInContinuationApproved CreateBreakInTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), null);

    private static bool IsExpectedBreakResult(NetworkRequestBreakSiege request,
        NetworkBreakSiegeApproved result) =>
        (result.Header.Status == AuthorityResultStatus.Accepted
            ? result.Outcome is SiegeBreakOutcome.Applied or SiegeBreakOutcome.AlreadyLeft
            : result.Outcome == SiegeBreakOutcome.Rejected) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        request.FinishLocalMenus == result.FinishLocalMenus;

    private static bool IsExpectedAssaultResult(NetworkRequestSiegeAssault request,
        NetworkSiegeAssaultApproved result) => result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static bool IsExpectedBreakInResult(NetworkRequestBreakInContinuation request,
        NetworkBreakInContinuationApproved result) => result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private AuthorityCommitProbeResult ProbeBesiegeCommit(NetworkBesiegeSettlementApproved result) =>
        HasCanonicalCampMembership(result.PartyId, result.SettlementId, requireLeader: true)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private AuthorityCommitProbeResult ProbeJoinCommit(NetworkJoinSiegeCampApproved result) =>
        HasCanonicalCampMembership(result.PartyId, result.SettlementId, requireLeader: false)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private AuthorityCommitProbeResult ProbeBreakCommit(NetworkBreakSiegeApproved result)
    {
        if (!objectManager.TryGetObject<MobileParty>(result.PartyId, out var party))
            return AuthorityCommitProbeResult.Pending;

        // Result receipt alone is not a menu-continuation proof. Both branches require the local
        // replica to have dropped the requester from the camp and from any active battle phase.
        return party.BesiegerCamp == null && party.MapEvent == null
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeAssaultCommit(NetworkSiegeAssaultApproved result)
    {
        var state = lastAssaultState;
        if (state == null) return AuthorityCommitProbeResult.Pending;
        if (IsSameAuthorityRequest(state.Header, result.Header) &&
            (!HeadersMatch(state.Header, result.Header) ||
             !string.Equals(state.RequestingPartyId, result.PartyId, StringComparison.Ordinal) ||
             !string.Equals(state.SettlementId, result.SettlementId, StringComparison.Ordinal) ||
             !string.Equals(state.AttackerPartyId, result.AttackerPartyId, StringComparison.Ordinal) ||
             !string.Equals(state.MapEventId, result.MapEventId, StringComparison.Ordinal)))
            return AuthorityCommitProbeResult.Invalid;
        if (!HeadersMatch(state.Header, result.Header) ||
            !string.Equals(state.RequestingPartyId, result.PartyId, StringComparison.Ordinal) ||
            !string.Equals(state.SettlementId, result.SettlementId, StringComparison.Ordinal) ||
            !string.Equals(state.AttackerPartyId, result.AttackerPartyId, StringComparison.Ordinal) ||
            !string.Equals(state.MapEventId, result.MapEventId, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject<MobileParty>(result.PartyId, out var party) ||
            !objectManager.TryGetObject<Settlement>(result.SettlementId, out var settlement) ||
            !objectManager.TryGetObject<TaleWorlds.CampaignSystem.MapEvents.MapEvent>(state.MapEventId, out var mapEvent))
            return AuthorityCommitProbeResult.Pending;

        return ReferenceEquals(settlement.Party?.MapEvent, mapEvent) &&
            ReferenceEquals(party.MapEvent, mapEvent) &&
            mapEvent.IsSiegeAssault && party.Party.Side == BattleSideEnum.Attacker &&
            objectManager.TryGetId(mapEvent.AttackerSide?.LeaderParty, out var authoritativeAttackerId) &&
            objectManager.TryGetId(settlement.SiegeEvent?.BesiegerCamp?.LeaderParty, out var campLeaderId) &&
            string.Equals(campLeaderId, result.AttackerPartyId, StringComparison.Ordinal) &&
            string.Equals(authoritativeAttackerId, result.AttackerPartyId, StringComparison.Ordinal)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeBreakInCommit(NetworkBreakInContinuationApproved result)
    {
        var state = lastBreakInState;
        if (state == null) return AuthorityCommitProbeResult.Pending;
        if (IsSameAuthorityRequest(state.Header, result.Header) &&
            (!HeadersMatch(state.Header, result.Header) ||
             !string.Equals(state.PartyId, result.PartyId, StringComparison.Ordinal) ||
             !string.Equals(state.SettlementId, result.SettlementId, StringComparison.Ordinal)))
            return AuthorityCommitProbeResult.Invalid;
        if (!HeadersMatch(state.Header, result.Header) ||
            !string.Equals(state.PartyId, result.PartyId, StringComparison.Ordinal) ||
            !string.Equals(state.SettlementId, result.SettlementId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject<MobileParty>(result.PartyId, out var party) ||
            !objectManager.TryGetObject<Settlement>(result.SettlementId, out var settlement))
            return AuthorityCommitProbeResult.Pending;

        return ReferenceEquals(party.CurrentSettlement, settlement)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private static bool HeadersMatch(AuthorityResultHeader left, AuthorityResultHeader right) =>
        left.RequestId == right.RequestId && left.CommittedRevision == right.CommittedRevision &&
        left.Status == right.Status && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal);

    private static bool IsSameAuthorityRequest(AuthorityResultHeader left, AuthorityResultHeader right) =>
        left.RequestId == right.RequestId && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal);

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

    private void UnwindSiegeEntry(string reason)
    {
        // Entry attempts do not make a local siege write. Clear the menu/loading residue exactly once
        // when their authority lifecycle reaches any non-applied terminal outcome.
        loadingInterface?.HideLoadingScreen();
        PlayerEncounter.LeaveEncounter = true;
        GameMenu.ExitToLast();
        Logger.Information("Siege entry did not apply: {Reason}", reason);
    }

    private void PresentBreakOutcome(AuthorityClientOutcome<NetworkBreakSiegeApproved> outcome)
    {
        if (!outcome.Applied)
        {
            UnwindSiegeBreak(outcome.ReasonCode ?? "siege-break-not-applied");
            return;
        }

        if (outcome.Result.BattleLeaveApplied || !outcome.Result.FinishLocalMenus) return;

        using (new AllowedThread())
        {
            siegeEventInterface.FinishLocalPlayerSiegeLeave();
        }
    }

    private void PresentAssaultOutcome(AuthorityClientOutcome<NetworkSiegeAssaultApproved> outcome)
    {
        // The correlated prompt has already adopted the canonical assault encounter. A result is
        // only a completion gate; it must not create a second client-local map event.
        if (outcome.Applied) return;
        UnwindSiegeEntry(outcome.ReasonCode ?? "siege-assault-not-applied");
    }

    private void PresentBreakInOutcome(AuthorityClientOutcome<NetworkBreakInContinuationApproved> outcome)
    {
        var pending = pendingBreakInContinuation;
        if (pending == null) return;

        if (!outcome.Applied ||
            !objectManager.TryGetObject<Settlement>(outcome.Result.SettlementId, out var settlement))
        {
            var rejectionMenuId = Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId;
            bool shouldRecoverFromDebrief = ReferenceEquals(PlayerEncounter.Current, pending.Encounter) &&
                rejectionMenuId == pending.MenuId;
            ClearPendingBreakInContinuation(pending, restoreLocationEncounter: true);
            if (shouldRecoverFromDebrief)
                siegeEventInterface.FinishLocalPlayerSiegeLeave();
            return;
        }

        var currentMenuId = Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId;
        if (!ReferenceEquals(PlayerEncounter.Current, pending.Encounter) ||
            !ReferenceEquals(PlayerEncounter.EncounterSettlement, settlement) || currentMenuId != pending.MenuId)
        {
            ClearPendingBreakInContinuation(pending, restoreLocationEncounter: false);
            Logger.Warning("Ignoring break-in authority outcome because the encounter or menu changed");
            return;
        }

        ClearPendingBreakInContinuation(pending, restoreLocationEncounter: false);
        siegeEventInterface.ContinueLocalPlayerBreakIn(settlement);
    }

    private void UnwindSiegeBreak(string reason)
    {
        // A rejected/cancelled/timeout leave has no safe local mutation to finish. Remove only
        // transient UI residue and keep the current native menu usable; replica-apply failures
        // are fail-closed by the authority router after this cleanup.
        loadingInterface?.HideLoadingScreen();
        Logger.Information("Siege break did not apply: {Reason}", reason);
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
        breakRoute?.Dispose();
        assaultRoute?.Dispose();
        breakInRoute?.Dispose();
        messageBroker.Unsubscribe<NetworkPromptSiegeDefense>(HandleDefensePrompt);
        messageBroker.Unsubscribe<NetworkPromptSiegePreparation>(HandlePreparationPrompt);
        messageBroker.Unsubscribe<NetworkPromptSiegeEnded>(HandleSiegeEndedPrompt);
        messageBroker.Unsubscribe<AssaultSiegeAttempted>(HandleAssaultAttempt);
        messageBroker.Unsubscribe<NetworkPromptSiegeAssault>(HandleAssaultPrompt);
        messageBroker.Unsubscribe<NetworkSnapSiegeCampPartyPosition>(HandleCampPositionSnap);
        messageBroker.Unsubscribe<CampaignTick>(HandleCampaignTick);
        pendingInterruptedAssault = null;
        messageBroker.Unsubscribe<BreakInContinuationAttempted>(HandleBreakInContinuationAttempt);
        messageBroker.Unsubscribe<NetworkPartyEnterSettlement>(HandleCorrelatedSettlementEntry);
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

    private sealed class PendingBreakInContinuation
    {
        public readonly string PartyId;
        public readonly string SettlementId;
        public readonly PlayerEncounter Encounter;
        public readonly string MenuId;
        public readonly LocationEncounter PreviousLocationEncounter;
        public readonly LocationEncounter StagedLocationEncounter;

        public PendingBreakInContinuation(
            string partyId,
            string settlementId,
            PlayerEncounter encounter,
            string menuId,
            LocationEncounter previousLocationEncounter,
            LocationEncounter stagedLocationEncounter)
        {
            PartyId = partyId;
            SettlementId = settlementId;
            Encounter = encounter;
            MenuId = menuId;
            PreviousLocationEncounter = previousLocationEncounter;
            StagedLocationEncounter = stagedLocationEncounter;
        }
    }

    private sealed class CorrelatedAssaultState
    {
        public CorrelatedAssaultState(
            AuthorityResultHeader header,
            string requestingPartyId,
            string settlementId,
            string mapEventId,
            string attackerPartyId)
        {
            Header = header;
            RequestingPartyId = requestingPartyId;
            SettlementId = settlementId;
            MapEventId = mapEventId;
            AttackerPartyId = attackerPartyId;
        }

        public AuthorityResultHeader Header { get; }
        public string RequestingPartyId { get; }
        public string SettlementId { get; }
        public string MapEventId { get; }
        public string AttackerPartyId { get; }
    }

    private sealed class CorrelatedBreakInState
    {
        public CorrelatedBreakInState(AuthorityResultHeader header, string partyId, string settlementId)
        {
            Header = header;
            PartyId = partyId;
            SettlementId = settlementId;
        }

        public AuthorityResultHeader Header { get; }
        public string PartyId { get; }
        public string SettlementId { get; }
    }
}
