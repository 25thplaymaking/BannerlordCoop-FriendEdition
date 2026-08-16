using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>The closed set of server-authoritative ways to resolve a map event.</summary>
internal enum BattleStartMode
{
    Mission = 0,
    Simulation = 1,
    Unclaimed = 2,
}

/// <summary>Feature-owned result of executing one already-admitted battle start.</summary>
internal readonly struct BattleStartDecision
{
    private BattleStartDecision(AuthorityResultStatus status, string reasonCode, bool statePublished)
    {
        Status = status;
        ReasonCode = reasonCode;
        StatePublished = statePublished;
    }

    public AuthorityResultStatus Status { get; }
    public string ReasonCode { get; }
    public bool StatePublished { get; }
    public static BattleStartDecision Accepted() => new(AuthorityResultStatus.Accepted, null, true);
    public static BattleStartDecision Reject(string reasonCode) => new(AuthorityResultStatus.Rejected, reasonCode, false);
    public static BattleStartDecision Failed(string reasonCode) => new(AuthorityResultStatus.ExecutionFailed, reasonCode, false);
}

internal readonly struct BattleStartIntent
{
    public BattleStartIntent(BattleStartMode mode, string mapEventId, string attackerPartyId)
    {
        Mode = mode;
        MapEventId = mapEventId;
        AttackerPartyId = attackerPartyId;
    }

    public BattleStartMode Mode { get; }
    public string MapEventId { get; }
    public string AttackerPartyId { get; }
}

/// <summary>
/// The single authority-route owner for battle starts. It owns request correlation, replay, terminal results and
/// the client commit barrier; mode handlers only perform their already-admitted game-state work.
/// </summary>
internal class BattleStartCoordinator : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleStartCoordinator>();

    internal static BattleStartCoordinator Instance { get; private set; }

    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly BattleMissionStartHandler missionStartHandler;
    private readonly BattleSimulationRunHandler simulationRunHandler;
    private readonly IAuthorityRouteHandle<BattleStartIntent, NetworkBattleStartReply> battleStartRoute;

    public BattleStartCoordinator(
        IObjectManager objectManager,
        IModConfigAuthority configAuthority,
        INetworkConfig configuration,
        IAuthorityRequestRouter authorityRequestRouter,
        BattleMissionStartHandler missionStartHandler,
        BattleSimulationRunHandler simulationRunHandler)
    {
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        this.missionStartHandler = missionStartHandler;
        this.simulationRunHandler = simulationRunHandler;

        battleStartRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleStartIntent, NetworkBattleStartRequest, NetworkBattleStartReply>.Define(
                routeId: "map-event.battle-start",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkBattleStartRequest(header, (int)intent.Mode,
                    intent.MapEventId, intent.AttackerPartyId),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateWireShape,
                buildCommandKey: BuildCommandKey,
                validateHeader: ValidateHeader,
                execute: ExecuteAuthoritatively,
                createTerminalResult: CreateTerminalResult,
                probeClientCommit: ProbeClientCommit,
                requestResync: _ => { },
                presentTerminalOutcome: PresentTerminalOutcome,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout,
                    configuration.ObjectCreationTimeout, retryCount: 1),
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => request.Mode == result.Mode &&
                    string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal)));

        Instance = this;
    }

    public void Dispose()
    {
        battleStartRoute.Dispose();
        if (Instance == this) Instance = null;
    }

    public bool RequestBlocking(BattleStartMode mode, string mapEventId, string attackerPartyId)
    {
        var outcome = battleStartRoute.SubmitBlocking(new BattleStartIntent(mode, mapEventId, attackerPartyId));
        if (outcome.Applied && outcome.Result.Mode == (int)mode &&
            string.Equals(outcome.Result.MapEventId, mapEventId, StringComparison.Ordinal))
            return true;

        Logger.Warning("Battle start did not commit. Completion={Completion} Reason={Reason} Mode={Mode} MapEventId={MapEventId}",
            outcome.Completion, outcome.ReasonCode, mode, mapEventId);
        return false;
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkBattleStartRequest request)
    {
        if (request.Mode != (int)BattleStartMode.Mission && request.Mode != (int)BattleStartMode.Simulation)
            return "invalid-battle-mode";
        if (string.IsNullOrWhiteSpace(request.MapEventId) || request.MapEventId.Length > 256 ||
            request.AttackerPartyId?.Length > 256)
            return "invalid-battle-start-identifiers";
        return null;
    }

    private static string BuildCommandKey(NetworkBattleStartRequest request) =>
        string.Concat(request.Mode, ":", request.MapEventId.Length, ":", request.MapEventId, ":",
            request.AttackerPartyId?.Length ?? -1, ":", request.AttackerPartyId ?? string.Empty);

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

    private AuthorityServerReply<NetworkBattleStartReply> ExecuteAuthoritatively(
        AuthorityServerContext context, NetworkBattleStartRequest request)
    {
        if (!objectManager.TryGetObject<MapEvent>(request.MapEventId, out var mapEvent) || mapEvent == null)
            return Reply(context.Header, request, BattleStartDecision.Reject("map-event-not-found"));
        if (mapEvent.IsFinalized || mapEvent.HasWinner)
            return Reply(context.Header, request, BattleStartDecision.Reject("map-event-finalized"));
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var party) || party == null ||
            mapEvent.FindMapEventParty(party.Party) == null)
            return Reply(context.Header, request, BattleStartDecision.Reject("requester-not-participant"));
        // Compatibility field only: if supplied it must agree with the party established from the authenticated peer.
        if (!string.IsNullOrEmpty(request.AttackerPartyId) &&
            !string.Equals(request.AttackerPartyId, context.Player.MobilePartyId, StringComparison.Ordinal))
            return Reply(context.Header, request, BattleStartDecision.Reject("attacker-party-mismatch"));

        BattleStartDecision decision = request.Mode == (int)BattleStartMode.Mission
            ? missionStartHandler.TryStartMission(request.MapEventId, mapEvent, party, context.Player.MobilePartyId)
            : simulationRunHandler.TryStartSimulation(request.MapEventId, mapEvent, context.Peer, party);
        return Reply(context.Header, request, decision);
    }

    private static AuthorityServerReply<NetworkBattleStartReply> Reply(AuthorityRequestHeader header,
        NetworkBattleStartRequest request, BattleStartDecision decision) =>
        new(new NetworkBattleStartReply(header, decision.Status, request.Mode, request.MapEventId, decision.ReasonCode),
            decision.StatePublished);

    private static NetworkBattleStartReply CreateTerminalResult(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reasonCode) =>
        new(header, status, (int)BattleStartMode.Unclaimed, null, reasonCode);

    private static AuthorityCommitProbeResult ProbeClientCommit(NetworkBattleStartReply result)
    {
        if (result.Mode == (int)BattleStartMode.Mission)
            return BattleModeRegistry.IsMission(result.MapEventId)
                ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
        if (result.Mode == (int)BattleStartMode.Simulation)
            return BattleModeRegistry.IsSimulation(result.MapEventId)
                ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
        return AuthorityCommitProbeResult.Invalid;
    }

    private static void PresentTerminalOutcome(AuthorityClientOutcome<NetworkBattleStartReply> outcome)
    {
        if (outcome.Applied) return;
        // Nothing may remain armed after rejection, timeout, cancellation, or a failed canonical apply.
        LoadingWindow.DisableGlobalLoadingWindow();
        if (BattleSpawnGate.IsCoopBattleActive) BattleSpawnGate.EndBattle();
        BattleSimulationReplay.Reset();
    }
}
