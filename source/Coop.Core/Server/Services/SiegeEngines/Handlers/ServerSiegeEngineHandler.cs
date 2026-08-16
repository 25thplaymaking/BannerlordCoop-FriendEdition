using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.SiegeEngines.Messages;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.SiegeEngines.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.SiegeEngines.Messages;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEnginesConstructionProgress.Messages;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using TaleWorlds.CampaignSystem.Party;
using static TaleWorlds.CampaignSystem.Siege.SiegeEvent;

namespace Coop.Core.Server.Services.SiegeEngines.Handlers;

/// <summary>
/// Broadcasts server-side siege engine container and construction progress changes to clients, and
/// applies client engine build/remove orders authoritatively.
/// </summary>
internal class ServerSiegeEngineHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerSiegeEngineHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeEngineDeployIntent, NetworkSiegeEngineCommandResult> deployRoute;
    private readonly IAuthorityRouteHandle<SiegeEngineRemoveIntent, NetworkSiegeEngineCommandResult> removeRoute;
    private readonly object revisionGate = new object();
    private readonly ConcurrentDictionary<string, SiegeEngineSlotRevision> slotRevisions = new ConcurrentDictionary<string, SiegeEngineSlotRevision>();
    private string revisionEpoch = NewRevisionEpoch();
    // Route execution and the native container patches both run on the campaign game thread. This
    // narrow scope lets the existing replication owner attach a request correlation without
    // changing ordinary engine callbacks into authority routes.
    private ActiveAuthorityMutation activeMutation;

    private sealed class ActiveAuthorityMutation
    {
        public AuthorityRequestHeader Header;
        public SiegeEngineCommandKind Kind;
        public SiegeEnginesContainer Container;
        public int Index;
        public bool IsRanged;
        public string EngineTypeId;
        public bool MutationStarted;
        public PublishedSlotState Published;
        public Exception Failure;
    }

    private sealed class PublishedSlotState
    {
        public string ContainerId;
        public string SiegeEngineId;
        public string EngineTypeId;
        public int Index;
        public bool IsRanged;
        public long Revision;
        public string Epoch;
    }

    public ServerSiegeEngineHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.siegeEventInterface = siegeEventInterface;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        deployRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEngineDeployIntent, NetworkRequestDeploySiegeEngine,
                NetworkSiegeEngineCommandResult>.Define(
                "siege.engine.deploy", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestDeploySiegeEngine(intent.SiegeEventId, intent.Side,
                    intent.EngineTypeId, intent.Index, intent.ExpectedOccupantId, intent.ExpectedRevision,
                    intent.RevisionEpoch, intent.ContainerId, header),
                request => request.Header, result => result.Header, ValidateDeployWireShape, BuildDeployCommandKey,
                ValidateHeader, ExecuteDeploy, CreateDeployTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedDeployResult));
        removeRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEngineRemoveIntent, NetworkRequestRemoveSiegeEngine,
                NetworkSiegeEngineCommandResult>.Define(
                "siege.engine.remove", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestRemoveSiegeEngine(intent.SiegeEventId, intent.Side,
                    intent.Index, intent.IsRanged, intent.MoveToReserve, intent.ExpectedOccupantId,
                    intent.ExpectedRevision, intent.RevisionEpoch, intent.ContainerId, header),
                request => request.Header, result => result.Header, ValidateRemoveWireShape, BuildRemoveCommandKey,
                ValidateHeader, ExecuteRemove, CreateRemoveTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedRemoveResult));
        messageBroker.Subscribe<SiegeEngineDeployed>(HandleDeployed);
        messageBroker.Subscribe<SiegeEngineUndeployed>(HandleUndeployed);
        messageBroker.Subscribe<SiegeEngineReserveAdded>(HandleReserveAdded);
        messageBroker.Subscribe<SiegeEngineReserveRemoved>(HandleReserveRemoved);
        messageBroker.Subscribe<SiegeEngineProgressChanged>(HandleProgress);
        messageBroker.Subscribe<SiegeEngineHitpointsChanged>(HandleHitpoints);
        messageBroker.Subscribe<SiegeEngineMissileAdded>(HandleMissileAdded);
        messageBroker.Subscribe<SiegeEngineContainerMutationFailed>(HandleContainerMutationFailed);
        messageBroker.Subscribe<PlayerCampaignEntered>(HandlePlayerCampaignEntered);
        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
    }

    private readonly struct SiegeEngineDeployIntent
    {
        public SiegeEngineDeployIntent(string siegeEventId, string containerId, int side, string engineTypeId,
            int index, string expectedOccupantId, long expectedRevision, string revisionEpoch)
        {
            SiegeEventId = siegeEventId;
            ContainerId = containerId;
            Side = side;
            EngineTypeId = engineTypeId;
            Index = index;
            ExpectedOccupantId = expectedOccupantId;
            ExpectedRevision = expectedRevision;
            RevisionEpoch = revisionEpoch;
        }

        public string SiegeEventId { get; }
        public string ContainerId { get; }
        public int Side { get; }
        public string EngineTypeId { get; }
        public int Index { get; }
        public string ExpectedOccupantId { get; }
        public long ExpectedRevision { get; }
        public string RevisionEpoch { get; }
    }

    private readonly struct SiegeEngineRemoveIntent
    {
        public SiegeEngineRemoveIntent(string siegeEventId, string containerId, int side, int index, bool isRanged,
            bool moveToReserve, string expectedOccupantId, long expectedRevision, string revisionEpoch)
        {
            SiegeEventId = siegeEventId;
            ContainerId = containerId;
            Side = side;
            Index = index;
            IsRanged = isRanged;
            MoveToReserve = moveToReserve;
            ExpectedOccupantId = expectedOccupantId;
            ExpectedRevision = expectedRevision;
            RevisionEpoch = revisionEpoch;
        }

        public string SiegeEventId { get; }
        public string ContainerId { get; }
        public int Side { get; }
        public int Index { get; }
        public bool IsRanged { get; }
        public bool MoveToReserve { get; }
        public string ExpectedOccupantId { get; }
        public long ExpectedRevision { get; }
        public string RevisionEpoch { get; }
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

    private static string ValidateDeployWireShape(NetworkRequestDeploySiegeEngine request)
    {
        if (!HasBoundedId(request.SiegeEventId) || !HasBoundedId(request.ContainerId) ||
            !HasBoundedId(request.EngineTypeId) || !HasBoundedEpoch(request.RevisionEpoch) ||
            request.Side != (int)BattleSideEnum.Attacker || request.Index < 0 || request.Index > 31 ||
            request.ExpectedRevision < 0 || !HasBoundedOptionalId(request.ExpectedOccupantId))
            return "invalid-siege-engine-deploy";
        return null;
    }

    private static string ValidateRemoveWireShape(NetworkRequestRemoveSiegeEngine request)
    {
        if (!HasBoundedId(request.SiegeEventId) || !HasBoundedId(request.ContainerId) ||
            !HasBoundedEpoch(request.RevisionEpoch) || request.Side != (int)BattleSideEnum.Attacker ||
            request.Index < 0 || request.Index > 31 || request.ExpectedRevision < 0 ||
            !HasBoundedOptionalId(request.ExpectedOccupantId))
            return "invalid-siege-engine-remove";
        return null;
    }

    private static bool HasBoundedId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool HasBoundedOptionalId(string value) => value == null || HasBoundedId(value);
    private static bool HasBoundedEpoch(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64;

    private static string BuildDeployCommandKey(NetworkRequestDeploySiegeEngine request) => string.Concat(
        request.SiegeEventId.Length, ":", request.SiegeEventId, ":", request.ContainerId.Length, ":", request.ContainerId,
        ":", request.Side, ":", request.EngineTypeId.Length, ":", request.EngineTypeId, ":", request.Index,
        ":", request.ExpectedOccupantId?.Length ?? -1, ":", request.ExpectedOccupantId, ":", request.ExpectedRevision,
        ":", request.RevisionEpoch);

    private static string BuildRemoveCommandKey(NetworkRequestRemoveSiegeEngine request) => string.Concat(
        request.SiegeEventId.Length, ":", request.SiegeEventId, ":", request.ContainerId.Length, ":", request.ContainerId,
        ":", request.Side, ":", request.Index, ":", request.IsRanged, ":", request.MoveToReserve,
        ":", request.ExpectedOccupantId?.Length ?? -1, ":", request.ExpectedOccupantId, ":", request.ExpectedRevision,
        ":", request.RevisionEpoch);

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> ExecuteDeploy(
        AuthorityServerContext context, NetworkRequestDeploySiegeEngine request)
    {
        if (!TryResolveAuthorizedSlot(context, request.SiegeEventId, request.ContainerId, request.Side, request.Index,
                isRanged: null, out var siegeEvent, out var container, out var containerId, out var denial))
            return Reject(context.Header, SiegeEngineCommandKind.Deploy, request.SiegeEventId, request.ContainerId,
                request.Side, request.Index, false, denial, request.RevisionEpoch);

        if (IsSiegeFightingAssault(siegeEvent))
            return Reject(context.Header, SiegeEngineCommandKind.Deploy, request.SiegeEventId, containerId,
                request.Side, request.Index, false, "siege-assault-active", request.RevisionEpoch);

        var engineType = MBObjectManager.Instance.GetObject<SiegeEngineType>(request.EngineTypeId);
        if (engineType == null)
            return Reject(context.Header, SiegeEngineCommandKind.Deploy, request.SiegeEventId, containerId,
                request.Side, request.Index, false, "unknown-engine-type", request.RevisionEpoch);

        bool isRanged = engineType.IsRanged;
        var slots = isRanged ? container.DeployedRangedSiegeEngines : container.DeployedMeleeSiegeEngines;
        if (request.Index < 0 || request.Index >= slots.Length)
            return Reject(context.Header, SiegeEngineCommandKind.Deploy, request.SiegeEventId, containerId,
                request.Side, request.Index, isRanged, "invalid-siege-engine-slot", request.RevisionEpoch);
        GetSlotState(containerId, isRanged, request.Index, out var currentRevision, out var currentEpoch);
        if (!SlotMatchesExpectedState(container, request.Index, isRanged, request.ExpectedOccupantId,
                request.ExpectedRevision, request.RevisionEpoch, currentRevision, currentEpoch, objectManager))
        {
            TrySendCurrentSlotState(context.Peer, context.Header, siegeEvent, container, containerId, request.Side,
                request.Index, isRanged, moveToReserve: false, currentRevision, currentEpoch);
            return Reject(context.Header, SiegeEngineCommandKind.Deploy, request.SiegeEventId, containerId,
                request.Side, request.Index, isRanged, "stale-slot-state", currentEpoch);
        }

        var current = GetSlot(container, isRanged, request.Index);
        if (current?.SiegeEngine == engineType)
            return PublishAlreadyApplied(context, SiegeEngineCommandKind.Deploy, request.SiegeEventId, containerId,
                request.Side, request.Index, isRanged, request.EngineTypeId, current, currentRevision, currentEpoch,
                moveToReserve: false);

        return ExecuteMutation(context, SiegeEngineCommandKind.Deploy, request, siegeEvent, container, containerId,
            isRanged, engineType);
    }

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> ExecuteRemove(
        AuthorityServerContext context, NetworkRequestRemoveSiegeEngine request)
    {
        if (!TryResolveAuthorizedSlot(context, request.SiegeEventId, request.ContainerId, request.Side, request.Index,
                request.IsRanged, out var siegeEvent, out var container, out var containerId, out var denial))
            return Reject(context.Header, SiegeEngineCommandKind.Remove, request.SiegeEventId, request.ContainerId,
                request.Side, request.Index, request.IsRanged, denial, request.RevisionEpoch);

        if (IsSiegeFightingAssault(siegeEvent))
            return Reject(context.Header, SiegeEngineCommandKind.Remove, request.SiegeEventId, containerId,
                request.Side, request.Index, request.IsRanged, "siege-assault-active", request.RevisionEpoch);

        GetSlotState(containerId, request.IsRanged, request.Index, out var currentRevision, out var currentEpoch);
        if (!SlotMatchesExpectedState(container, request.Index, request.IsRanged, request.ExpectedOccupantId,
                request.ExpectedRevision, request.RevisionEpoch, currentRevision, currentEpoch, objectManager))
        {
            TrySendCurrentSlotState(context.Peer, context.Header, siegeEvent, container, containerId, request.Side,
                request.Index, request.IsRanged, request.MoveToReserve, currentRevision, currentEpoch);
            return Reject(context.Header, SiegeEngineCommandKind.Remove, request.SiegeEventId, containerId,
                request.Side, request.Index, request.IsRanged, "stale-slot-state", currentEpoch);
        }

        if (GetSlot(container, request.IsRanged, request.Index) == null)
            return PublishAlreadyApplied(context, SiegeEngineCommandKind.Remove, request.SiegeEventId, containerId,
                request.Side, request.Index, request.IsRanged, null, null, currentRevision, currentEpoch,
                request.MoveToReserve);

        return ExecuteMutation(context, SiegeEngineCommandKind.Remove, request, siegeEvent, container, containerId,
            request.IsRanged, engineType: null);
    }

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> ExecuteMutation(
        AuthorityServerContext context,
        SiegeEngineCommandKind kind,
        NetworkRequestDeploySiegeEngine request,
        SiegeEvent siegeEvent,
        SiegeEnginesContainer container,
        string containerId,
        bool isRanged,
        SiegeEngineType engineType)
    {
        var scope = new ActiveAuthorityMutation
        {
            Header = context.Header,
            Kind = kind,
            Container = container,
            Index = request.Index,
            IsRanged = isRanged,
            EngineTypeId = request.EngineTypeId,
        };
        return ExecuteScopedMutation(context, scope, request.SiegeEventId, request.Side, moveToReserve: false,
            () => siegeEventInterface.DeploySiegeEngine(siegeEvent, (BattleSideEnum)request.Side, engineType, request.Index));
    }

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> ExecuteMutation(
        AuthorityServerContext context,
        SiegeEngineCommandKind kind,
        NetworkRequestRemoveSiegeEngine request,
        SiegeEvent siegeEvent,
        SiegeEnginesContainer container,
        string containerId,
        bool isRanged,
        SiegeEngineType engineType)
    {
        var scope = new ActiveAuthorityMutation
        {
            Header = context.Header,
            Kind = kind,
            Container = container,
            Index = request.Index,
            IsRanged = isRanged,
        };
        return ExecuteScopedMutation(context, scope, request.SiegeEventId, request.Side, request.MoveToReserve,
            () => siegeEventInterface.RemoveDeployedSiegeEngine(siegeEvent, (BattleSideEnum)request.Side, request.Index,
                request.IsRanged, request.MoveToReserve));
    }

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> ExecuteScopedMutation(
        AuthorityServerContext context,
        ActiveAuthorityMutation scope,
        string siegeEventId,
        int side,
        bool moveToReserve,
        Action mutate)
    {
        if (activeMutation != null)
            throw new InvalidOperationException("Nested authority siege-engine mutation is not supported.");

        activeMutation = scope;
        try
        {
            scope.MutationStarted = true;
            mutate();
            if (scope.Failure != null)
                return IsolateAfterMutation(context, scope, siegeEventId, side, moveToReserve,
                    "native-siege-engine-finalizer-failed", scope.Failure);
            var published = scope.Published;
            if (published == null)
                return IsolateAfterMutation(context, scope, siegeEventId, side, moveToReserve,
                    "canonical-slot-publication-missing", null);

            return Accepted(context.Header, scope.Kind, siegeEventId, published, side, moveToReserve);
        }
        catch (Exception exception)
        {
            if (scope.MutationStarted)
                return IsolateAfterMutation(context, scope, siegeEventId, side, moveToReserve,
                    "siege-engine-mutation-ambiguous", exception);
            return Failed(context.Header, scope.Kind, siegeEventId, null, side, scope.Index, scope.IsRanged,
                "siege-engine-mutation-failed", null);
        }
        finally
        {
            activeMutation = null;
        }
    }

    private bool TryResolveAuthorizedSlot(
        AuthorityServerContext context,
        string siegeEventId,
        string requestedContainerId,
        int sideValue,
        int index,
        bool? isRanged,
        out SiegeEvent siegeEvent,
        out SiegeEnginesContainer container,
        out string containerId,
        out string denial)
    {
        siegeEvent = null;
        container = null;
        containerId = null;
        denial = "invalid-siege-engine-context";
        if (sideValue != (int)BattleSideEnum.Attacker ||
            !objectManager.TryGetObjectWithLogging<SiegeEvent>(siegeEventId, out siegeEvent) ||
            !TryGetContainer(siegeEvent, BattleSideEnum.Attacker, out container) ||
            !objectManager.TryGetIdWithLogging(container, out containerId) ||
            !string.Equals(containerId, requestedContainerId, StringComparison.Ordinal))
            return false;

        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var playerParty) ||
            !ReferenceEquals(playerParty.BesiegerCamp, siegeEvent.BesiegerCamp) ||
            !ReferenceEquals(siegeEvent.BesiegerCamp?.LeaderParty, playerParty))
        {
            denial = "siege-engine-not-camp-leader";
            return false;
        }

        if (isRanged == null)
            return index >= 0;

        var slots = isRanged.Value ? container.DeployedRangedSiegeEngines : container.DeployedMeleeSiegeEngines;
        if (index < 0 || index >= slots.Length)
        {
            denial = "invalid-siege-engine-slot";
            return false;
        }

        return true;
    }

    private static SiegeEngineConstructionProgress GetSlot(SiegeEnginesContainer container, bool isRanged, int index)
    {
        var slots = isRanged ? container.DeployedRangedSiegeEngines : container.DeployedMeleeSiegeEngines;
        return index >= 0 && index < slots.Length ? slots[index] : null;
    }

    private static bool IsSiegeFightingAssault(SiegeEvent siegeEvent) =>
        siegeEvent.BesiegerCamp?.LeaderParty?.MapEvent != null ||
        siegeEvent.BesiegedSettlement?.Party?.MapEvent != null;

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> PublishAlreadyApplied(
        AuthorityServerContext context,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        string containerId,
        int side,
        int index,
        bool isRanged,
        string engineTypeId,
        SiegeEngineConstructionProgress current,
        long revision,
        string epoch,
        bool moveToReserve)
    {
        try
        {
            string siegeEngineId = null;
            if (current != null && !objectManager.TryGetIdWithLogging(current, out siegeEngineId))
                return Failed(context.Header, kind, siegeEventId, containerId, side, index, isRanged,
                    "slot-state-unavailable", epoch);

            if (kind == SiegeEngineCommandKind.Deploy)
            {
                network.Send(context.Peer, new NetworkChangeSiegeEngineDeployed(containerId, siegeEngineId,
                    current.SiegeEngine?.StringId, index, revision, isRanged, epoch,
                    context.Header.SessionId, context.Header.RequestId));
                engineTypeId = current.SiegeEngine?.StringId;
            }
            else
            {
                network.Send(context.Peer, new NetworkChangeSiegeEngineUndeployed(containerId, index, isRanged,
                    moveToReserve, revision, epoch, context.Header.SessionId, context.Header.RequestId));
            }

            return Accepted(context.Header, kind, siegeEventId, new PublishedSlotState
            {
                ContainerId = containerId,
                SiegeEngineId = siegeEngineId,
                EngineTypeId = engineTypeId,
                Index = index,
                IsRanged = isRanged,
                Revision = revision,
                Epoch = epoch,
            }, side, moveToReserve);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Could not publish idempotent siege-engine state. Route={Route} SessionId={SessionId} RequestId={RequestId}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId);
            return Failed(context.Header, kind, siegeEventId, containerId, side, index, isRanged,
                "slot-state-publication-failed", epoch);
        }
    }

    private void TrySendCurrentSlotState(
        LiteNetLib.NetPeer peer,
        AuthorityRequestHeader header,
        SiegeEvent siegeEvent,
        SiegeEnginesContainer container,
        string containerId,
        int side,
        int index,
        bool isRanged,
        bool moveToReserve,
        long revision,
        string epoch)
    {
        try
        {
            var current = GetSlot(container, isRanged, index);
            if (current == null)
            {
                network.Send(peer, new NetworkChangeSiegeEngineUndeployed(containerId, index, isRanged,
                    moveToReserve, revision, epoch, header.SessionId, header.RequestId));
                return;
            }

            if (!objectManager.TryGetIdWithLogging(current, out var engineId)) return;
            network.Send(peer, new NetworkChangeSiegeEngineDeployed(containerId, engineId,
                current.SiegeEngine?.StringId, index, revision, isRanged, epoch,
                header.SessionId, header.RequestId));
        }
        catch (Exception exception)
        {
            Logger.Warning(exception,
                "Could not send current siege-engine slot state. SessionId={SessionId} RequestId={RequestId}",
                header.SessionId, header.RequestId);
        }
    }

    private AuthorityServerReply<NetworkSiegeEngineCommandResult> IsolateAfterMutation(
        AuthorityServerContext context,
        ActiveAuthorityMutation scope,
        string siegeEventId,
        int side,
        bool moveToReserve,
        string reason,
        Exception exception)
    {
        Logger.Fatal(exception,
            "Ambiguous globally replicated siege-engine mutation; disconnecting campaign peers. Route={Route} SessionId={SessionId} RequestId={RequestId} Reason={Reason}",
            context.RouteId, context.Header.SessionId, context.Header.RequestId, reason);
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            try { peer.Disconnect(); }
            catch (Exception disconnectException)
            {
                Logger.Fatal(disconnectException,
                    "Could not disconnect peer after ambiguous siege-engine mutation. Controller={ControllerId}",
                    player.ControllerId);
            }
        }

        return new AuthorityServerReply<NetworkSiegeEngineCommandResult>(
            CreateResult(context.Header, scope.Kind, siegeEventId, null, side, scope.Index, scope.IsRanged,
                null, null, 0, null, AuthorityResultStatus.ExecutionFailed, reason),
            statePublished: false,
            suppressReply: true);
    }

    private static AuthorityServerReply<NetworkSiegeEngineCommandResult> Accepted(
        AuthorityRequestHeader header,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        PublishedSlotState state,
        int side,
        bool moveToReserve) =>
        new(CreateResult(header, kind, siegeEventId, state.ContainerId, side, state.Index, state.IsRanged,
            state.EngineTypeId, state.SiegeEngineId, state.Revision, state.Epoch, AuthorityResultStatus.Accepted, null),
            statePublished: true);

    private static AuthorityServerReply<NetworkSiegeEngineCommandResult> Reject(
        AuthorityRequestHeader header,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        string containerId,
        int side,
        int index,
        bool isRanged,
        string reason,
        string epoch) =>
        new(CreateResult(header, kind, siegeEventId, containerId, side, index, isRanged, null, null, 0,
            epoch, AuthorityResultStatus.StaleState, reason), statePublished: false);

    private static AuthorityServerReply<NetworkSiegeEngineCommandResult> Failed(
        AuthorityRequestHeader header,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        string containerId,
        int side,
        int index,
        bool isRanged,
        string reason,
        string epoch) =>
        new(CreateResult(header, kind, siegeEventId, containerId, side, index, isRanged, null, null, 0,
            epoch, AuthorityResultStatus.ExecutionFailed, reason), statePublished: false);

    private static NetworkSiegeEngineCommandResult CreateDeployTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        CreateResult(header, SiegeEngineCommandKind.Deploy, null, null, 0, 0, false, null, null, 0, null,
            status, reason);

    private static NetworkSiegeEngineCommandResult CreateRemoveTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        CreateResult(header, SiegeEngineCommandKind.Remove, null, null, 0, 0, false, null, null, 0, null,
            status, reason);

    private static NetworkSiegeEngineCommandResult CreateResult(
        AuthorityRequestHeader requestHeader,
        SiegeEngineCommandKind kind,
        string siegeEventId,
        string containerId,
        int side,
        int index,
        bool isRanged,
        string engineTypeId,
        string siegeEngineId,
        long slotRevision,
        string epoch,
        AuthorityResultStatus status,
        string reason) =>
        new(new AuthorityResultHeader(requestHeader.SessionId, requestHeader.RequestId, status,
                status == AuthorityResultStatus.Accepted ? slotRevision : requestHeader.ExpectedRevision, reason),
            kind, siegeEventId, containerId, side, index, isRanged, engineTypeId, siegeEngineId, slotRevision, epoch);

    private static bool IsExpectedDeployResult(NetworkRequestDeploySiegeEngine request,
        NetworkSiegeEngineCommandResult result) => result.Kind == SiegeEngineCommandKind.Deploy &&
        string.Equals(request.SiegeEventId, result.SiegeEventId, StringComparison.Ordinal) &&
        string.Equals(request.ContainerId, result.ContainerId, StringComparison.Ordinal) &&
        request.Side == result.Side && request.Index == result.Index &&
        string.Equals(request.EngineTypeId, result.EngineTypeId, StringComparison.Ordinal) &&
        result.Header.RequestId == request.Header.RequestId &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal);

    private static bool IsExpectedRemoveResult(NetworkRequestRemoveSiegeEngine request,
        NetworkSiegeEngineCommandResult result) => result.Kind == SiegeEngineCommandKind.Remove &&
        string.Equals(request.SiegeEventId, result.SiegeEventId, StringComparison.Ordinal) &&
        string.Equals(request.ContainerId, result.ContainerId, StringComparison.Ordinal) &&
        request.Side == result.Side && request.Index == result.Index && request.IsRanged == result.IsRanged &&
        result.Header.RequestId == request.Header.RequestId &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal);


    private static bool TryGetContainer(SiegeEvent siegeEvent, BattleSideEnum side, out SiegeEnginesContainer container)
    {
        container = null;
        if (side != BattleSideEnum.Attacker && side != BattleSideEnum.Defender) return false;

        container = siegeEvent.GetSiegeEventSide(side)?.SiegeEngines;
        return container != null;
    }

    /// <summary>
    /// Optimistic slot-generation check. The revision rejects ABA (including the same engine returning to the
    /// slot), while the occupant id verifies that client and server agree on the contents of that generation.
    /// </summary>
    internal static bool SlotMatchesExpectedState(
        SiegeEnginesContainer container,
        int index,
        bool isRanged,
        string expectedOccupantId,
        long expectedRevision,
        string expectedRevisionEpoch,
        long currentRevision,
        string currentRevisionEpoch,
        IObjectManager objectManager)
    {
        if (container == null || objectManager == null) return false;
        if (!string.Equals(expectedRevisionEpoch, currentRevisionEpoch, StringComparison.Ordinal)) return false;
        if (expectedRevision != currentRevision) return false;

        var slots = isRanged ? container.DeployedRangedSiegeEngines : container.DeployedMeleeSiegeEngines;
        if (index < 0 || index >= slots.Length) return false;

        var currentOccupant = slots[index];
        if (currentOccupant == null) return expectedOccupantId == null;
        if (string.IsNullOrEmpty(expectedOccupantId)) return false;

        return objectManager.TryGetId(currentOccupant, out var currentOccupantId)
            && string.Equals(currentOccupantId, expectedOccupantId, StringComparison.Ordinal);
    }

    private static string SlotKey(string containerId, bool isRanged, int index)
    {
        return containerId + "|" + (isRanged ? "r" : "m") + "|" + index;
    }

    private static string NewRevisionEpoch() => Guid.NewGuid().ToString("N");

    private void GetSlotState(string containerId, bool isRanged, int index, out long revision, out string epoch)
    {
        lock (revisionGate)
        {
            epoch = revisionEpoch;
            revision = slotRevisions.TryGetValue(SlotKey(containerId, isRanged, index), out var slot)
                ? slot.Revision
                : 0L;
        }
    }

    // Caller holds revisionGate through both generation advance and network send so a snapshot cannot publish
    // the new generation before the delta that creates it.
    private long AdvanceSlotRevisionLocked(string containerId, bool isRanged, int index)
    {
        var updated = slotRevisions.AddOrUpdate(
            SlotKey(containerId, isRanged, index),
            _ => new SiegeEngineSlotRevision(containerId, isRanged, index, 1L),
            (_, current) => new SiegeEngineSlotRevision(containerId, isRanged, index, checked(current.Revision + 1L)));
        return updated.Revision;
    }

    private void HandlePlayerCampaignEntered(MessagePayload<PlayerCampaignEntered> payload)
    {
        lock (revisionGate)
        {
            var snapshot = new NetworkSyncSiegeEngineSlotRevisions(revisionEpoch, slotRevisions.Values.ToArray());
            network.Send(payload.What.playerId, snapshot);
        }
    }

    private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
    {
        lock (revisionGate)
        {
            slotRevisions.Clear();
            revisionEpoch = NewRevisionEpoch();
        }
    }

    // Runs on the game thread already — published from the container-mutation patch; only resolves ids and broadcasts, so no GameThread.RunSafe.
    private void HandleDeployed(MessagePayload<SiegeEngineDeployed> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.SiegeEngine, out var siegeEngineId)) return;

        bool isRanged = obj.SiegeEngine.SiegeEngine?.IsRanged == true;
        lock (revisionGate)
        {
            long slotRevision = AdvanceSlotRevisionLocked(containerId, isRanged, obj.Index);
            var scope = activeMutation;
            bool correlated = scope != null && scope.Kind == SiegeEngineCommandKind.Deploy &&
                ReferenceEquals(scope.Container, obj.Container) && scope.Index == obj.Index &&
                scope.IsRanged == isRanged && string.Equals(scope.EngineTypeId, obj.SiegeEngine.SiegeEngine?.StringId,
                    StringComparison.Ordinal);
            network.SendAll(new NetworkChangeSiegeEngineDeployed(
                containerId,
                siegeEngineId,
                obj.SiegeEngine.SiegeEngine?.StringId,
                obj.Index,
                slotRevision,
                isRanged,
                revisionEpoch,
                correlated ? scope.Header.SessionId : null,
                correlated ? scope.Header.RequestId : 0));
            if (correlated)
            {
                scope.Published = new PublishedSlotState
                {
                    ContainerId = containerId,
                    SiegeEngineId = siegeEngineId,
                    EngineTypeId = obj.SiegeEngine.SiegeEngine?.StringId,
                    Index = obj.Index,
                    IsRanged = isRanged,
                    Revision = slotRevision,
                    Epoch = revisionEpoch,
                };
            }
        }
    }

    // Runs on the game thread already — published from the container-mutation patch; only resolves an id and broadcasts, so no GameThread.RunSafe.
    private void HandleUndeployed(MessagePayload<SiegeEngineUndeployed> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;

        lock (revisionGate)
        {
            long slotRevision = AdvanceSlotRevisionLocked(containerId, obj.IsRanged, obj.Index);
            var scope = activeMutation;
            bool correlated = scope != null && scope.Kind == SiegeEngineCommandKind.Remove &&
                ReferenceEquals(scope.Container, obj.Container) && scope.Index == obj.Index &&
                scope.IsRanged == obj.IsRanged;
            network.SendAll(new NetworkChangeSiegeEngineUndeployed(
                containerId,
                obj.Index,
                obj.IsRanged,
                obj.MoveToReserve,
                slotRevision,
                revisionEpoch,
                correlated ? scope.Header.SessionId : null,
                correlated ? scope.Header.RequestId : 0));
            if (correlated)
            {
                scope.Published = new PublishedSlotState
                {
                    ContainerId = containerId,
                    Index = obj.Index,
                    IsRanged = obj.IsRanged,
                    Revision = slotRevision,
                    Epoch = revisionEpoch,
                };
            }
        }
    }

    private void HandleReserveAdded(MessagePayload<SiegeEngineReserveAdded> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.SiegeEngine, out var siegeEngineId)) return;

        network.SendAll(new NetworkChangeSiegeEngineReserveAdded(containerId, siegeEngineId, obj.SiegeEngine.SiegeEngine?.StringId));
    }

    private void HandleReserveRemoved(MessagePayload<SiegeEngineReserveRemoved> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.SiegeEngine, out var siegeEngineId)) return;

        network.SendAll(new NetworkChangeSiegeEngineReserveRemoved(containerId, siegeEngineId));
    }

    // Runs on the game thread already — published from the construction-progress patch; only resolves an id and broadcasts, so no GameThread.RunSafe.
    private void HandleProgress(MessagePayload<SiegeEngineProgressChanged> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.SiegeEngine, out var siegeEngineId)) return;

        network.SendAll(new NetworkChangeSiegeEngineProgress(siegeEngineId, obj.IsRedeployment, obj.Value));
    }

    // Runs on the game thread already — published from the hitpoints patch / late registration; resolves an id and broadcasts.
    private void HandleHitpoints(MessagePayload<SiegeEngineHitpointsChanged> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.SiegeEngine, out var siegeEngineId)) return;

        network.SendAll(new NetworkChangeSiegeEngineHitpoints(siegeEngineId, obj.Hitpoints, obj.MaxHitPoints));
    }

    // Runs on the game thread already — published from the bombardment patch; resolves ids and broadcasts the visual missile.
    private void HandleMissileAdded(MessagePayload<SiegeEngineMissileAdded> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.SiegeEvent, out var siegeEventId)) return;

        string targetEngineId = null;
        if (obj.TargetSiegeEngine != null) objectManager.TryGetId(obj.TargetSiegeEngine, out targetEngineId);

        network.SendAll(new NetworkAddSiegeEngineMissile(siegeEventId, (int)obj.Side, obj.ShooterType?.StringId,
            obj.ShooterSlotIndex, (int)obj.TargetType, obj.TargetSlotIndex, targetEngineId,
            obj.CollisionTicks, obj.FireTicks, obj.HitSuccessful));
    }

    private void HandleContainerMutationFailed(MessagePayload<SiegeEngineContainerMutationFailed> payload)
    {
        var failure = payload.What;
        var scope = activeMutation;
        if (scope == null || !ReferenceEquals(scope.Container, failure.Container) || scope.Index != failure.Index ||
            scope.IsRanged != failure.IsRanged)
            return;

        scope.Failure = failure.Exception ?? new InvalidOperationException("Native siege-engine mutation failed.");
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<SiegeEngineDeployed>(HandleDeployed);
        messageBroker.Unsubscribe<SiegeEngineUndeployed>(HandleUndeployed);
        messageBroker.Unsubscribe<SiegeEngineReserveAdded>(HandleReserveAdded);
        messageBroker.Unsubscribe<SiegeEngineReserveRemoved>(HandleReserveRemoved);
        messageBroker.Unsubscribe<SiegeEngineProgressChanged>(HandleProgress);
        messageBroker.Unsubscribe<SiegeEngineHitpointsChanged>(HandleHitpoints);
        messageBroker.Unsubscribe<SiegeEngineMissileAdded>(HandleMissileAdded);
        messageBroker.Unsubscribe<SiegeEngineContainerMutationFailed>(HandleContainerMutationFailed);
        messageBroker.Unsubscribe<PlayerCampaignEntered>(HandlePlayerCampaignEntered);
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        deployRoute.Dispose();
        removeRoute.Dispose();
    }
}
