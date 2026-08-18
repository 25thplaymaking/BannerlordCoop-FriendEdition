using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.TournamentGames;
using TaleWorlds.Core;

namespace GameInterface.Services.Tournaments.Handlers;

internal sealed class TournamentStateSyncHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<TournamentStateSyncHandler>();
    internal static TournamentStateSyncHandler Instance { get; private set; }

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ITournamentSessionRegistry sessionRegistry;
    private readonly IRelayNetwork[] relayNetworks;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<TournamentStateIntent, NetworkTournamentStateQueryResult> stateRoute;
    private long nextStateEpoch;
    private long appliedStateEpoch = -1;
    private string appliedConfigSessionId;
    private bool campaignReady;

    public TournamentStateSyncHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        ITournamentSessionRegistry sessionRegistry,
        IEnumerable<IRelayNetwork> relayNetworks,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.sessionRegistry = sessionRegistry;
        this.relayNetworks = relayNetworks?.ToArray() ?? Array.Empty<IRelayNetwork>();
        this.configAuthority = configAuthority;
        Instance = this;

        stateRoute = authorityRequestRouter.Register(
            AuthorityRoute<TournamentStateIntent, NetworkRequestTournamentState,
                NetworkTournamentStateQueryResult>.Define(
                routeId: "tournament.state",
                kind: AuthorityRouteKind.BootstrapQuery,
                createHeader: CreateStateHeader,
                buildRequest: (_, header) => new NetworkRequestTournamentState(header),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: request => request.TryValidateWireShape(out var failure) ? null : failure,
                buildCommandKey: request => $"state:{request.SessionId.Length}:{request.SessionId}:{request.Revision}",
                validateHeader: ValidateStateHeader,
                execute: ExecuteStateQuery,
                createTerminalResult: CreateStateTerminal,
                probeClientCommit: ProbeStateApplied,
                requestResync: _ => { },
                presentTerminalOutcome: PresentStateTerminal,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: AuthorityTimeoutPolicy.BootstrapQuery,
                // No result-shape predicate. The router has already matched the reply to this
                // request by id, and there is nothing about the payload left to validate here.
                //
                // The previous predicate required Snapshot.NativeTournaments to be non-null, which
                // a correct reply cannot guarantee: NetworkTournamentStateSnapshot is
                // [ProtoContract(SkipConstructor = true)], protobuf-net writes nothing for an empty
                // repeated field, and the skipped constructor never runs its Array.Empty
                // initialisation - so a host with no active tournaments deserializes to null. That
                // is the normal wire shape for "none", which is exactly why ApplyStateSnapshot
                // null-coalesces the same three arrays. Treating it as an unexpected result failed
                // the request as invalid-replica before the commit probe ever ran, and because this
                // route is fail-closed that disconnected the client - deterministically, for every
                // player joining a campaign that happened to have no tournaments running.
                requireAuthenticatedPlayer: true,
                failClosedOnApplyFailure: true));

        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<NetworkTournamentStateSnapshot>(Handle_StateSnapshot);
        messageBroker.Subscribe<NetworkTournamentStateQueryResult>(Handle_StateQueryResult);
        messageBroker.Subscribe<NetworkTournamentSessionRemoved>(Handle_SessionRemoved);
        messageBroker.Subscribe<TournamentNativeStateChanged>(Handle_NativeStateChanged);
        messageBroker.Subscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<NetworkTournamentStateSnapshot>(Handle_StateSnapshot);
        messageBroker.Unsubscribe<NetworkTournamentStateQueryResult>(Handle_StateQueryResult);
        messageBroker.Unsubscribe<NetworkTournamentSessionRemoved>(Handle_SessionRemoved);
        messageBroker.Unsubscribe<TournamentNativeStateChanged>(Handle_NativeStateChanged);
        messageBroker.Unsubscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
        stateRoute.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void Handle_CampaignReady(MessagePayload<CampaignReady> payload)
    {
        campaignReady = true;
        if (ModInformation.IsClient)
            StartStateBootstrap();
    }

    private void Handle_HostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (payload.What.Snapshot == null || !configAuthority.IsCurrent(payload.What.Snapshot)) return;
        if (ModInformation.IsClient && !string.Equals(appliedConfigSessionId, payload.What.Snapshot.SessionId,
                StringComparison.Ordinal))
        {
            appliedConfigSessionId = null;
            appliedStateEpoch = -1;
        }
        StartStateBootstrap();
    }

    private void StartStateBootstrap()
    {
        if (!ModInformation.IsClient || !configAuthority.TryGetCurrent(out _)) return;

        // Wait for the campaign before asking for its state. HostModConfigAccepted is published
        // from the module-validation barrier, which runs before the save has even been requested,
        // and MessageBroker.Publish is synchronous - so without this the query went out while the
        // client still had 30-90s of manifest hashing, save transfer, and campaign load ahead of
        // it. A BootstrapQuery only allows 30s of wall-clock across its retries, so the reply
        // could not be serviced in time and the route failed on a deadline it was never able to
        // meet. Fourberie, Improved Garrisons, Player Settlement and the capability handler all
        // gate their bootstrap the same way; this route and romance were the outliers.
        if (!campaignReady) return;

        stateRoute.Submit(default);
    }

    internal static void RequestCanonicalResync()
    {
        Instance?.StartStateBootstrap();
    }

    private AuthorityRequestHeader CreateStateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateStateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "tournament-state-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private AuthorityServerReply<NetworkTournamentStateQueryResult> ExecuteStateQuery(
        AuthorityServerContext context,
        NetworkRequestTournamentState _)
    {
        NetworkTournamentStateSnapshot snapshot;
        try
        {
            snapshot = CreateStateSnapshot();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Tournament state bootstrap failed to capture canonical state");
            return new AuthorityServerReply<NetworkTournamentStateQueryResult>(
                CreateStateTerminal(context.Header, AuthorityResultStatus.ExecutionFailed,
                    "tournament-state-capture-failed"), false);
        }

        return new AuthorityServerReply<NetworkTournamentStateQueryResult>(
            new NetworkTournamentStateQueryResult(
                context.Header,
                AuthorityResultStatus.Accepted,
                snapshot,
                ++nextStateEpoch,
                null),
            statePublished: true);
    }

    private static NetworkTournamentStateQueryResult CreateStateTerminal(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reasonCode) =>
        new NetworkTournamentStateQueryResult(header, status, default, 0, reasonCode);

    private AuthorityCommitProbeResult ProbeStateApplied(NetworkTournamentStateQueryResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.StateEpoch <= 0 ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            !string.Equals(config.SessionId, result.Header.SessionId, StringComparison.Ordinal) ||
            config.Revision != result.Header.CommittedRevision)
            return AuthorityCommitProbeResult.Invalid;

        return string.Equals(appliedConfigSessionId, result.Header.SessionId, StringComparison.Ordinal) &&
            appliedStateEpoch == result.StateEpoch
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentStateTerminal(AuthorityClientOutcome<NetworkTournamentStateQueryResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        Logger.Warning("Tournament state bootstrap ended without a canonical snapshot. Completion={Completion}, Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private void Handle_NativeStateChanged(MessagePayload<TournamentNativeStateChanged> payload)
    {
        if (ModInformation.IsClient)
            return;

        GameThread.RunSafe(() =>
        {
            NetworkTournamentStateSnapshot snapshot = CreateStateSnapshot();
            Logger.Information(
                "[Tournament] Broadcasting native tournament state: tournaments={TournamentCount}, towns={TournamentTowns}, sessions={SessionCount}",
                snapshot.NativeTournaments.Length,
                string.Join(",", snapshot.NativeTournaments.Select(tournament => tournament.TownId)),
                snapshot.Sessions.Length);
            network.SendAll(snapshot);
        }, context: nameof(Handle_NativeStateChanged));
    }

    private void Handle_StateSnapshot(MessagePayload<NetworkTournamentStateSnapshot> payload)
    {
        if (ModInformation.IsServer ||
            !TournamentServerMessageGuard.IsTrusted(payload.Who, relayNetworks))
            return;

        GameThread.RunSafe(
            () => ApplyStateSnapshot(payload.What),
            context: nameof(Handle_StateSnapshot));
    }

    private void Handle_StateQueryResult(MessagePayload<NetworkTournamentStateQueryResult> payload)
    {
        if (ModInformation.IsServer || !TournamentServerMessageGuard.IsTrusted(payload.Who, relayNetworks)) return;
        if (payload.What.Status != AuthorityResultStatus.Accepted) return;

        GameThread.RunSafe(() =>
        {
            if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
                !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
                config.Revision != payload.What.CommittedRevision ||
                payload.What.StateEpoch <= 0)
                return;

            if (ApplyStateSnapshot(payload.What.Snapshot))
            {
                appliedConfigSessionId = payload.What.SessionId;
                appliedStateEpoch = payload.What.StateEpoch;
            }
        }, context: nameof(Handle_StateQueryResult));
    }

    private bool ApplyStateSnapshot(NetworkTournamentStateSnapshot state)
    {
        if (Campaign.Current?.TournamentManager is not TournamentManager manager)
            return false;

        TournamentNativeGameData[] nativeTournaments = state.NativeTournaments ??
            System.Array.Empty<TournamentNativeGameData>();
        TournamentLeaderboardEntryData[] leaderboard = state.Leaderboard ??
            System.Array.Empty<TournamentLeaderboardEntryData>();
        TournamentSessionSnapshot[] sessions = state.Sessions ??
            System.Array.Empty<TournamentSessionSnapshot>();
        LogReceivedState(manager, nativeTournaments);
        RemoveStaleSessions(sessions);

        Dictionary<Town, FightTournamentGame> authoritativeTournaments =
            RehydrateAuthoritativeTournaments(nativeTournaments);
        ReconcileNativeTournaments(manager, authoritativeTournaments);
        ReconcileLeaderboard(manager, leaderboard);
        ApplySessions(sessions);
        return true;
    }

    private static void LogReceivedState(
        TournamentManager manager,
        TournamentNativeGameData[] nativeTournaments)
    {
        Logger.Information(
            "[Tournament] Received native tournament state: authoritative={AuthoritativeCount}, authoritativeTowns={AuthoritativeTowns}, localBefore={LocalCount}, localTownsBefore={LocalTowns}",
            nativeTournaments.Length,
            string.Join(",", nativeTournaments
                .Where(tournament => tournament != null)
                .Select(tournament => tournament.TownId)),
            manager._activeTournaments.Count,
            string.Join(",", manager._activeTournaments
                .Where(tournament => tournament?.Town != null)
                .Select(tournament => tournament.Town.Name.ToString())));
    }

    private void RemoveStaleSessions(TournamentSessionSnapshot[] sessions)
    {
        foreach (TournamentSessionSnapshot stale in TournamentStateReconciliation.GetStaleSessions(
                     sessionRegistry.GetAll(), sessions))
        {
            var tombstone = CreateInternalTombstone(stale);
            if (!sessionRegistry.ApplyTombstone(tombstone))
                continue;
            messageBroker.Publish(this, new TournamentSessionRemoved(tombstone));
        }
    }

    private Dictionary<Town, FightTournamentGame> RehydrateAuthoritativeTournaments(
        TournamentNativeGameData[] nativeTournaments)
    {
        var authoritativeTournaments = new Dictionary<Town, FightTournamentGame>();
        foreach (TournamentNativeGameData data in nativeTournaments)
        {
            if (data == null || !TryRehydrateNativeGame(data, out var game))
                continue;
            authoritativeTournaments[game.Town] = game;
        }
        return authoritativeTournaments;
    }

    private static void ReconcileNativeTournaments(
        TournamentManager manager,
        IReadOnlyDictionary<Town, FightTournamentGame> authoritativeTournaments)
    {
        RemoveStaleNativeTournaments(manager, authoritativeTournaments);
        AddOrUpdateNativeTournaments(manager, authoritativeTournaments);
        Logger.Information(
            "[Tournament] Reconciled native tournament state: authoritative={AuthoritativeCount}, localAfter={LocalCount}, localTownsAfter={LocalTowns}",
            authoritativeTournaments.Count,
            manager._activeTournaments.Count,
            string.Join(",", manager._activeTournaments
                .Where(tournament => tournament?.Town != null)
                .Select(tournament => tournament.Town.Name.ToString())));
    }

    private static void RemoveStaleNativeTournaments(
        TournamentManager manager,
        IReadOnlyDictionary<Town, FightTournamentGame> authoritativeTournaments)
    {
        foreach (TournamentGame tournament in manager._activeTournaments.ToArray())
        {
            if (tournament?.Town != null && authoritativeTournaments.ContainsKey(tournament.Town))
                continue;

            Town removedTown = tournament?.Town;
            manager.RemoveTournament(tournament);
            bool removed = !manager._activeTournaments.Contains(tournament);
            if (removed && removedTown != null)
            {
                CampaignEventDispatcher.Instance.OnTournamentCancelled(removedTown);
                Logger.Information(
                    "[Tournament] Raised native tournament cancellation event after authoritative removal from {Town}",
                    removedTown.Name);
            }
            Logger.Information(
                "[Tournament] Removed native tournament from {Town}; removed={Removed}",
                removedTown?.Name,
                removed);
        }
    }

    private static void AddOrUpdateNativeTournaments(
        TournamentManager manager,
        IReadOnlyDictionary<Town, FightTournamentGame> authoritativeTournaments)
    {
        foreach (var pair in authoritativeTournaments)
        {
            TournamentGame existing = manager._activeTournaments
                .FirstOrDefault(tournament => tournament?.Town == pair.Key);
            if (existing != null)
            {
                existing.CreationTime = pair.Value.CreationTime;
                existing.Mode = pair.Value.Mode;
                existing.Prize = pair.Value.Prize;
                continue;
            }

            manager.AddTournament(pair.Value);
            Logger.Information("[Tournament] Added native tournament to {Town}", pair.Key.Name);
        }
    }

    private void ReconcileLeaderboard(
        TournamentManager manager,
        TournamentLeaderboardEntryData[] leaderboard)
    {
        manager._worldWideTournamentLeaderboard.Clear();
        foreach (TournamentLeaderboardEntryData data in leaderboard)
        {
            if (data != null && objectManager.TryGetObject(data.HeroId, out Hero hero))
                manager.InitializeLeaderboardEntry(hero, data.Wins);
        }
    }

    private void ApplySessions(TournamentSessionSnapshot[] sessions)
    {
        foreach (TournamentSessionSnapshot snapshot in sessions)
        {
            TournamentSessionSnapshot normalized = TournamentSessionSnapshotNormalizer.Normalize(snapshot);
            if (normalized != null && sessionRegistry.ApplySnapshot(normalized))
                messageBroker.Publish(this, new TournamentSessionUpdated(normalized));
        }
    }
    private void Handle_SessionRemoved(MessagePayload<NetworkTournamentSessionRemoved> payload)
    {
        if (ModInformation.IsServer ||
            !TournamentServerMessageGuard.IsTrusted(payload.Who, relayNetworks))
            return;

        GameThread.RunSafe(() =>
        {
            if (sessionRegistry.ApplyTombstone(payload.What))
                messageBroker.Publish(this, new TournamentSessionRemoved(payload.What));
        }, context: nameof(Handle_SessionRemoved));
    }

    private NetworkTournamentSessionRemoved CreateInternalTombstone(TournamentSessionSnapshot snapshot)
    {
        string configSessionId = configAuthority.TryGetCurrent(out ModConfigSnapshot config) ? config.SessionId : string.Empty;
        return new NetworkTournamentSessionRemoved(configSessionId, snapshot.SessionId, snapshot.TownId,
            snapshot.MissionInstanceId, snapshot.Revision + 1, authorityRequestId: 0);
    }

    private NetworkTournamentStateSnapshot CreateStateSnapshot()
    {
        var tournaments = new List<TournamentNativeGameData>();
        if (Campaign.Current?.TournamentManager is TournamentManager manager)
        {
            foreach (TournamentGame tournament in manager._activeTournaments)
            {
                if (tournament?.Town == null || !objectManager.TryGetId(tournament.Town, out var townId))
                {
                    continue;
                }

                bool isSupported = tournament.GetType() == typeof(FightTournamentGame);
                string prizeId = null;
                if (tournament.Prize != null && !objectManager.TryGetId(tournament.Prize, out prizeId))
                {
                    if (isSupported)
                        continue;
                    prizeId = null;
                }
                tournaments.Add(new TournamentNativeGameData(
                    townId,
                    prizeId,
                    tournament.CreationTime,
                    (int)tournament.Mode,
                    isSupported));
            }
        }

        TournamentLeaderboardEntryData[] leaderboard = Campaign.Current?.TournamentManager?.GetLeaderboard()
            ?.Select(entry =>
            {
                return objectManager.TryGetId(entry.Key, out var heroId)
                    ? new TournamentLeaderboardEntryData(heroId, entry.Value)
                    : null;
            })
            .Where(entry => entry != null)
            .ToArray() ?? Array.Empty<TournamentLeaderboardEntryData>();
        return new NetworkTournamentStateSnapshot(
            tournaments.ToArray(),
            leaderboard,
            sessionRegistry.GetAll());
    }

    private bool TryRehydrateNativeGame(TournamentNativeGameData data, out FightTournamentGame game)
    {
        game = null;
        if (!objectManager.TryGetObject(data.TownId, out Town town))
            return false;

        ItemObject prize = null;
        if (data.PrizeItemId != null && !objectManager.TryGetObject(data.PrizeItemId, out prize))
            return false;

        game = data.IsSupported
            ? ObjectHelper.SkipConstructor<FightTournamentGame>()
            : ObjectHelper.SkipConstructor<UnsupportedFightTournamentGame>();
        game.Town = town;
        game.CreationTime = data.CreationTime;
        game.Mode = (TournamentGame.QualificationMode)data.QualificationMode;
        game.Prize = prize;
        return true;
    }
    private sealed class UnsupportedFightTournamentGame : FightTournamentGame
    {
        private UnsupportedFightTournamentGame(Town town) : base(town)
        {
        }
    }

    private readonly struct TournamentStateIntent
    {
    }
}
