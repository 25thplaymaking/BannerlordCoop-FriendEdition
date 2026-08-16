using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.TournamentGames;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Tournaments.Handlers;

internal sealed partial class TournamentSessionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<TournamentSessionHandler>();
    internal static TournamentSessionHandler Instance { get; private set; }

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ITournamentSessionRegistry sessionRegistry;
    private readonly ITournamentGameInterface tournamentGameInterface;
    private readonly ITournamentNativeRemovalAuthorization nativeRemovalAuthorization;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<TournamentJoinIntent, NetworkTournamentJoinResult> joinRoute;
    private readonly IAuthorityRouteHandle<TournamentLeavePreparationIntent, NetworkTournamentLeavePreparationResult> leavePreparationRoute;
    private readonly IAuthorityRouteHandle<TournamentLaunchIntent, NetworkTournamentLaunchResult> startRoute;
    private readonly IAuthorityRouteHandle<TournamentLaunchIntent, NetworkTournamentLaunchResult> spectateRoute;
    private readonly IAuthorityRouteHandle<TournamentMissionEnteredIntent, NetworkTournamentMissionEnteredResult> missionEnteredRoute;
    private readonly IAuthorityRouteHandle<TournamentChoiceIntent, NetworkTournamentChoiceResult> choiceRoute;
    private readonly IAuthorityRouteHandle<TournamentBetIntent, NetworkTournamentBetResult> betRoute;
    private readonly Dictionary<string, NetworkEnterTournamentMission> pendingMissionLaunches = new();
    private readonly HashSet<string> openedMissionLaunches = new();
    private readonly Dictionary<string, BetLedgerEntry> betLedger = new();
    private readonly Dictionary<string, NetworkTournamentBetState> receivedBetStates = new();
    private readonly Dictionary<string, TournamentCompletionTransaction> completionTransactions = new();
    private readonly HashSet<string> completionInProgress = new();
    private readonly HashSet<string> liveProgressionControllers = new();
    private readonly HashSet<string> acceptedHitProgression = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<NetPeer, string> tournamentPeerControllers = new();

    public TournamentSessionHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IControllerIdProvider controllerIdProvider,
        ITournamentSessionRegistry sessionRegistry,
        ITournamentGameInterface tournamentGameInterface,
        ITournamentNativeRemovalAuthorization nativeRemovalAuthorization,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.sessionRegistry = sessionRegistry;
        this.tournamentGameInterface = tournamentGameInterface;
        this.nativeRemovalAuthorization = nativeRemovalAuthorization;
        this.configAuthority = configAuthority;
        Instance = this;

        joinRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentJoinIntent, NetworkRequestJoinTournament, NetworkTournamentJoinResult>.Define(
                "tournament.join", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestJoinTournament(header, intent.TownId, intent.SessionId, intent.ExpectedRevision),
                request => request.Header, result => result.Header, ValidateJoinWireShape, BuildJoinCommandKey,
                ValidateAuthorityHeader, ExecuteJoin, CreateJoinTerminal, ProbeJoinApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentJoinTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => result.Status != AuthorityResultStatus.Accepted ||
                    result.Snapshot != null && result.Snapshot.TownId == request.TownId));
        leavePreparationRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentLeavePreparationIntent, NetworkRequestLeaveTournamentPreparation,
                NetworkTournamentLeavePreparationResult>.Define(
                "tournament.leave-preparation", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestLeaveTournamentPreparation(header, intent.SessionId, intent.ExpectedRevision),
                request => request.Header, result => result.Header, ValidateLeavePreparationWireShape,
                BuildLeavePreparationCommandKey, ValidateAuthorityHeader, ExecuteLeavePreparation,
                CreateLeavePreparationTerminal, ProbeLeavePreparationApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentLeavePreparationTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => result.Status != AuthorityResultStatus.Accepted ||
                    result.Snapshot != null && result.Snapshot.SessionId == request.SessionId ||
                    result.RemovedSessionId == request.SessionId));
        startRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentLaunchIntent, NetworkRequestStartTournament, NetworkTournamentLaunchResult>.Define(
                "tournament.start", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestStartTournament(header, intent.SessionId, intent.ExpectedRevision),
                request => request.Header, result => result.Header, ValidateStartWireShape, BuildStartCommandKey,
                ValidateAuthorityHeader, ExecuteStart, CreateLaunchTerminal, ProbeStartApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentStartTerminal,
                configAuthority.IsTrustedServer, new AuthorityTimeoutPolicy(
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), retryCount: 0),
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedLaunchResult));
        spectateRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentLaunchIntent, NetworkRequestSpectateTournament, NetworkTournamentLaunchResult>.Define(
                "tournament.spectate", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestSpectateTournament(header, intent.SessionId, intent.ExpectedRevision),
                request => request.Header, result => result.Header, ValidateSpectateWireShape, BuildSpectateCommandKey,
                ValidateAuthorityHeader, ExecuteSpectate, CreateLaunchTerminal, ProbeSpectateApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentSpectateTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedLaunchResult));
        missionEnteredRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentMissionEnteredIntent, NetworkTournamentMissionEntered,
                NetworkTournamentMissionEnteredResult>.Define(
                "tournament.mission-entered", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkTournamentMissionEntered(header, intent.SessionId, intent.ExpectedRevision,
                    intent.MissionInstanceId, intent.IsSpectator),
                request => request.Header, result => result.Header, ValidateMissionEnteredWireShape,
                BuildMissionEnteredCommandKey, ValidateAuthorityHeader, ExecuteMissionEntered,
                CreateMissionEnteredTerminal, ProbeMissionEnteredApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentMissionEnteredTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedMissionEnteredResult));
        choiceRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentChoiceIntent, NetworkRequestTournamentChoice, NetworkTournamentChoiceResult>.Define(
                "tournament.choice", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestTournamentChoice(header, intent.SessionId, intent.ExpectedRevision,
                    intent.BracketRevision, intent.MatchId, intent.Choice, intent.StructuralDigest),
                request => request.Header, result => result.Header, ValidateChoiceWireShape, BuildChoiceCommandKey,
                ValidateAuthorityHeader, ExecuteChoice, CreateChoiceTerminal, ProbeChoiceApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentChoiceTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedChoiceResult));
        betRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentBetIntent, NetworkRequestTournamentBet, NetworkTournamentBetResult>.Define(
                "tournament.bet", AuthorityRouteKind.Command, CreateAuthorityHeader,
                (intent, header) => new NetworkRequestTournamentBet(header, intent.SessionId, intent.ExpectedRevision,
                    intent.BracketRevision, intent.MatchId, intent.Amount, intent.Sequence, intent.Quote),
                request => request.Header, result => result.Header, ValidateBetWireShape, BuildBetCommandKey,
                ValidateAuthorityHeader, ExecuteBet, CreateBetTerminal, ProbeBetApplied,
                _ => TournamentStateSyncHandler.RequestCanonicalResync(), PresentBetTerminal,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedBetResult));

        messageBroker.Subscribe<NetworkRequestLeaveActiveTournament>(Handle_LeaveActive);
        messageBroker.Subscribe<NetworkTournamentBetState>(Handle_BetState);
        messageBroker.Subscribe<NetworkSubmitTournamentSpawnManifest>(Handle_SpawnManifest);
        messageBroker.Subscribe<NetworkSubmitTournamentMatchResult>(Handle_MatchResult);
        messageBroker.Subscribe<NetworkSubmitTournamentHitProgression>(Handle_HitProgression);
        messageBroker.Subscribe<NetworkTournamentSessionSnapshot>(Handle_Snapshot);
        messageBroker.Subscribe<NetworkTournamentSpawnManifest>(Handle_SpawnManifestSnapshot);
        messageBroker.Subscribe<NetworkEnterTournamentMission>(Handle_EnterMission);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_Disconnected);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    public void Dispose()
    {
        joinRoute.Dispose();
        leavePreparationRoute.Dispose();
        startRoute.Dispose();
        spectateRoute.Dispose();
        missionEnteredRoute.Dispose();
        choiceRoute.Dispose();
        betRoute.Dispose();
        messageBroker.Unsubscribe<NetworkRequestLeaveActiveTournament>(Handle_LeaveActive);
        messageBroker.Unsubscribe<NetworkTournamentBetState>(Handle_BetState);
        messageBroker.Unsubscribe<NetworkSubmitTournamentSpawnManifest>(Handle_SpawnManifest);
        messageBroker.Unsubscribe<NetworkSubmitTournamentMatchResult>(Handle_MatchResult);
        messageBroker.Unsubscribe<NetworkSubmitTournamentHitProgression>(Handle_HitProgression);
        messageBroker.Unsubscribe<NetworkTournamentSessionSnapshot>(Handle_Snapshot);
        messageBroker.Unsubscribe<NetworkTournamentSpawnManifest>(Handle_SpawnManifestSnapshot);
        messageBroker.Unsubscribe<NetworkEnterTournamentMission>(Handle_EnterMission);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_Disconnected);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    internal static AuthorityRequestTicket<NetworkTournamentJoinResult> SubmitJoin(
        string townId,
        string sessionId,
        long expectedRevision,
        Action<AuthorityClientOutcome<NetworkTournamentJoinResult>> completion = null) =>
        Instance?.joinRoute.Submit(new TournamentJoinIntent(townId, sessionId, expectedRevision), completion);

    internal static AuthorityRequestTicket<NetworkTournamentLeavePreparationResult> SubmitLeavePreparation(
        string sessionId,
        long expectedRevision,
        Action<AuthorityClientOutcome<NetworkTournamentLeavePreparationResult>> completion = null) =>
        Instance?.leavePreparationRoute.Submit(
            new TournamentLeavePreparationIntent(sessionId, expectedRevision), completion);

    internal static AuthorityRequestTicket<NetworkTournamentLaunchResult> SubmitStart(
        string sessionId, long expectedRevision,
        Action<AuthorityClientOutcome<NetworkTournamentLaunchResult>> completion = null) =>
        Instance?.startRoute.Submit(new TournamentLaunchIntent(sessionId, expectedRevision), completion);

    internal static AuthorityRequestTicket<NetworkTournamentLaunchResult> SubmitSpectate(
        string sessionId, long expectedRevision,
        Action<AuthorityClientOutcome<NetworkTournamentLaunchResult>> completion = null) =>
        Instance?.spectateRoute.Submit(new TournamentLaunchIntent(sessionId, expectedRevision), completion);

    internal static AuthorityRequestTicket<NetworkTournamentMissionEnteredResult> SubmitMissionEntered(
        string sessionId, long expectedRevision, string missionInstanceId, bool isSpectator,
        Action<AuthorityClientOutcome<NetworkTournamentMissionEnteredResult>> completion = null) =>
        Instance?.missionEnteredRoute.Submit(
            new TournamentMissionEnteredIntent(sessionId, expectedRevision, missionInstanceId, isSpectator), completion);

    internal static AuthorityRequestTicket<NetworkTournamentChoiceResult> SubmitChoice(
        TournamentSessionSnapshot snapshot, TournamentPlayerChoice choice,
        Action<AuthorityClientOutcome<NetworkTournamentChoiceResult>> completion = null) =>
        Instance?.choiceRoute.Submit(new TournamentChoiceIntent(snapshot.SessionId, snapshot.Revision,
            snapshot.BracketRevision, snapshot.CurrentMatchId, choice, TournamentAuthorityProtocol.StructuralDigest(snapshot)), completion);

    internal static AuthorityRequestTicket<NetworkTournamentBetResult> SubmitBet(
        TournamentSessionSnapshot snapshot, int amount, long sequence, TournamentBetQuote quote,
        Action<AuthorityClientOutcome<NetworkTournamentBetResult>> completion = null) =>
        Instance?.betRoute.Submit(new TournamentBetIntent(snapshot.SessionId, snapshot.Revision, snapshot.BracketRevision,
            snapshot.CurrentMatchId, amount, sequence, quote), completion);

    private AuthorityRequestHeader CreateAuthorityHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateAuthorityHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "tournament-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private static string ValidateJoinWireShape(NetworkRequestJoinTournament request)
    {
        if (!IsBoundedTournamentId(request.TownId) ||
            (!string.IsNullOrEmpty(request.SessionId) && !IsBoundedTournamentId(request.SessionId)) ||
            request.ExpectedRevision < 0)
            return "invalid-tournament-join";
        return null;
    }

    private static string BuildJoinCommandKey(NetworkRequestJoinTournament request) =>
        string.Concat(FieldKey(request.TownId), FieldKey(request.SessionId), request.ExpectedRevision.ToString());

    private AuthorityServerReply<NetworkTournamentJoinResult> ExecuteJoin(
        AuthorityServerContext context,
        NetworkRequestJoinTournament request)
    {
        Player player = context.Player;
        if (HasEnrollmentInAnotherTown(player.ControllerId, request.TownId))
            return JoinReply(context.Header, AuthorityResultStatus.Rejected, null, "already-enrolled");
        if (!TryResolvePlayerAtTown(player, request.TownId, out Town town, out Hero hero, out _))
            return JoinReply(context.Header, AuthorityResultStatus.Rejected, null, "player-not-at-town");

        if (!TryGetOrCreateJoinSession(town, request.TownId, out TournamentSessionSnapshot current, out string failure))
            return JoinReply(context.Header, AuthorityResultStatus.Unavailable, current, failure);
        if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId != current.SessionId)
        {
            SendCanonical(context.Peer, current);
            return JoinReply(context.Header, AuthorityResultStatus.StaleState, current, "stale-tournament-session", true);
        }
        if (!string.IsNullOrEmpty(request.SessionId) && request.ExpectedRevision != current.Revision)
        {
            SendCanonical(context.Peer, current);
            return JoinReply(context.Header, AuthorityResultStatus.StaleState, current, "stale-tournament-revision", true);
        }

        if (current.Phase != TournamentSessionPhase.Preparation)
        {
            TournamentMutationStatus spectate = sessionRegistry.TryRequestSpectate(
                current.SessionId, current.Revision, player.ControllerId, out TournamentSessionSnapshot spectatorSnapshot);
            if (spectate == TournamentMutationStatus.Applied)
            {
                BroadcastSnapshot(spectatorSnapshot);
                network.Send(context.Peer, new NetworkEnterTournamentMission(
                    context.Header, spectatorSnapshot, player.ControllerId, true));
                return JoinReply(context.Header, AuthorityResultStatus.Accepted, spectatorSnapshot, null, true);
            }
            SendCanonical(context.Peer, spectatorSnapshot ?? current);
            return JoinReply(context.Header, AuthorityResultStatus.Rejected, spectatorSnapshot ?? current,
                "spectate-not-available", true);
        }

        long expectedRevision = string.IsNullOrEmpty(request.SessionId) ? current.Revision : request.ExpectedRevision;
        TournamentMutationStatus status = sessionRegistry.TryJoin(
            current.SessionId, expectedRevision, player.ControllerId, player.CharacterObjectId,
            hero.Name?.ToString() ?? player.ControllerId, MBRandom.RandomInt(int.MaxValue), hero.IsLord,
            out TournamentSessionSnapshot snapshot);
        if (status == TournamentMutationStatus.Full)
        {
            SendCanonical(context.Peer, snapshot);
            return JoinReply(context.Header, AuthorityResultStatus.Rejected, snapshot, "tournament-full", true);
        }
        if (status == TournamentMutationStatus.StaleRevision)
        {
            SendCanonical(context.Peer, snapshot);
            return JoinReply(context.Header, AuthorityResultStatus.StaleState, snapshot, "stale-tournament-revision", true);
        }
        if (status != TournamentMutationStatus.Applied && status != TournamentMutationStatus.NoChange)
            return JoinReply(context.Header, AuthorityResultStatus.Rejected, snapshot, "join-rejected");

        if (status == TournamentMutationStatus.Applied) BroadcastSnapshot(snapshot);
        else SendCanonical(context.Peer, snapshot);
        return JoinReply(context.Header, AuthorityResultStatus.Accepted, snapshot, null, true);
    }

    private bool TryGetOrCreateJoinSession(Town town, string townId, out TournamentSessionSnapshot snapshot, out string failure)
    {
        failure = null;
        bool hasSession = sessionRegistry.TryGetByTown(townId, out snapshot);
        if (hasSession) return true;
        TournamentGame nativeGame = Campaign.Current?.TournamentManager?.GetTournamentGame(town);
        if (nativeGame == null) { failure = "tournament-not-found"; return false; }
        if (nativeGame.GetType() != typeof(FightTournamentGame)) { failure = "tournament-unsupported"; return false; }
        if (!tournamentGameInterface.TryFreezeTournament(town, (FightTournamentGame)nativeGame, out TournamentSessionSeed seed) ||
            sessionRegistry.TryCreate(seed, out snapshot) == TournamentMutationStatus.Rejected)
        { failure = "tournament-prepare-failed"; return false; }
        messageBroker.Publish(this, new TournamentNativeStateChanged());
        return true;
    }

    private static AuthorityServerReply<NetworkTournamentJoinResult> JoinReply(
        AuthorityRequestHeader header, AuthorityResultStatus status, TournamentSessionSnapshot snapshot,
        string reasonCode, bool statePublished = false) =>
        new AuthorityServerReply<NetworkTournamentJoinResult>(
            new NetworkTournamentJoinResult(header, status, snapshot, reasonCode), statePublished);

    private static NetworkTournamentJoinResult CreateJoinTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new NetworkTournamentJoinResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeJoinApplied(NetworkTournamentJoinResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.Snapshot == null) return AuthorityCommitProbeResult.Invalid;
        if (!sessionRegistry.TryGet(result.Snapshot.SessionId, out TournamentSessionSnapshot applied))
            return AuthorityCommitProbeResult.Pending;
        bool joinedOrSpectating = applied.Contestants.Any(contestant => contestant.IsHuman &&
            contestant.ControllerId == controllerIdProvider.ControllerId) ||
            applied.SpectatorControllerIds.Contains(controllerIdProvider.ControllerId);
        return applied.Revision >= result.Snapshot.Revision && joinedOrSpectating
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentJoinTerminal(AuthorityClientOutcome<NetworkTournamentJoinResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Tournament join ended without canonical membership. Completion={Completion}, Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }

    private static string ValidateLeavePreparationWireShape(NetworkRequestLeaveTournamentPreparation request) =>
        IsBoundedTournamentId(request.SessionId) && request.ExpectedRevision >= 0 ? null : "invalid-tournament-leave";

    private static string BuildLeavePreparationCommandKey(NetworkRequestLeaveTournamentPreparation request) =>
        string.Concat(FieldKey(request.SessionId), request.ExpectedRevision.ToString());

    private AuthorityServerReply<NetworkTournamentLeavePreparationResult> ExecuteLeavePreparation(
        AuthorityServerContext context, NetworkRequestLeaveTournamentPreparation request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current))
            return LeavePreparationReply(context.Header, AuthorityResultStatus.StaleState, null, null, null,
                "tournament-not-found");
        if (request.ExpectedRevision != current.Revision)
        {
            SendCanonical(context.Peer, current);
            return LeavePreparationReply(context.Header, AuthorityResultStatus.StaleState, current, null, null,
                "stale-tournament-revision", true);
        }

        TournamentMutationStatus status = sessionRegistry.TryLeavePreparation(request.SessionId, request.ExpectedRevision,
            context.Player.ControllerId, out TournamentSessionSnapshot snapshot, out bool removed);
        if (status == TournamentMutationStatus.StaleRevision)
        {
            SendCanonical(context.Peer, snapshot ?? current);
            return LeavePreparationReply(context.Header, AuthorityResultStatus.StaleState, snapshot ?? current, null, null,
                "stale-tournament-revision", true);
        }
        if (status != TournamentMutationStatus.Applied)
        {
            SendCanonical(context.Peer, snapshot ?? current);
            return LeavePreparationReply(context.Header, AuthorityResultStatus.Rejected, snapshot ?? current, null, null,
                "leave-not-member", true);
        }
        if (removed)
        {
            network.SendAll(new NetworkTournamentSessionRemoved(current.SessionId, current.TownId));
            messageBroker.Publish(this, new TournamentSessionRemoved(current.SessionId, current.TownId));
            return LeavePreparationReply(context.Header, AuthorityResultStatus.Accepted, null, current.SessionId, current.TownId,
                null, true);
        }

        BroadcastSnapshot(snapshot);
        return LeavePreparationReply(context.Header, AuthorityResultStatus.Accepted, snapshot, null, null, null, true);
    }

    private static AuthorityServerReply<NetworkTournamentLeavePreparationResult> LeavePreparationReply(
        AuthorityRequestHeader header, AuthorityResultStatus status, TournamentSessionSnapshot snapshot,
        string removedSessionId, string removedTownId, string reasonCode, bool statePublished = false) =>
        new AuthorityServerReply<NetworkTournamentLeavePreparationResult>(
            new NetworkTournamentLeavePreparationResult(header, status, snapshot, removedSessionId, removedTownId, reasonCode),
            statePublished);

    private static NetworkTournamentLeavePreparationResult CreateLeavePreparationTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new NetworkTournamentLeavePreparationResult(header, status, null, null, null, reasonCode);

    private AuthorityCommitProbeResult ProbeLeavePreparationApplied(NetworkTournamentLeavePreparationResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Invalid;
        if (!string.IsNullOrEmpty(result.RemovedSessionId))
            return sessionRegistry.TryGet(result.RemovedSessionId, out _)
                ? AuthorityCommitProbeResult.Pending : AuthorityCommitProbeResult.Applied;
        if (result.Snapshot == null || !sessionRegistry.TryGet(result.Snapshot.SessionId, out TournamentSessionSnapshot applied))
            return AuthorityCommitProbeResult.Pending;
        bool absent = !applied.Contestants.Any(contestant => contestant.IsHuman &&
            contestant.ControllerId == controllerIdProvider.ControllerId);
        return applied.Revision >= result.Snapshot.Revision && absent
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentLeavePreparationTerminal(AuthorityClientOutcome<NetworkTournamentLeavePreparationResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Tournament preparation leave ended without canonical removal. Completion={Completion}, Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }

    private static bool IsBoundedTournamentId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static string FieldKey(string value) => $"{value?.Length ?? -1}:{value ?? string.Empty}";

    private readonly struct TournamentJoinIntent
    {
        public TournamentJoinIntent(string townId, string sessionId, long expectedRevision) =>
            (TownId, SessionId, ExpectedRevision) = (townId, sessionId, expectedRevision);
        public string TownId { get; }
        public string SessionId { get; }
        public long ExpectedRevision { get; }
    }

    private readonly struct TournamentLeavePreparationIntent
    {
        public TournamentLeavePreparationIntent(string sessionId, long expectedRevision) =>
            (SessionId, ExpectedRevision) = (sessionId, expectedRevision);
        public string SessionId { get; }
        public long ExpectedRevision { get; }
    }

    private readonly struct TournamentLaunchIntent
    {
        public TournamentLaunchIntent(string sessionId, long expectedRevision) =>
            (SessionId, ExpectedRevision) = (sessionId, expectedRevision);
        public string SessionId { get; }
        public long ExpectedRevision { get; }
    }

    private readonly struct TournamentMissionEnteredIntent
    {
        public TournamentMissionEnteredIntent(string sessionId, long expectedRevision, string missionInstanceId,
            bool isSpectator) => (SessionId, ExpectedRevision, MissionInstanceId, IsSpectator) =
            (sessionId, expectedRevision, missionInstanceId, isSpectator);
        public string SessionId { get; }
        public long ExpectedRevision { get; }
        public string MissionInstanceId { get; }
        public bool IsSpectator { get; }
    }

    private readonly struct TournamentChoiceIntent
    {
        public TournamentChoiceIntent(string sessionId, long expectedRevision, long bracketRevision, string matchId,
            TournamentPlayerChoice choice, string structuralDigest) =>
            (SessionId, ExpectedRevision, BracketRevision, MatchId, Choice, StructuralDigest) =
            (sessionId, expectedRevision, bracketRevision, matchId, choice, structuralDigest);
        public string SessionId { get; }
        public long ExpectedRevision { get; }
        public long BracketRevision { get; }
        public string MatchId { get; }
        public TournamentPlayerChoice Choice { get; }
        public string StructuralDigest { get; }
    }

    private readonly struct TournamentBetIntent
    {
        public TournamentBetIntent(string sessionId, long expectedRevision, long bracketRevision, string matchId,
            int amount, long sequence, TournamentBetQuote quote) =>
            (SessionId, ExpectedRevision, BracketRevision, MatchId, Amount, Sequence, Quote) =
            (sessionId, expectedRevision, bracketRevision, matchId, amount, sequence, quote);
        public string SessionId { get; }
        public long ExpectedRevision { get; }
        public long BracketRevision { get; }
        public string MatchId { get; }
        public int Amount { get; }
        public long Sequence { get; }
        public TournamentBetQuote Quote { get; }
    }

    private bool HasEnrollmentInAnotherTown(string controllerId, string townId)
    {
        return sessionRegistry.GetAll().Any(session =>
            session.TownId != townId &&
            session.Contestants.Any(contestant =>
                contestant.IsHuman && contestant.ControllerId == controllerId));
    }

    private static string ValidateStartWireShape(NetworkRequestStartTournament request) =>
        IsBoundedTournamentId(request.SessionId) && request.ExpectedRevision >= 0 ? null : "invalid-tournament-start";

    private static string BuildStartCommandKey(NetworkRequestStartTournament request) =>
        string.Concat(FieldKey(request.SessionId), request.ExpectedRevision.ToString());

    private static string ValidateSpectateWireShape(NetworkRequestSpectateTournament request) =>
        IsBoundedTournamentId(request.SessionId) && request.ExpectedRevision >= 0 ? null : "invalid-tournament-spectate";

    private static string BuildSpectateCommandKey(NetworkRequestSpectateTournament request) =>
        string.Concat(FieldKey(request.SessionId), request.ExpectedRevision.ToString());

    private AuthorityServerReply<NetworkTournamentLaunchResult> ExecuteStart(
        AuthorityServerContext context, NetworkRequestStartTournament request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current) ||
            request.ExpectedRevision != current.Revision || current.Phase != TournamentSessionPhase.Preparation ||
            !TryResolvePlayerAtTown(context.Player, current.TownId, out _, out _, out _) ||
            !TryGetPlayerSlot(current, context.Player.ControllerId, out _) ||
            Campaign.Current?.SaveHandler?.IsSaving == true)
        {
            SendCanonical(context.Peer, current);
            return LaunchReply(context.Header, AuthorityResultStatus.StaleState, current,
                context.Player.ControllerId, false, "stale-tournament-start", true);
        }

        if (!tournamentGameInterface.TryCreateBracket(current, out TournamentBracketUpdate bracket) ||
            bracket?.Rounds == null || string.IsNullOrEmpty(bracket.CurrentMatchId))
            return LaunchReply(context.Header, AuthorityResultStatus.Rejected, current,
                context.Player.ControllerId, false, "invalid-tournament-bracket");

        bool irreversible = false;
        string stage = "locked-prize";
        try
        {
            // From this point prize and roster state may no longer be safely rolled back.
            irreversible = true;
            if (!tournamentGameInterface.TryApplyLockedPrize(current))
                throw new InvalidOperationException("Could not apply locked prize.");
            stage = "registry-start";
            TournamentMutationStatus status = sessionRegistry.TryStart(request.SessionId, request.ExpectedRevision,
                context.Player.ControllerId, bracket.Rounds, bracket.CurrentMatchId, out TournamentSessionSnapshot snapshot);
            if (status != TournamentMutationStatus.Applied || snapshot == null)
                throw new InvalidOperationException("Tournament start did not commit: " + status);
            stage = "native-state-publication";
            messageBroker.Publish(this, new TournamentNativeStateChanged());
            stage = "snapshot-publication";
            BroadcastSnapshot(snapshot);
            stage = "mission-launch";
            network.SendAll(new NetworkEnterTournamentMission(context.Header, snapshot, context.Player.ControllerId, false));
            return LaunchReply(context.Header, AuthorityResultStatus.Accepted, snapshot,
                context.Player.ControllerId, false, null, true);
        }
        catch (Exception exception)
        {
            if (!irreversible) throw;
            Logger.Fatal(exception, "[Tournament] irreversible start failed at {Stage}; session={SessionId}, request={RequestId}",
                stage, current.SessionId, context.Header.RequestId);
            DisconnectAllTournamentClients();
            return LaunchReply(context.Header, AuthorityResultStatus.ExecutionFailed, null,
                context.Player.ControllerId, false, "tournament-start-fatal", false, suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkTournamentLaunchResult> ExecuteSpectate(
        AuthorityServerContext context, NetworkRequestSpectateTournament request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current) ||
            !TryResolvePlayerAtTown(context.Player, current.TownId, out _, out _, out _))
            return LaunchReply(context.Header, AuthorityResultStatus.StaleState, current,
                context.Player.ControllerId, true, "stale-tournament-session");

        TournamentMutationStatus status = sessionRegistry.TryRequestSpectate(request.SessionId, request.ExpectedRevision,
            context.Player.ControllerId, out TournamentSessionSnapshot snapshot);
        TournamentSessionSnapshot canonical = snapshot ?? current;
        LogSpectatorRequest(context.Player.ControllerId, status, canonical);
        if ((status != TournamentMutationStatus.Applied && status != TournamentMutationStatus.NoChange) ||
            (status == TournamentMutationStatus.NoChange &&
             !IsSpectatorRole(canonical, context.Player.ControllerId)))
        {
            SendCanonical(context.Peer, canonical);
            return LaunchReply(context.Header,
                status == TournamentMutationStatus.StaleRevision ? AuthorityResultStatus.StaleState : AuthorityResultStatus.Rejected,
                canonical, context.Player.ControllerId, true, "spectate-not-available", true);
        }

        if (status == TournamentMutationStatus.Applied) BroadcastSnapshot(canonical);
        else SendCanonical(context.Peer, canonical);
        network.Send(context.Peer, new NetworkEnterTournamentMission(context.Header, canonical,
            context.Player.ControllerId, true));
        return LaunchReply(context.Header, AuthorityResultStatus.Accepted, canonical,
            context.Player.ControllerId, true, null, true);
    }

    private static NetworkTournamentLaunchResult CreateLaunchTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new(header, status, null, null, false, reasonCode);

    private static AuthorityServerReply<NetworkTournamentLaunchResult> LaunchReply(
        AuthorityRequestHeader header, AuthorityResultStatus status, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator, string reasonCode, bool statePublished = false,
        bool suppressReply = false) => new(new NetworkTournamentLaunchResult(header, status, snapshot,
            requesterControllerId, isSpectator, reasonCode), statePublished, suppressReply);

    private AuthorityCommitProbeResult ProbeStartApplied(NetworkTournamentLaunchResult result) =>
        ProbeLaunchApplied(result, expectSpectator: false);

    private AuthorityCommitProbeResult ProbeSpectateApplied(NetworkTournamentLaunchResult result) =>
        ProbeLaunchApplied(result, expectSpectator: true);

    private AuthorityCommitProbeResult ProbeLaunchApplied(NetworkTournamentLaunchResult result, bool expectSpectator)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.Snapshot == null ||
            result.IsSpectator != expectSpectator || result.Snapshot.Phase == TournamentSessionPhase.Preparation ||
            result.Snapshot.Revision != result.Header.CommittedRevision ||
            result.RequesterControllerId != controllerIdProvider.ControllerId ||
            !sessionRegistry.TryGet(result.TournamentSessionId, out TournamentSessionSnapshot applied))
            return AuthorityCommitProbeResult.Invalid;
        bool requesterHasRole = expectSpectator
            ? IsSpectatorRole(result.Snapshot, result.RequesterControllerId)
            : TryGetPlayerSlot(result.Snapshot, result.RequesterControllerId, out _);
        if (!requesterHasRole)
            return AuthorityCommitProbeResult.Invalid;
        if (applied.Revision < result.Header.CommittedRevision)
            return AuthorityCommitProbeResult.Pending;
        if (applied.Revision != result.Header.CommittedRevision || applied.MissionInstanceId != result.MissionInstanceId)
            return AuthorityCommitProbeResult.Invalid;
        bool member = expectSpectator
            ? applied.SpectatorControllerIds.Contains(controllerIdProvider.ControllerId)
            : TryGetPlayerSlot(applied, controllerIdProvider.ControllerId, out _);
        return member ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static bool IsExpectedLaunchResult(NetworkRequestStartTournament request, NetworkTournamentLaunchResult result) =>
        IsExpectedLaunchResult(request.Header, request.SessionId, result, false);

    private static bool IsExpectedLaunchResult(NetworkRequestSpectateTournament request, NetworkTournamentLaunchResult result) =>
        IsExpectedLaunchResult(request.Header, request.SessionId, result, true);

    private static bool IsExpectedLaunchResult(AuthorityRequestHeader header, string sessionId,
        NetworkTournamentLaunchResult result, bool isSpectator) =>
        result.Status != AuthorityResultStatus.Accepted ||
        (result.SessionId == header.SessionId && result.AuthorityRequestId == header.RequestId &&
         result.TournamentSessionId == sessionId && result.MissionInstanceId == result.Snapshot?.MissionInstanceId &&
         result.IsSpectator == isSpectator);

    private void PresentStartTerminal(AuthorityClientOutcome<NetworkTournamentLaunchResult> outcome) =>
        PresentLaunchTerminal(outcome, false);

    private void PresentSpectateTerminal(AuthorityClientOutcome<NetworkTournamentLaunchResult> outcome) =>
        PresentLaunchTerminal(outcome, true);

    private void PresentLaunchTerminal(AuthorityClientOutcome<NetworkTournamentLaunchResult> outcome, bool isSpectator)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied || outcome.Result.IsSpectator != isSpectator)
        {
            Logger.Warning("Tournament launch ended without replica presentation. Completion={Completion}, Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
            TournamentStateSyncHandler.RequestCanonicalResync();
            return;
        }
        TryOpenPendingMissionLaunch(outcome.Result.SessionId, outcome.Result.AuthorityRequestId);
    }

    private static string ValidateMissionEnteredWireShape(NetworkTournamentMissionEntered request) =>
        IsBoundedTournamentId(request.SessionId) && IsBoundedTournamentId(request.MissionInstanceId) &&
        request.ExpectedRevision >= 0 ? null : "invalid-tournament-mission-entry";

    private static string BuildMissionEnteredCommandKey(NetworkTournamentMissionEntered request) =>
        string.Concat(FieldKey(request.SessionId), FieldKey(request.MissionInstanceId), request.ExpectedRevision.ToString(),
            request.IsSpectator ? "1" : "0");

    private AuthorityServerReply<NetworkTournamentMissionEnteredResult> ExecuteMissionEntered(
        AuthorityServerContext context, NetworkTournamentMissionEntered request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current))
            return MissionEnteredReply(context.Header, AuthorityResultStatus.StaleState, null,
                context.Player.ControllerId, request.IsSpectator, "tournament-not-found");
        if (current.MissionInstanceId != request.MissionInstanceId)
            return MissionEnteredReply(context.Header, AuthorityResultStatus.InvalidRequest, current,
                context.Player.ControllerId, request.IsSpectator, "invalid-mission-correlation");

        bool isCompetitor = TryGetPlayerSlot(current, context.Player.ControllerId, out _);
        bool actualSpectator = IsSpectatorRole(current, context.Player.ControllerId);
        if (!IsActiveSession(current) || (!isCompetitor && !actualSpectator) ||
            actualSpectator != request.IsSpectator)
            return MissionEnteredReply(context.Header, AuthorityResultStatus.InvalidRequest, current,
                context.Player.ControllerId, request.IsSpectator, "invalid-mission-role");

        TournamentMutationStatus status = sessionRegistry.TryEnterMission(request.SessionId, request.ExpectedRevision,
            context.Player.ControllerId, out TournamentSessionSnapshot snapshot);
        TournamentSessionSnapshot canonical = snapshot ?? current;
        bool semanticStale = status == TournamentMutationStatus.StaleRevision && IsActiveSession(canonical) &&
            canonical.MissionInstanceId == request.MissionInstanceId &&
            IsConfirmedEntrant(canonical, context.Player.ControllerId) &&
            IsSpectatorRole(canonical, context.Player.ControllerId) == request.IsSpectator;
        if (status == TournamentMutationStatus.Applied || status == TournamentMutationStatus.NoChange || semanticStale)
        {
            if (status == TournamentMutationStatus.Applied) BroadcastSnapshot(canonical);
            else SendCanonical(context.Peer, canonical);
            if (sessionRegistry.TryGetSpawnManifest(canonical.SessionId, out TournamentSpawnManifestData manifest))
                network.Send(context.Peer, new NetworkTournamentSpawnManifest(manifest));
            return MissionEnteredReply(context.Header, AuthorityResultStatus.Accepted, canonical,
                context.Player.ControllerId, request.IsSpectator, null, true);
        }

        SendCanonical(context.Peer, canonical);
        return MissionEnteredReply(context.Header, AuthorityResultStatus.StaleState, canonical,
            context.Player.ControllerId, request.IsSpectator, "stale-tournament-revision", true);
    }

    private static NetworkTournamentMissionEnteredResult CreateMissionEnteredTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new(header, status, null, null, false, reasonCode);

    private static AuthorityServerReply<NetworkTournamentMissionEnteredResult> MissionEnteredReply(
        AuthorityRequestHeader header, AuthorityResultStatus status, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator, string reasonCode, bool statePublished = false) =>
        new(new NetworkTournamentMissionEnteredResult(header, status, snapshot, requesterControllerId,
            isSpectator, reasonCode), statePublished);

    private AuthorityCommitProbeResult ProbeMissionEnteredApplied(NetworkTournamentMissionEnteredResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.Snapshot == null ||
            result.Snapshot.Revision != result.Header.CommittedRevision ||
            result.RequesterControllerId != controllerIdProvider.ControllerId ||
            !sessionRegistry.TryGet(result.TournamentSessionId, out TournamentSessionSnapshot applied))
            return AuthorityCommitProbeResult.Invalid;
        if (applied.Revision < result.Header.CommittedRevision)
            return AuthorityCommitProbeResult.Pending;
        if (applied.Revision != result.Header.CommittedRevision || !IsActiveSession(applied) ||
            applied.MissionInstanceId != result.MissionInstanceId)
            return AuthorityCommitProbeResult.Invalid;
        bool exactEntrant = IsConfirmedEntrant(applied, controllerIdProvider.ControllerId);
        bool actualSpectator = IsSpectatorRole(applied, controllerIdProvider.ControllerId);
        return exactEntrant && actualSpectator == result.IsSpectator
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static bool IsExpectedMissionEnteredResult(NetworkTournamentMissionEntered request,
        NetworkTournamentMissionEnteredResult result) => result.Status != AuthorityResultStatus.Accepted ||
        (result.SessionId == request.Header.SessionId && result.AuthorityRequestId == request.Header.RequestId &&
         result.TournamentSessionId == request.SessionId && result.MissionInstanceId == request.MissionInstanceId &&
         result.IsSpectator == request.IsSpectator);

    private static void PresentMissionEnteredTerminal(AuthorityClientOutcome<NetworkTournamentMissionEnteredResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
        {
            Logger.Warning("Tournament mission entry ended without canonical entrant membership. Completion={Completion}, Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
            TournamentStateSyncHandler.RequestCanonicalResync();
        }
    }

    private static string ValidateChoiceWireShape(NetworkRequestTournamentChoice request) =>
        IsBoundedTournamentId(request.SessionId) && IsBoundedTournamentId(request.MatchId) &&
        request.ExpectedRevision >= 0 && request.BracketRevision >= 0 &&
        TournamentAuthorityProtocol.IsBoundedDigest(request.StructuralDigest) ? null : "invalid-tournament-choice";

    private static string BuildChoiceCommandKey(NetworkRequestTournamentChoice request) => string.Concat(
        FieldKey(request.SessionId), request.ExpectedRevision.ToString(), request.BracketRevision.ToString(),
        FieldKey(request.MatchId), ((int)request.Choice).ToString(), FieldKey(request.StructuralDigest));

    private AuthorityServerReply<NetworkTournamentChoiceResult> ExecuteChoice(
        AuthorityServerContext context, NetworkRequestTournamentChoice request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current))
            return ChoiceReply(context.Header, AuthorityResultStatus.StaleState, null, request, context.Player.ControllerId,
                TournamentBallotOutcome.Open, "tournament-not-found");

        bool sameEpoch = current.Phase == TournamentSessionPhase.AwaitingChoices &&
            current.CurrentMatchId == request.MatchId && current.BracketRevision == request.BracketRevision &&
            IsVoter(current, context.Player.ControllerId) &&
            TournamentAuthorityProtocol.StructuralDigest(current) == request.StructuralDigest;
        if (!sameEpoch)
        {
            SendCanonical(context.Peer, current);
            return ChoiceReply(context.Header, AuthorityResultStatus.StaleState, current, request, context.Player.ControllerId,
                TournamentBallotOutcome.Open, "stale-tournament-choice", true);
        }

        long revision = request.ExpectedRevision == current.Revision ? request.ExpectedRevision : current.Revision;
        TournamentMutationStatus status = sessionRegistry.TryChoose(request.SessionId, revision, request.MatchId,
            context.Player.ControllerId, request.Choice, out TournamentSessionSnapshot changed, out TournamentBallotOutcome outcome);
        TournamentSessionSnapshot canonical = changed ?? current;
        if (status == TournamentMutationStatus.Applied && outcome == TournamentBallotOutcome.SimulateMatch)
            canonical = SimulateCurrentMatchAndAdvance(canonical, context.Player.ControllerId);

        if (status == TournamentMutationStatus.Applied || status == TournamentMutationStatus.NoChange)
        {
            // A correlated canonical snapshot is always emitted before the Accepted terminal reply.
            if (status == TournamentMutationStatus.Applied) BroadcastSnapshot(canonical);
            else SendCanonical(context.Peer, canonical);
            return ChoiceReply(context.Header, AuthorityResultStatus.Accepted, canonical, request, context.Player.ControllerId,
                outcome, null, true);
        }

        SendCanonical(context.Peer, canonical);
        return ChoiceReply(context.Header, AuthorityResultStatus.StaleState, canonical, request, context.Player.ControllerId,
            outcome, "stale-tournament-choice", true);
    }

    private static AuthorityServerReply<NetworkTournamentChoiceResult> ChoiceReply(AuthorityRequestHeader header,
        AuthorityResultStatus status, TournamentSessionSnapshot snapshot, NetworkRequestTournamentChoice request,
        string controllerId, TournamentBallotOutcome outcome, string reasonCode, bool statePublished = false) => new(
        new NetworkTournamentChoiceResult(header, status, snapshot, request.Choice, outcome, controllerId, reasonCode),
        statePublished);

    private static NetworkTournamentChoiceResult CreateChoiceTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reasonCode) => new(header, status, null, default,
        TournamentBallotOutcome.Open, null, reasonCode);

    private AuthorityCommitProbeResult ProbeChoiceApplied(NetworkTournamentChoiceResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.Snapshot == null ||
            result.ControllerId != controllerIdProvider.ControllerId ||
            !sessionRegistry.TryGet(result.TournamentSessionId, out TournamentSessionSnapshot applied))
            return AuthorityCommitProbeResult.Pending;
        if (applied.Revision != result.CommittedRevision || applied.CurrentMatchId != result.MatchId ||
            applied.BracketRevision != result.BracketRevision ||
            TournamentAuthorityProtocol.StructuralDigest(applied) != result.StructuralDigest)
            return AuthorityCommitProbeResult.Invalid;
        bool choicePresent = applied.Choices.Any(choice => choice.ControllerId == controllerIdProvider.ControllerId &&
            choice.Choice == result.Choice);
        bool outcomeChangedEpoch = result.Outcome != TournamentBallotOutcome.Open;
        return choicePresent || outcomeChangedEpoch ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static bool IsExpectedChoiceResult(NetworkRequestTournamentChoice request,
        NetworkTournamentChoiceResult result) => result.Status != AuthorityResultStatus.Accepted ||
        (result.ConfigSessionId == request.ConfigSessionId && result.AuthorityRequestId == request.AuthorityRequestId &&
         result.TournamentSessionId == request.SessionId && result.Choice == request.Choice &&
         result.Snapshot != null && result.Snapshot.Revision == result.CommittedRevision);

    private static void PresentChoiceTerminal(AuthorityClientOutcome<NetworkTournamentChoiceResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        Logger.Warning("Tournament choice ended without an applied canonical ballot. Completion={Completion}, Reason={Reason}; reselect after refresh.",
            outcome.Completion, outcome.ReasonCode);
        TournamentStateSyncHandler.RequestCanonicalResync();
    }

    private static bool IsVoter(TournamentSessionSnapshot snapshot, string controllerId) =>
        TryGetPlayerSlot(snapshot, controllerId, out _) || IsSpectatorRole(snapshot, controllerId);

    private void Handle_LeaveActive(MessagePayload<NetworkRequestLeaveActiveTournament> payload)
    {
        if (ModInformation.IsClient || !TryAuthenticate(payload.Who, out var peer, out var player))
            return;

        GameThread.RunSafe(() => LeaveActive(
            payload.What.SessionId,
            payload.What.ExpectedRevision,
            player.ControllerId,
            peer), context: nameof(Handle_LeaveActive));
    }

    private void Handle_SpawnManifest(MessagePayload<NetworkSubmitTournamentSpawnManifest> payload)
    {
        if (ModInformation.IsClient || !TryAuthenticate(payload.Who, out var peer, out var player))
            return;

        GameThread.RunSafe(() =>
        {
            TournamentSpawnManifestData manifest = payload.What.Manifest;
            TournamentSessionSnapshot current = null;
            bool validManifest = manifest != null &&
                sessionRegistry.TryGet(manifest.SessionId, out current) &&
                current.Phase == TournamentSessionPhase.LiveMatch &&
                current.HostControllerId == player.ControllerId &&
                manifest.Revision == current.Revision &&
                manifest.BracketRevision == current.BracketRevision &&
                manifest.MatchId == current.CurrentMatchId &&
                TournamentSpawnManifestValidator.IsValid(manifest, current);
            bool validObjects = validManifest && HasValidManifestObjects(manifest);
            if (!validManifest || !validObjects)
            {
                Logger.Warning(
                    "[Tournament] Rejected spawn manifest session={SessionId}, controller={ControllerId}, " +
                    "phase={Phase}, revision={ManifestRevision}/{CurrentRevision}, " +
                    "bracket={ManifestBracket}/{CurrentBracket}, match={ManifestMatch}/{CurrentMatch}, " +
                    "structure={ValidManifest}, objects={ValidObjects}",
                    manifest?.SessionId,
                    player.ControllerId,
                    current?.Phase,
                    manifest?.Revision,
                    current?.Revision,
                    manifest?.BracketRevision,
                    current?.BracketRevision,
                    manifest?.MatchId,
                    current?.CurrentMatchId,
                    validManifest,
                    validObjects);
                SendCanonical(peer, current);
                return;
            }

            var status = sessionRegistry.TryStoreSpawnManifest(manifest, player.ControllerId, out var snapshot);
            if (status == TournamentMutationStatus.Applied)
            {
                var message = new NetworkTournamentSpawnManifest(payload.What.Manifest);
                network.SendAll(message);
                messageBroker.Publish(this, new TournamentSpawnManifestUpdated(payload.What.Manifest));
            }
            else
            {
                SendCanonical(peer, snapshot);
            }
        }, context: nameof(Handle_SpawnManifest));
    }

    private void Handle_MatchResult(MessagePayload<NetworkSubmitTournamentMatchResult> payload)
    {
        if (ModInformation.IsClient || !TryAuthenticate(payload.Who, out var peer, out var player))
            return;

        GameThread.RunSafe(() =>
        {
            TournamentMatchResultData result = payload.What.Result;
            TournamentSessionSnapshot current = null;
            TournamentSpawnManifestData manifest = null;
            if (result == null ||
                !sessionRegistry.TryGet(result.SessionId, out current) ||
                current.Phase != TournamentSessionPhase.LiveMatch ||
                current.HostControllerId != player.ControllerId ||
                !sessionRegistry.TryGetSpawnManifest(result.SessionId, out manifest) ||
                manifest.MatchId != current.CurrentMatchId ||
                manifest.BracketRevision != current.BracketRevision ||
                manifest.Revision > current.Revision ||
                result.WinnerTeamIds == null ||
                result.WinnerSlotIds == null ||
                result.TeamScores == null ||
                result.TeamScores.Any(score => score == null) ||
                result.Revision != current.Revision ||
                result.BracketRevision != current.BracketRevision ||
                result.MatchId != current.CurrentMatchId)
            {
                Logger.Warning(
                    "[Tournament] Rejected live match result before bracket advance session={SessionId}, match={ResultMatch}/{CurrentMatch}, controller={ControllerId}, revision={ResultRevision}/{CurrentRevision}, bracket={ResultBracket}/{CurrentBracket}, manifest={ManifestMatch}",
                    result?.SessionId,
                    result?.MatchId,
                    current?.CurrentMatchId,
                    player.ControllerId,
                    result?.Revision,
                    current?.Revision,
                    result?.BracketRevision,
                    current?.BracketRevision,
                    manifest?.MatchId);
                SendCanonical(peer, current);
                return;
            }

            if (!tournamentGameInterface.TryAdvanceBracket(current, result, out var bracket))
            {
                Logger.Warning(
                    "[Tournament] Failed to advance live bracket session={SessionId}, match={MatchId}, winners={WinnerSlots}, scores={Scores}",
                    result.SessionId,
                    result.MatchId,
                    string.Join(",", result.WinnerSlotIds),
                    string.Join(",", result.TeamScores.Select(score => $"{score.TeamId}:{score.Score}")));
                SendCanonical(peer, current);
                return;
            }

            IReadOnlyDictionary<string, int> contestantScores =
                TournamentStateReconciliation.ReconcileContestantScores(
                    current,
                    bracket.ContestantScores,
                    out bool scoresChanged);
            Logger.Information(
                "[Tournament] Committing live result session={SessionId}, match={MatchId}: sequence={Sequence}, candidateScores={CandidateScoreCount}, canonicalScores={CanonicalScoreCount}, corrected={ScoresChanged}",
                result.SessionId,
                result.MatchId,
                result.Sequence,
                bracket.ContestantScores.Count,
                contestantScores.Count,
                scoresChanged);
            var status = sessionRegistry.TryApplyMatchResult(
                result,
                player.ControllerId,
                bracket.Rounds,
                contestantScores,
                bracket.CurrentMatchId,
                bracket.WinnerSlotId,
                bracket.IsCompleted,
                out var snapshot);
            if (status != TournamentMutationStatus.Applied)
            {
                Logger.Warning(
                    "[Tournament] Match result registry rejected session={SessionId}, match={MatchId}, status={Status}, sequence={Sequence}",
                    result.SessionId,
                    result.MatchId,
                    status,
                    result.Sequence);
                SendCanonical(peer, snapshot);
                return;
            }

            ResolveBets(current, bracket.MatchWinnerSlotIds);
            BroadcastSnapshot(snapshot);
            if (bracket.IsCompleted)
                CompleteTournament(snapshot);
        }, context: nameof(Handle_MatchResult));
    }

    private void Handle_Snapshot(MessagePayload<NetworkTournamentSessionSnapshot> payload)
    {
        if (ModInformation.IsServer ||
            !TournamentServerMessageGuard.IsTrusted(payload.Who))
            return;

        GameThread.RunSafe(() =>
        {
            TournamentSessionSnapshot snapshot = TournamentSessionSnapshotNormalizer.Normalize(payload.What.Snapshot);
            if (sessionRegistry.ApplySnapshot(snapshot))
            {
                messageBroker.Publish(this, new TournamentSessionUpdated(snapshot));
                TryOpenPendingMissionLaunches(snapshot);
            }
        }, context: nameof(Handle_Snapshot));
    }

    private void Handle_SpawnManifestSnapshot(MessagePayload<NetworkTournamentSpawnManifest> payload)
    {
        if (ModInformation.IsServer ||
            !TournamentServerMessageGuard.IsTrusted(payload.Who))
            return;

        GameThread.RunSafe(
            () => messageBroker.Publish(this, new TournamentSpawnManifestUpdated(payload.What.Manifest)),
            context: nameof(Handle_SpawnManifestSnapshot));
    }

    private void Handle_EnterMission(MessagePayload<NetworkEnterTournamentMission> payload)
    {
        if (ModInformation.IsServer ||
            !TournamentServerMessageGuard.IsTrusted(payload.Who))
            return;

        GameThread.RunSafe(() =>
        {
            TournamentSessionSnapshot snapshot = TournamentSessionSnapshotNormalizer.Normalize(payload.What.Snapshot);
            if (snapshot == null || snapshot.SessionId != payload.What.TournamentSessionId ||
                snapshot.MissionInstanceId != payload.What.MissionInstanceId ||
                string.IsNullOrEmpty(payload.What.ConfigSessionId) || payload.What.AuthorityRequestId <= 0 ||
                string.IsNullOrEmpty(payload.What.RequesterControllerId))
            {
                Logger.Warning("Ignoring invalid tournament mission launch correlation.");
                return;
            }
            if (!payload.What.IsSpectator && !snapshot.Contestants.Any(contestant =>
                    contestant.IsHuman && contestant.ControllerId == controllerIdProvider.ControllerId))
            {
                return;
            }
            if (payload.What.IsSpectator && !snapshot.SpectatorControllerIds.Contains(controllerIdProvider.ControllerId))
                return;

            pendingMissionLaunches[MissionLaunchKey(payload.What)] = payload.What;
            TryOpenPendingMissionLaunch(payload.What.ConfigSessionId, payload.What.AuthorityRequestId);
        }, context: nameof(Handle_EnterMission));
    }

    private void TryOpenPendingMissionLaunches(TournamentSessionSnapshot applied)
    {
        foreach (NetworkEnterTournamentMission launch in pendingMissionLaunches.Values
                     .Where(candidate => candidate.TournamentSessionId == applied.SessionId &&
                         candidate.MissionInstanceId == applied.MissionInstanceId).ToArray())
            TryOpenPendingMissionLaunch(launch.ConfigSessionId, launch.AuthorityRequestId);
    }

    private void TryOpenPendingMissionLaunch(string configSessionId, long authorityRequestId)
    {
        NetworkEnterTournamentMission launch = pendingMissionLaunches.Values.FirstOrDefault(candidate =>
            candidate.ConfigSessionId == configSessionId && candidate.AuthorityRequestId == authorityRequestId);
        if (launch.Snapshot == null || !sessionRegistry.TryGet(launch.TournamentSessionId, out TournamentSessionSnapshot applied) ||
            applied.Revision != launch.Snapshot.Revision || applied.MissionInstanceId != launch.MissionInstanceId)
            return;

        string key = MissionLaunchKey(launch);
        if (openedMissionLaunches.Contains(key)) return;
        if (!ContainerProvider.TryResolve(out ICoopTournamentLauncher launcher))
        {
            Logger.Error("Could not resolve {Launcher}", nameof(ICoopTournamentLauncher));
            return;
        }
        if (launcher.OpenCoopTournament(applied, launch.IsSpectator) == null) return;
        openedMissionLaunches.Add(key);
        pendingMissionLaunches.Remove(key);
    }

    private static string MissionLaunchKey(NetworkEnterTournamentMission launch) =>
        string.Concat(FieldKey(launch.ConfigSessionId), launch.AuthorityRequestId.ToString(),
            FieldKey(launch.TournamentSessionId), FieldKey(launch.MissionInstanceId),
            FieldKey(launch.RequesterControllerId), launch.IsSpectator ? "1" : "0");

    private void DisconnectAllTournamentClients()
    {
        var peers = new HashSet<NetPeer>(tournamentPeerControllers.Keys);
        foreach (Player connected in playerManager.Players)
        {
            if (playerManager.TryGetPeer(connected.ControllerId, out NetPeer peer))
                peers.Add(peer);
        }
        foreach (NetPeer peer in peers)
        {
            try { peer.Disconnect(); }
            catch (Exception exception)
            {
                Logger.Fatal(exception, "[Tournament] failed to disconnect client after irreversible launch failure.");
            }
        }
    }

    private void Handle_Disconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (ModInformation.IsClient)
            return;
        if (!TryResolveDisconnectedController(payload.What.PlayerId, out var controllerId))
            return;

        GameThread.RunSafe(
            () => ProcessDisconnected(controllerId),
            context: nameof(Handle_Disconnected));
    }

    private bool TryResolveDisconnectedController(NetPeer playerId, out string controllerId)
    {
        if (playerManager.TryGetPlayer(playerId, out var player))
            controllerId = player.ControllerId;
        else if (!tournamentPeerControllers.TryGetValue(playerId, out controllerId))
            return false;

        tournamentPeerControllers.TryRemove(playerId, out _);
        return true;
    }

    private void ProcessDisconnected(string controllerId)
    {
        foreach (TournamentSessionSnapshot snapshot in sessionRegistry.GetAll())
            ProcessDisconnectedSession(controllerId, snapshot);
    }

    private void ProcessDisconnectedSession(
        string controllerId,
        TournamentSessionSnapshot snapshot)
    {
        bool involved = snapshot.Contestants.Any(contestant => contestant.ControllerId == controllerId) ||
            snapshot.SpectatorControllerIds.Contains(controllerId);
        if (!involved && snapshot.Phase == TournamentSessionPhase.Preparation)
            return;

        if (snapshot.Phase == TournamentSessionPhase.Preparation)
        {
            LeavePreparationAfterDisconnect(controllerId, snapshot);
            return;
        }
        if (involved)
            LeaveActive(snapshot.SessionId, snapshot.Revision, controllerId, null);
    }

    private void LeavePreparationAfterDisconnect(
        string controllerId,
        TournamentSessionSnapshot snapshot)
    {
        TournamentMutationStatus status = sessionRegistry.TryLeavePreparation(
            snapshot.SessionId,
            snapshot.Revision,
            controllerId,
            out var changed,
            out var removed);
        if (status != TournamentMutationStatus.Applied)
            return;
        if (removed)
        {
            network.SendAll(new NetworkTournamentSessionRemoved(snapshot.SessionId, snapshot.TownId));
            messageBroker.Publish(this, new TournamentSessionRemoved(snapshot.SessionId, snapshot.TownId));
        }
        else if (changed != null)
        {
            BroadcastSnapshot(changed);
        }
    }

    private void LeaveActive(
        string sessionId,
        long expectedRevision,
        string controllerId,
        NetPeer peer)
    {
        if (!sessionRegistry.TryGet(sessionId, out var current))
        {
            SendCanonical(peer, current);
            return;
        }

        bool isMember = IsActiveMember(current, controllerId);
        if (TryHandleCompletedLeave(current, controllerId, isMember, peer))
            return;
        if (!TryResolveReplacementName(current, out var replacementName))
        {
            SendCanonical(peer, current);
            return;
        }
        if (expectedRevision != current.Revision && !isMember)
        {
            SendCanonical(peer, current);
            return;
        }

        ApplyActiveLeave(current, controllerId, replacementName, peer);
    }

    private static bool IsActiveMember(
        TournamentSessionSnapshot snapshot,
        string controllerId)
    {
        return snapshot.Contestants.Any(contestant =>
                contestant.IsHuman && contestant.ControllerId == controllerId) ||
            snapshot.SpectatorControllerIds.Contains(controllerId);
    }

    private bool TryHandleCompletedLeave(
        TournamentSessionSnapshot current,
        string controllerId,
        bool isMember,
        NetPeer peer)
    {
        if (!current.IsCompleted)
            return false;
        if (!isMember)
        {
            SendCanonical(peer, current);
            return true;
        }

        Logger.Information(
            "[Tournament] Completed tournament leave requested session={SessionId}, controller={ControllerId}",
            current.SessionId,
            controllerId);
        FinalizeCompletedTournament(current);
        return true;
    }

    private bool TryResolveReplacementName(
        TournamentSessionSnapshot current,
        out string replacementName)
    {
        replacementName = null;
        if (!objectManager.TryGetObject(current.TownId, out Town town) ||
            town.Culture?.BasicTroop == null ||
            !objectManager.TryGetId(town.Culture.BasicTroop, out _))
            return false;

        replacementName = town.Culture.BasicTroop.Name?.ToString() ?? "Tournament Recruit";
        return true;
    }

    private void ApplyActiveLeave(
        TournamentSessionSnapshot current,
        string controllerId,
        string replacementName,
        NetPeer peer)
    {
        TournamentSpawnManifestData migrationManifest = null;
        if (current.HostControllerId == controllerId)
            sessionRegistry.TryGetSpawnManifest(current.SessionId, out migrationManifest);

        TournamentMutationStatus status = sessionRegistry.TryLeaveActive(
            current.SessionId,
            current.Revision,
            controllerId,
            MBRandom.RandomInt(int.MaxValue),
            replacementName,
            out var snapshot,
            out var outcome,
            out var noViewers);
        PublishHostMigration(current, snapshot, status, migrationManifest);
        if (status == TournamentMutationStatus.Applied)
            SettleBetLedger(current.SessionId, controllerId, snapshot.Revision, current.CurrentMatchId, "Tournament bet forfeited", peer);
        PublishMutation(status, peer, snapshot);
        if (status == TournamentMutationStatus.Applied && outcome == TournamentBallotOutcome.SimulateMatch)
            snapshot = SimulateCurrentMatchAndAdvance(snapshot, controllerId);
        if (status == TournamentMutationStatus.Applied && noViewers && snapshot != null)
            ResolveAfterLastHumanLeaves(snapshot);
    }

    private void PublishHostMigration(
        TournamentSessionSnapshot previous,
        TournamentSessionSnapshot snapshot,
        TournamentMutationStatus status,
        TournamentSpawnManifestData migrationManifest)
    {
        if (status != TournamentMutationStatus.Applied ||
            migrationManifest == null ||
            snapshot.HostControllerId == previous.HostControllerId)
            return;

        network.SendAll(new NetworkTournamentSpawnManifest(migrationManifest));
        messageBroker.Publish(this, new TournamentSpawnManifestUpdated(migrationManifest));
    }

    private void ResolveAfterLastHumanLeaves(TournamentSessionSnapshot snapshot)
    {
        Logger.Information(
            "[Tournament] Last human left active tournament session={SessionId}, phase={Phase}, revision={Revision}; resolving remaining bracket immediately",
            snapshot.SessionId,
            snapshot.Phase,
            snapshot.Revision);
        if (!snapshot.IsCompleted)
            SimulateRemainingTournament(snapshot);

        if (sessionRegistry.TryGet(snapshot.SessionId, out var completed) && completed.IsCompleted)
        {
            Logger.Information(
                "[Tournament] Last-human leave completed simulation session={SessionId}, winner={WinnerSlotId}; finalizing immediately",
                completed.SessionId,
                completed.WinnerSlotId);
            FinalizeCompletedTournament(completed);
            return;
        }

        Logger.Error(
            "[Tournament] Last-human leave failed to complete tournament session={SessionId}; canonicalPresent={CanonicalPresent}, phase={Phase}, match={MatchId}, revision={Revision}",
            snapshot.SessionId,
            completed != null,
            completed?.Phase,
            completed?.CurrentMatchId,
            completed?.Revision);
    }
    private bool TryAuthenticate(object source, out NetPeer peer, out Player player)
    {
        peer = source as NetPeer;
        player = null;
        if (peer == null || !playerManager.TryGetPlayer(peer, out player))
        {
            Logger.Warning("Rejected tournament request without an authenticated player peer");
            return false;
        }
        tournamentPeerControllers[peer] = player.ControllerId;
        return true;
    }

    private bool TryResolvePlayerAtTown(
        Player player,
        string townId,
        out Town town,
        out Hero hero,
        out MobileParty party)
    {
        town = null;
        hero = null;
        party = null;
        if (!objectManager.TryGetObject(townId, out town))
            return false;
        if (!TryResolvePlayer(player, out hero, out party))
            return false;
        return party.CurrentSettlement == town.Settlement && hero.PartyBelongedTo == party;
    }

    private bool TryResolvePlayer(Player player, out Hero hero, out MobileParty party)
    {
        hero = null;
        party = null;
        if (player == null || !objectManager.TryGetObject(player.HeroId, out hero))
            return false;
        if (!objectManager.TryGetObject(player.MobilePartyId, out party))
            return false;
        return hero != null && party != null;
    }

    private void PublishMutation(TournamentMutationStatus status, NetPeer requester, TournamentSessionSnapshot snapshot)
    {
        if (status == TournamentMutationStatus.Applied)
            BroadcastSnapshot(snapshot);
        else if (snapshot != null)
            SendCanonical(requester, snapshot);
    }

    private void BroadcastSnapshot(TournamentSessionSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        network.SendAll(new NetworkTournamentSessionSnapshot(snapshot));
        messageBroker.Publish(this, new TournamentSessionUpdated(snapshot));
    }

    private void SendCanonical(NetPeer peer, TournamentSessionSnapshot snapshot)
    {
        if (peer != null && snapshot != null)
            network.Send(peer, new NetworkTournamentSessionSnapshot(snapshot));
    }

    private void SendRejection(NetPeer peer, string townId, string reason)
    {
        if (peer != null)
            network.Send(peer, new NetworkTournamentRequestRejected(townId, reason));
    }

    private void SendBetResult(
        NetPeer peer,
        string sessionId,
        long revision,
        long sequence,
        string matchId,
        bool accepted,
        string reason,
        int bettedDenars,
        int thisRoundBettedDenars,
        int expectedPayout,
        bool isSettlement)
    {
        if (peer == null)
            return;

        network.Send(peer, new NetworkTournamentBetResult(
            sessionId,
            revision,
            sequence,
            matchId,
            accepted,
            reason,
            bettedDenars,
            thisRoundBettedDenars,
            expectedPayout,
            isSettlement));
    }

    private void ResolveBets(TournamentSessionSnapshot snapshot, string[] winnerSlotIds)
    {
        foreach (TournamentContestantData contestant in snapshot.Contestants.Where(contestant => contestant.IsHuman))
        {
            if (!IsSlotInCurrentMatch(snapshot, contestant.SlotId))
                continue;
            if (winnerSlotIds.Contains(contestant.SlotId))
                continue;
            SettleBetLedger(
                snapshot.SessionId,
                contestant.ControllerId,
                snapshot.Revision,
                snapshot.CurrentMatchId,
                "Tournament bet lost");
        }
    }

    private void CompleteTournament(TournamentSessionSnapshot snapshot)
    {
        if (!ShouldCompleteTournament(snapshot))
            return;
        if (!completionInProgress.Add(snapshot.SessionId))
            return;

        try
        {
            if (!TryResolveCompletionContext(
                    snapshot,
                    out var game,
                    out var winner,
                    out var participants,
                    out var winnerData,
                    out var manager))
            {
                Logger.Error("Could not rehydrate completed tournament session {SessionId}", snapshot.SessionId);
                return;
            }

            TournamentCompletionTransaction transaction = GetOrCreateCompletionTransaction(snapshot.SessionId);
            RunCompletionTransactions(
                snapshot,
                game,
                winner,
                participants,
                winnerData,
                manager,
                transaction);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to complete tournament session {SessionId}", snapshot.SessionId);
        }
        finally
        {
            completionInProgress.Remove(snapshot.SessionId);
        }
    }

    private bool ShouldCompleteTournament(TournamentSessionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.IsCompleted)
            return false;
        return !completionTransactions.TryGetValue(snapshot.SessionId, out var existing) ||
            (!existing.IsCompleted && !existing.IsReadyForRemoval);
    }

    private bool TryResolveCompletionContext(
        TournamentSessionSnapshot snapshot,
        out FightTournamentGame game,
        out CharacterObject winner,
        out MBList<CharacterObject> participants,
        out TournamentContestantData winnerData,
        out TournamentManager manager)
    {
        game = null;
        winner = null;
        participants = null;
        winnerData = snapshot.Contestants
            .FirstOrDefault(contestant => contestant.SlotId == snapshot.WinnerSlotId);
        manager = Campaign.Current?.TournamentManager as TournamentManager;
        if (!tournamentGameInterface.TryRehydrateGame(snapshot, out game) ||
            !TryResolveWinner(snapshot, out winner, out participants) ||
            manager == null ||
            game.Town == null ||
            game.Prize == null ||
            winnerData == null)
            return false;

        return winnerData.ControllerId == null ||
            (winner.IsHero && winner.HeroObject?.PartyBelongedTo != null);
    }

    private TournamentCompletionTransaction GetOrCreateCompletionTransaction(string sessionId)
    {
        if (completionTransactions.TryGetValue(sessionId, out var transaction))
            return transaction;

        transaction = new TournamentCompletionTransaction();
        completionTransactions.Add(sessionId, transaction);
        return transaction;
    }

    private void RunCompletionTransactions(
        TournamentSessionSnapshot snapshot,
        FightTournamentGame game,
        CharacterObject winner,
        MBList<CharacterObject> participants,
        TournamentContestantData winnerData,
        TournamentManager manager,
        TournamentCompletionTransaction transaction)
    {
        transaction.Run(TournamentCompletionStep.Leaderboard, () =>
        {
            if (winner.IsHero)
                manager.AddLeaderboardEntry(winner.HeroObject);
        });
        transaction.Run(TournamentCompletionStep.Influence, () =>
        {
            if (winnerData.ControllerId != null &&
                Campaign.Current.GameMode == CampaignGameMode.Campaign &&
                winner.HeroObject.MapFaction?.IsKingdomFaction == true &&
                winner.HeroObject.MapFaction.Leader != winner.HeroObject)
            {
                GainKingdomInfluenceAction.ApplyForDefault(winner.HeroObject, 1f);
            }
        });
        transaction.Run(TournamentCompletionStep.Prize, () =>
        {
            if (!winner.IsHero)
                return;
            if (winnerData.ControllerId != null)
                winner.HeroObject.PartyBelongedTo.ItemRoster.AddToCounts(game.Prize, 1);
            else
                manager.GivePrizeToWinner(game, winner.HeroObject, true);
        });
        transaction.Run(TournamentCompletionStep.BetPayout,
            () => PayWinningBetAndForfeitOthers(snapshot, winner));
        transaction.Run(TournamentCompletionStep.BetSettlement, () => { });
        transaction.Run(TournamentCompletionStep.SimulationProgression, () =>
        {
            var participantsNeedingProgression = new MBList<CharacterObject>();
            for (int index = 0; index < participants.Count; index++)
            {
                TournamentContestantData contestant = index < snapshot.Contestants.Length
                    ? snapshot.Contestants[index]
                    : null;
                if (NeedsSimulationProgression(
                        snapshot.SessionId,
                        contestant,
                        liveProgressionControllers))
                    participantsNeedingProgression.Add(participants[index]);
            }
            ApplyTournamentProgression(game.Town, participantsNeedingProgression);
        });
    }

    private void FinalizeCompletedTournament(TournamentSessionSnapshot snapshot)
    {
        CompleteTournament(snapshot);
        if (!TryBeginFinalization(snapshot, out var transaction))
            return;

        try
        {
            RunFinalizationTransactions(snapshot, transaction);
            RemoveCompletedTransaction(completionTransactions, snapshot.SessionId, transaction);
            Logger.Information(
                "[Tournament] Finalized completed tournament session={SessionId}; ejecting all mission members",
                snapshot.SessionId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to finalize tournament session {SessionId}", snapshot.SessionId);
        }
        finally
        {
            completionInProgress.Remove(snapshot.SessionId);
        }
    }

    internal static bool RemoveCompletedTransaction(
        Dictionary<string, TournamentCompletionTransaction> completionTransactions,
        string sessionId,
        TournamentCompletionTransaction transaction)
    {
        return transaction?.IsCompleted == true &&
            completionTransactions.TryGetValue(sessionId, out var current) &&
            ReferenceEquals(current, transaction) &&
            completionTransactions.Remove(sessionId);
    }

    private bool TryBeginFinalization(
        TournamentSessionSnapshot snapshot,
        out TournamentCompletionTransaction transaction)
    {
        transaction = null;
        return snapshot != null &&
            completionTransactions.TryGetValue(snapshot.SessionId, out transaction) &&
            transaction.IsReadyForRemoval &&
            !transaction.IsCompleted &&
            completionInProgress.Add(snapshot.SessionId);
    }

    private void RunFinalizationTransactions(
        TournamentSessionSnapshot snapshot,
        TournamentCompletionTransaction transaction)
    {
        if (!tournamentGameInterface.TryRehydrateGame(snapshot, out var game) ||
            !TryResolveWinner(snapshot, out var winner, out var participants) ||
            game.Town == null ||
            game.Prize == null)
        {
            throw new InvalidOperationException("Could not rehydrate the tournament before final removal.");
        }

        transaction.Run(TournamentCompletionStep.FinishedEvent, () =>
            CampaignEventDispatcher.Instance.OnTournamentFinished(
                winner,
                new MBReadOnlyList<CharacterObject>(participants),
                game.Town,
                game.Prize));
        transaction.Run(TournamentCompletionStep.NativeRemoval, () =>
        {
            if (!RemoveNativeTournament(snapshot))
                throw new InvalidOperationException("Could not remove the completed native tournament.");
        });
        transaction.Run(TournamentCompletionStep.SessionRemoval, () =>
        {
            if (!RemoveSessionAndBroadcast(snapshot))
                throw new InvalidOperationException("Could not remove the completed coop tournament session.");
        });
    }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (ModInformation.IsClient)
            return;

        foreach (TournamentSessionSnapshot snapshot in sessionRegistry.GetAll()
                     .Where(session => session.IsCompleted))
        {
            CompleteTournament(snapshot);
        }
    }

    private bool RemoveNativeTournament(TournamentSessionSnapshot snapshot)
    {
        if (Campaign.Current?.TournamentManager is not TournamentManager manager ||
            !objectManager.TryGetObject(snapshot.TownId, out Town town))
        {
            return false;
        }

        TournamentGame nativeGame = manager.GetTournamentGame(town);
        if (nativeGame == null)
        {
            Logger.Information(
                "[Tournament] Completed native tournament already absent session={SessionId}, town={Town}, activeCount={ActiveCount}",
                snapshot.SessionId,
                town.Name,
                manager._activeTournaments.Count);
            return true;
        }

        int activeCountBefore = manager._activeTournaments.Count;
        Logger.Information(
            "[Tournament] Removing completed native tournament session={SessionId}, town={Town}, activeCountBefore={ActiveCountBefore}",
            snapshot.SessionId,
            town.Name,
            activeCountBefore);
        using (nativeRemovalAuthorization.Authorize(snapshot.TownId))
        {
            Logger.Information(
                "[Tournament] Authorized completion transaction native removal session={SessionId}, townId={TownId}",
                snapshot.SessionId,
                snapshot.TownId);
            manager.RemoveTournament(nativeGame);
        }
        bool removed = manager.GetTournamentGame(town) == null;
        Logger.Information(
            "[Tournament] Removed completed native tournament session={SessionId}, town={Town}, removed={Removed}, activeCountBefore={ActiveCountBefore}, activeCountAfter={ActiveCountAfter}",
            snapshot.SessionId,
            town.Name,
            removed,
            activeCountBefore,
            manager._activeTournaments.Count);
        return removed;
    }

    private bool TryResolveWinner(
        TournamentSessionSnapshot snapshot,
        out CharacterObject winner,
        out MBList<CharacterObject> participants)
    {
        winner = null;
        participants = new MBList<CharacterObject>();
        foreach (TournamentContestantData contestant in snapshot.Contestants)
        {
            if (!objectManager.TryGetObject(contestant.CharacterId, out CharacterObject character))
                return false;
            participants.Add(character);
            if (contestant.SlotId == snapshot.WinnerSlotId)
                winner = character;
        }
        return winner != null;
    }

    private static bool TryGetPlayerSlot(
        TournamentSessionSnapshot snapshot,
        string controllerId,
        out TournamentContestantData slot)
    {
        slot = snapshot.Contestants.FirstOrDefault(contestant =>
            contestant.IsHuman && !contestant.IsReplaced && contestant.ControllerId == controllerId);
        return slot != null;
    }

    private static bool IsSpectatorRole(TournamentSessionSnapshot snapshot, string controllerId) =>
        snapshot?.SpectatorControllerIds?.Contains(controllerId) == true &&
        !TryGetPlayerSlot(snapshot, controllerId, out _);

    private static bool IsActiveSession(TournamentSessionSnapshot snapshot) =>
        snapshot != null && snapshot.Phase != TournamentSessionPhase.Preparation && !snapshot.IsCompleted;

    private static bool IsConfirmedEntrant(TournamentSessionSnapshot snapshot, string controllerId)
    {
        return snapshot != null && (snapshot.HostControllerId == controllerId ||
            snapshot.SuccessorControllerIds.Contains(controllerId));
    }

    private static void LogSpectatorRequest(
        string controllerId,
        TournamentMutationStatus status,
        TournamentSessionSnapshot snapshot)
    {
        Logger.Information(
            "[Tournament] Spectator request session={SessionId}, controller={ControllerId}, status={Status}, humans={HumanCount}, spectators={SpectatorCount}, voters={VoterCount}, revision={Revision}",
            snapshot?.SessionId,
            controllerId,
            status,
            CountActiveHumans(snapshot),
            snapshot?.SpectatorControllerIds?.Length ?? 0,
            snapshot?.VoterCount ?? 0,
            snapshot?.Revision ?? 0);
    }

    private static int CountActiveHumans(TournamentSessionSnapshot snapshot)
        => snapshot?.Contestants?.Count(contestant => contestant.IsHuman && !contestant.IsReplaced) ?? 0;

    private static int CountEntrants(TournamentSessionSnapshot snapshot)
        => snapshot == null || snapshot.HostControllerId == null
            ? 0
            : 1 + (snapshot.SuccessorControllerIds?.Length ?? 0);

    private static string GetPlayerRole(TournamentSessionSnapshot snapshot, string controllerId)
    {
        if (snapshot?.Contestants?.Any(contestant =>
                contestant.IsHuman &&
                !contestant.IsReplaced &&
                contestant.ControllerId == controllerId) == true)
            return "Competitor";

        return snapshot?.SpectatorControllerIds?.Contains(controllerId) == true
            ? "Spectator"
            : "NonParticipant";
    }

    private static bool IsSlotInCurrentMatch(TournamentSessionSnapshot snapshot, string slotId)
    {
        return snapshot.Rounds
            .SelectMany(round => round.Matches)
            .Where(match => match.MatchId == snapshot.CurrentMatchId)
            .SelectMany(match => match.Teams)
            .Any(team => team.ParticipantSlotIds.Contains(slotId));
    }

    private bool HasValidManifestObjects(TournamentSpawnManifestData manifest)
    {
        foreach (TournamentAgentSpawnData agent in manifest.Agents)
        {
            if (!objectManager.TryGetObject(agent.CharacterId, out CharacterObject _) ||
                !HasValidEquipmentObjects(agent.Equipment))
            {
                return false;
            }

            if (agent.MountAgentId != Guid.Empty &&
                ((!string.IsNullOrEmpty(agent.MountCharacterId) &&
                  !objectManager.TryGetObject(agent.MountCharacterId, out BasicCharacterObject _)) ||
                 !HasValidEquipmentObjects(agent.MountEquipment)))
            {
                return false;
            }
        }
        return true;
    }

    private bool HasValidEquipmentObjects(EquipmentElement[] equipment)
    {
        foreach (EquipmentElement element in equipment)
        {
            if (element.IsEmpty) continue;
            if (!objectManager.TryGetCatalogId(element.Item, out string _) ||
                (element.ItemModifier != null &&
                 !objectManager.TryGetId(element.ItemModifier, out string _)))
            {
                return false;
            }
        }
        return true;
    }

    private void SettleBetLedger(
        string sessionId,
        string controllerId,
        long revision,
        string matchId,
        string reason,
        NetPeer peer = null)
    {
        string key = GetBetKey(sessionId, controllerId);
        if (!betLedger.TryGetValue(key, out var ledger))
            return;

        peer ??= tournamentPeerControllers
            .FirstOrDefault(entry => entry.Value == controllerId)
            .Key;
        long sequence = ledger.LastDomainSequence + 1;
        betLedger.Remove(key);
        SendBetResult(
            peer,
            sessionId,
            revision,
            sequence,
            matchId,
            true,
            reason,
            0,
            0,
            0,
            true);
    }

    private static string GetBetKey(string sessionId, string controllerId)
    {
        return $"{sessionId}:{controllerId}";
    }
    private sealed class BetLedgerEntry
    {
        public readonly Dictionary<string, int> MatchAmounts = new();
        public int ExpectedPayout;
        public int TotalBettedDenars;
        public string LastMatchId;
        public long LastDomainSequence;
        public string LastHeroId;
        public int LastHeroGold;
    }
}
