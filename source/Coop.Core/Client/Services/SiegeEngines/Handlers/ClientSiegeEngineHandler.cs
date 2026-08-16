using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.SiegeEngines.Messages;
using Coop.Core.Server.Services.SiegeEngines.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.SiegeEngines.Messages;
using GameInterface.Services.SiegeEnginesConstructionProgress.Messages;
using System;
using System.Collections.Generic;
using Serilog;
using TaleWorlds.Core;
using static TaleWorlds.CampaignSystem.Siege.SiegeEvent;

namespace Coop.Core.Client.Services.SiegeEngines.Handlers;

/// <summary>
/// Forwards replicated siege engine container and construction progress changes to GameInterface, and
/// sends the local player's engine build/remove orders to the server.
/// </summary>
internal class ClientSiegeEngineHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClientSiegeEngineHandler>();

    private sealed class PendingSlotRequest
    {
        public bool IsDeploy;
        public string SiegeEventId;
        public string ContainerId;
        public int Side;
        public string EngineTypeId;
        public int Index;
        public bool IsRanged;
        public bool MoveToReserve;
        public string ExpectedOccupantId;
    }

    private sealed class ObservedSlotState
    {
        public bool IsDeployed;
        public string SiegeEngineId;
        public string EngineTypeId;
        public bool MoveToReserve;
        public string SessionId;
        public long RequestId;
    }

    private readonly struct SiegeEngineDeployIntent
    {
        public SiegeEngineDeployIntent(PendingSlotRequest request, long revision, string epoch)
        {
            SiegeEventId = request.SiegeEventId;
            ContainerId = request.ContainerId;
            Side = request.Side;
            EngineTypeId = request.EngineTypeId;
            Index = request.Index;
            ExpectedOccupantId = request.ExpectedOccupantId;
            ExpectedRevision = revision;
            RevisionEpoch = epoch;
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
        public SiegeEngineRemoveIntent(PendingSlotRequest request, long revision, string epoch)
        {
            SiegeEventId = request.SiegeEventId;
            ContainerId = request.ContainerId;
            Side = request.Side;
            Index = request.Index;
            IsRanged = request.IsRanged;
            MoveToReserve = request.MoveToReserve;
            ExpectedOccupantId = request.ExpectedOccupantId;
            ExpectedRevision = revision;
            RevisionEpoch = epoch;
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

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeEngineDeployIntent, NetworkSiegeEngineCommandResult> deployRoute;
    private readonly IAuthorityRouteHandle<SiegeEngineRemoveIntent, NetworkSiegeEngineCommandResult> removeRoute;
    private readonly object revisionGate = new object();
    private readonly Dictionary<string, long> slotRevisions = new Dictionary<string, long>();
    private readonly List<IMessage> pendingSlotDeltas = new List<IMessage>();
    private readonly List<PendingSlotRequest> pendingLocalRequests = new List<PendingSlotRequest>();
    private readonly Dictionary<string, ObservedSlotState> observedSlotStates = new Dictionary<string, ObservedSlotState>();
    private string revisionEpoch;

    public ClientSiegeEngineHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        deployRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEngineDeployIntent, NetworkRequestDeploySiegeEngine,
                NetworkSiegeEngineCommandResult>.Define(
                "siege.engine.deploy", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestDeploySiegeEngine(intent.SiegeEventId, intent.Side,
                    intent.EngineTypeId, intent.Index, intent.ExpectedOccupantId, intent.ExpectedRevision,
                    intent.RevisionEpoch, intent.ContainerId, header),
                request => request.Header, result => result.Header, ValidateDeployWireShape, BuildDeployCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Siege engine routes execute only on the server."),
                CreateDeployTerminalResult, ProbeDeployCommit, _ => { }, PresentTerminalOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedDeployResult));
        removeRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeEngineRemoveIntent, NetworkRequestRemoveSiegeEngine,
                NetworkSiegeEngineCommandResult>.Define(
                "siege.engine.remove", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestRemoveSiegeEngine(intent.SiegeEventId, intent.Side,
                    intent.Index, intent.IsRanged, intent.MoveToReserve, intent.ExpectedOccupantId,
                    intent.ExpectedRevision, intent.RevisionEpoch, intent.ContainerId, header),
                request => request.Header, result => result.Header, ValidateRemoveWireShape, BuildRemoveCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Siege engine routes execute only on the server."),
                CreateRemoveTerminalResult, ProbeRemoveCommit, _ => { }, PresentTerminalOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedRemoveResult));
        messageBroker.Subscribe<NetworkChangeSiegeEngineDeployed>(HandleDeployed);
        messageBroker.Subscribe<NetworkChangeSiegeEngineUndeployed>(HandleUndeployed);
        messageBroker.Subscribe<NetworkChangeSiegeEngineReserveAdded>(HandleReserveAdded);
        messageBroker.Subscribe<NetworkChangeSiegeEngineReserveRemoved>(HandleReserveRemoved);
        messageBroker.Subscribe<NetworkChangeSiegeEngineProgress>(HandleProgress);
        messageBroker.Subscribe<NetworkChangeSiegeEngineHitpoints>(HandleHitpoints);
        messageBroker.Subscribe<NetworkAddSiegeEngineMissile>(HandleMissileAdded);
        messageBroker.Subscribe<NetworkSyncSiegeEngineSlotRevisions>(HandleSlotRevisionSnapshot);
        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Subscribe<SiegeEngineDeployRequested>(HandleDeployRequested);
        messageBroker.Subscribe<SiegeEngineRemovalRequested>(HandleRemovalRequested);
    }

    private void HandleDeployed(MessagePayload<NetworkChangeSiegeEngineDeployed> payload)
    {
        var obj = payload.What;
        lock (revisionGate)
        {
            if (!TryAdvanceSlotRevisionLocked(obj.RevisionEpoch, obj.ContainerId, obj.IsRanged, obj.Index, obj.SlotRevision, obj))
            {
                RecordEqualRevisionProofLocked(obj);
                return;
            }
            RecordObservedStateLocked(obj);
            PublishDeployed(obj);
        }
    }

    private void PublishDeployed(NetworkChangeSiegeEngineDeployed obj)
    {
        messageBroker.Publish(this, new ChangeSiegeEngineDeployed(obj.ContainerId, obj.SiegeEngineId, obj.EngineTypeId, obj.Index));
    }

    private void HandleUndeployed(MessagePayload<NetworkChangeSiegeEngineUndeployed> payload)
    {
        var obj = payload.What;
        lock (revisionGate)
        {
            if (!TryAdvanceSlotRevisionLocked(obj.RevisionEpoch, obj.ContainerId, obj.IsRanged, obj.Index, obj.SlotRevision, obj))
            {
                RecordEqualRevisionProofLocked(obj);
                return;
            }
            RecordObservedStateLocked(obj);
            PublishUndeployed(obj);
        }
    }

    private void PublishUndeployed(NetworkChangeSiegeEngineUndeployed obj)
    {
        messageBroker.Publish(this, new ChangeSiegeEngineUndeployed(obj.ContainerId, obj.Index, obj.IsRanged, obj.MoveToReserve));
    }

    private void HandleSlotRevisionSnapshot(MessagePayload<NetworkSyncSiegeEngineSlotRevisions> payload)
    {
        var snapshot = payload.What;
        lock (revisionGate)
        {
            // A campaign/server handler owns one epoch. Re-entering a campaign receives a complete snapshot,
            // so replace rather than merge: revisions from a prior campaign may be higher for repeated ids.
            // Do not seed the snapshot's final counters yet: queued deltas are post-save changes and must replay
            // in wire order against the transferred save state first. Keep the gate through publication so a
            // concurrently arriving live delta cannot overtake this buffered batch.
            revisionEpoch = snapshot.RevisionEpoch;
            slotRevisions.Clear();
            observedSlotStates.Clear();
            var replayedSlotKeys = ReplayPendingSlotDeltasLocked();

            // The snapshot is authoritative for every slot, including ones with no post-save delta. Max also
            // covers any generation whose state was already present in the transferred save.
            MergeSlotRevisionSnapshotLocked(snapshot.Slots);

            // UI orders can be raised while the transferred save is already interactive but before the
            // server's revision snapshot reaches this handler. Send them only after replaying the queued
            // post-save deltas. A click for any replay-touched slot is stale even if the occupant returned via
            // ABA, so discard it rather than retargeting unseen state; untouched slots retain observed intent.
            FlushPendingLocalRequestsLocked(replayedSlotKeys);
        }
    }

    private HashSet<string> ReplayPendingSlotDeltasLocked()
    {
        var replayedSlotKeys = new HashSet<string>();
        foreach (var delta in pendingSlotDeltas)
            ReplayPendingSlotDeltaLocked(delta, replayedSlotKeys);

        pendingSlotDeltas.Clear();
        return replayedSlotKeys;
    }

    private void ReplayPendingSlotDeltaLocked(IMessage delta, HashSet<string> replayedSlotKeys)
    {
        if (!DeltaBelongsToEpoch(delta, revisionEpoch)) return;

        if (delta is NetworkChangeSiegeEngineDeployed deployed)
        {
            ReplayDeployedSlotDeltaLocked(deployed, replayedSlotKeys);
            return;
        }

        if (delta is NetworkChangeSiegeEngineUndeployed undeployed)
            ReplayUndeployedSlotDeltaLocked(undeployed, replayedSlotKeys);
    }

    private void ReplayDeployedSlotDeltaLocked(NetworkChangeSiegeEngineDeployed deployed,
        HashSet<string> replayedSlotKeys)
    {
        replayedSlotKeys.Add(SlotKey(deployed.ContainerId, deployed.IsRanged, deployed.Index));
        if (TryAdvanceSlotRevisionLocked(deployed.RevisionEpoch, deployed.ContainerId, deployed.IsRanged,
                deployed.Index, deployed.SlotRevision, deployed))
        {
            RecordObservedStateLocked(deployed);
            PublishDeployed(deployed);
        }
        else
            RecordEqualRevisionProofLocked(deployed);
    }

    private void ReplayUndeployedSlotDeltaLocked(NetworkChangeSiegeEngineUndeployed undeployed,
        HashSet<string> replayedSlotKeys)
    {
        replayedSlotKeys.Add(SlotKey(undeployed.ContainerId, undeployed.IsRanged, undeployed.Index));
        if (TryAdvanceSlotRevisionLocked(undeployed.RevisionEpoch, undeployed.ContainerId, undeployed.IsRanged,
                undeployed.Index, undeployed.SlotRevision, undeployed))
        {
            RecordObservedStateLocked(undeployed);
            PublishUndeployed(undeployed);
        }
        else
            RecordEqualRevisionProofLocked(undeployed);
    }

    private void MergeSlotRevisionSnapshotLocked(IEnumerable<SiegeEngineSlotRevision> snapshotSlots)
    {
        foreach (var slot in snapshotSlots ?? Array.Empty<SiegeEngineSlotRevision>())
        {
            var key = SlotKey(slot.ContainerId, slot.IsRanged, slot.Index);
            if (!slotRevisions.TryGetValue(key, out var current) || slot.Revision > current)
                slotRevisions[key] = slot.Revision;
        }
    }

    private void FlushPendingLocalRequestsLocked(HashSet<string> replayedSlotKeys)
    {
        foreach (var request in pendingLocalRequests)
        {
            var key = SlotKey(request.ContainerId, request.IsRanged, request.Index);
            if (replayedSlotKeys.Contains(key))
            {
                // There was no epoch/generation available when the click was captured, so even an ABA
                // replay (A -> B -> A) is indistinguishable from the original A. Never retarget stale UI
                // intent to the post-save generation; the user can act again on the state now displayed.
                Logger.Warning("Ignoring pre-snapshot siege-engine request for {Slot}: buffered deltas changed that slot before the revision snapshot",
                    key);
                continue;
            }

            SendSlotRequestLocked(request);
        }
        pendingLocalRequests.Clear();
    }

    private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
    {
        lock (revisionGate)
        {
            revisionEpoch = null;
            slotRevisions.Clear();
            observedSlotStates.Clear();
            pendingSlotDeltas.Clear();
            pendingLocalRequests.Clear();
        }
    }

    private void HandleReserveAdded(MessagePayload<NetworkChangeSiegeEngineReserveAdded> payload)
    {
        var obj = payload.What;
        messageBroker.Publish(this, new ChangeSiegeEngineReserveAdded(obj.ContainerId, obj.SiegeEngineId, obj.EngineTypeId));
    }

    private void HandleReserveRemoved(MessagePayload<NetworkChangeSiegeEngineReserveRemoved> payload)
    {
        var obj = payload.What;
        messageBroker.Publish(this, new ChangeSiegeEngineReserveRemoved(obj.ContainerId, obj.SiegeEngineId));
    }

    private void HandleProgress(MessagePayload<NetworkChangeSiegeEngineProgress> payload)
    {
        var obj = payload.What;
        messageBroker.Publish(this, new ChangeSiegeEngineProgress(obj.SiegeEngineId, obj.IsRedeployment, obj.Value));
    }

    private void HandleHitpoints(MessagePayload<NetworkChangeSiegeEngineHitpoints> payload)
    {
        var obj = payload.What;
        messageBroker.Publish(this, new ChangeSiegeEngineHitpoints(obj.SiegeEngineId, obj.Hitpoints, obj.MaxHitPoints));
    }

    private void HandleMissileAdded(MessagePayload<NetworkAddSiegeEngineMissile> payload)
    {
        var obj = payload.What;
        messageBroker.Publish(this, new ApplySiegeEngineMissile(obj.SiegeEventId, obj.Side, obj.ShooterEngineTypeId,
            obj.ShooterSlotIndex, obj.TargetType, obj.TargetSlotIndex, obj.TargetSiegeEngineId,
            obj.CollisionTicks, obj.FireTicks, obj.HitSuccessful));
    }

    // Runs on the game thread already — published from the production-popup container patch; only resolves an id and sends, so no GameThread.RunSafe.
    private void HandleDeployRequested(MessagePayload<SiegeEngineDeployRequested> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.SiegeEvent, out var siegeEventId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;
        if (!TryGetExpectedOccupantId(obj.ExpectedOccupant, out var expectedOccupantId)) return;

        QueueOrSendSlotRequest(new PendingSlotRequest
        {
            IsDeploy = true,
            SiegeEventId = siegeEventId,
            ContainerId = containerId,
            Side = (int)obj.Side,
            EngineTypeId = obj.EngineType.StringId,
            Index = obj.Index,
            IsRanged = obj.EngineType.IsRanged,
            ExpectedOccupantId = expectedOccupantId,
        });
    }

    private void HandleRemovalRequested(MessagePayload<SiegeEngineRemovalRequested> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.SiegeEvent, out var siegeEventId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Container, out var containerId)) return;
        if (!TryGetExpectedOccupantId(obj.ExpectedOccupant, out var expectedOccupantId)) return;

        QueueOrSendSlotRequest(new PendingSlotRequest
        {
            SiegeEventId = siegeEventId,
            ContainerId = containerId,
            Side = (int)obj.Side,
            Index = obj.Index,
            IsRanged = obj.IsRanged,
            MoveToReserve = obj.MoveToReserve,
            ExpectedOccupantId = expectedOccupantId,
        });
    }

    // Null is a meaningful expectation (the slot must still be empty). A non-null occupant must have the
    // replicated server id; silently downgrading an unresolved occupant to null would turn a replace/remove
    // click into an operation against a different slot generation.
    private bool TryGetExpectedOccupantId(SiegeEngineConstructionProgress expectedOccupant, out string expectedOccupantId)
    {
        expectedOccupantId = null;
        return expectedOccupant == null
            || objectManager.TryGetIdWithLogging(expectedOccupant, out expectedOccupantId);
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

    private static string ValidateDeployWireShape(NetworkRequestDeploySiegeEngine request) =>
        string.IsNullOrWhiteSpace(request.SiegeEventId) || request.SiegeEventId.Length > 256 ||
        string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > 256 ||
        string.IsNullOrWhiteSpace(request.EngineTypeId) || request.EngineTypeId.Length > 256 ||
        request.Side != (int)BattleSideEnum.Attacker || request.Index < 0 || request.Index > 31 ||
        request.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(request.RevisionEpoch) ||
        request.RevisionEpoch.Length > 64 ||
        (request.ExpectedOccupantId != null && request.ExpectedOccupantId.Length > 256)
            ? "invalid-siege-engine-deploy" : null;

    private static string ValidateRemoveWireShape(NetworkRequestRemoveSiegeEngine request) =>
        string.IsNullOrWhiteSpace(request.SiegeEventId) || request.SiegeEventId.Length > 256 ||
        string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > 256 ||
        request.Side != (int)BattleSideEnum.Attacker || request.Index < 0 || request.Index > 31 ||
        request.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(request.RevisionEpoch) ||
        request.RevisionEpoch.Length > 64 ||
        (request.ExpectedOccupantId != null && request.ExpectedOccupantId.Length > 256)
            ? "invalid-siege-engine-remove" : null;

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

    private static NetworkSiegeEngineCommandResult CreateDeployTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason),
            SiegeEngineCommandKind.Deploy, null, null, 0, 0, false, null, null, 0, null);

    private static NetworkSiegeEngineCommandResult CreateRemoveTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason),
            SiegeEngineCommandKind.Remove, null, null, 0, 0, false, null, null, 0, null);

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

    private AuthorityCommitProbeResult ProbeDeployCommit(NetworkSiegeEngineCommandResult result) =>
        ProbeCommit(result, expectDeployed: true);

    private AuthorityCommitProbeResult ProbeRemoveCommit(NetworkSiegeEngineCommandResult result) =>
        ProbeCommit(result, expectDeployed: false);

    private AuthorityCommitProbeResult ProbeCommit(NetworkSiegeEngineCommandResult result, bool expectDeployed)
    {
        lock (revisionGate)
        {
            string key = CommitKey(result.RevisionEpoch, result.ContainerId, result.IsRanged, result.Index,
                result.SlotRevision);
            if (!observedSlotStates.TryGetValue(key, out var state) ||
                state.IsDeployed != expectDeployed ||
                state.RequestId != result.Header.RequestId ||
                !string.Equals(state.SessionId, result.Header.SessionId, StringComparison.Ordinal) ||
                (expectDeployed && (!string.Equals(state.SiegeEngineId, result.SiegeEngineId, StringComparison.Ordinal) ||
                                    !string.Equals(state.EngineTypeId, result.EngineTypeId, StringComparison.Ordinal))))
                return AuthorityCommitProbeResult.Pending;
        }

        if (!objectManager.TryGetObject<SiegeEnginesContainer>(result.ContainerId, out var container))
            return AuthorityCommitProbeResult.Pending;

        var slot = GetSlot(container, result.IsRanged, result.Index);
        if (!expectDeployed) return slot == null ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Invalid;
        if (slot == null || !objectManager.TryGetId(slot, out var slotId)) return AuthorityCommitProbeResult.Pending;
        return string.Equals(slotId, result.SiegeEngineId, StringComparison.Ordinal) &&
               string.Equals(slot.SiegeEngine?.StringId, result.EngineTypeId, StringComparison.Ordinal)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Invalid;
    }

    // The production click gate closes the selection popup before this request is submitted. A terminal
    // failure therefore needs no rollback of a local mutation; leaving the popup closed prevents a stale
    // click from being replayed against a later slot generation.
    private static void PresentTerminalOutcome(AuthorityClientOutcome<NetworkSiegeEngineCommandResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Siege-engine authority request did not apply. Reason={Reason}", outcome.ReasonCode);
    }

    private static string SlotKey(string containerId, bool isRanged, int index)
    {
        return containerId + "|" + (isRanged ? "r" : "m") + "|" + index;
    }

    private static string CommitKey(string epoch, string containerId, bool isRanged, int index, long revision) =>
        epoch + "|" + SlotKey(containerId, isRanged, index) + "|" + revision;

    private void RecordObservedStateLocked(NetworkChangeSiegeEngineDeployed deployed)
    {
        observedSlotStates[CommitKey(deployed.RevisionEpoch, deployed.ContainerId, deployed.IsRanged, deployed.Index,
            deployed.SlotRevision)] = new ObservedSlotState
        {
            IsDeployed = true,
            SiegeEngineId = deployed.SiegeEngineId,
            EngineTypeId = deployed.EngineTypeId,
            SessionId = deployed.AuthoritySessionId,
            RequestId = deployed.AuthorityRequestId,
        };
    }

    private void RecordObservedStateLocked(NetworkChangeSiegeEngineUndeployed undeployed)
    {
        observedSlotStates[CommitKey(undeployed.RevisionEpoch, undeployed.ContainerId, undeployed.IsRanged, undeployed.Index,
            undeployed.SlotRevision)] = new ObservedSlotState
        {
            IsDeployed = false,
            MoveToReserve = undeployed.MoveToReserve,
            SessionId = undeployed.AuthoritySessionId,
            RequestId = undeployed.AuthorityRequestId,
        };
    }

    private void RecordEqualRevisionProofLocked(NetworkChangeSiegeEngineDeployed deployed)
    {
        if (deployed.AuthorityRequestId <= 0 ||
            !string.Equals(revisionEpoch, deployed.RevisionEpoch, StringComparison.Ordinal))
            return;

        string key = CommitKey(deployed.RevisionEpoch, deployed.ContainerId, deployed.IsRanged, deployed.Index,
            deployed.SlotRevision);
        if (!observedSlotStates.TryGetValue(key, out var state) || !state.IsDeployed ||
            !string.Equals(state.SiegeEngineId, deployed.SiegeEngineId, StringComparison.Ordinal) ||
            !string.Equals(state.EngineTypeId, deployed.EngineTypeId, StringComparison.Ordinal))
        {
            Logger.Error("Rejecting conflicting equal-revision siege-engine deployment proof for {Slot}", key);
            return;
        }

        state.SessionId = deployed.AuthoritySessionId;
        state.RequestId = deployed.AuthorityRequestId;
    }

    private void RecordEqualRevisionProofLocked(NetworkChangeSiegeEngineUndeployed undeployed)
    {
        if (undeployed.AuthorityRequestId <= 0 ||
            !string.Equals(revisionEpoch, undeployed.RevisionEpoch, StringComparison.Ordinal))
            return;

        string key = CommitKey(undeployed.RevisionEpoch, undeployed.ContainerId, undeployed.IsRanged, undeployed.Index,
            undeployed.SlotRevision);
        if (!observedSlotStates.TryGetValue(key, out var state) || state.IsDeployed ||
            state.MoveToReserve != undeployed.MoveToReserve)
        {
            Logger.Error("Rejecting conflicting equal-revision siege-engine removal proof for {Slot}", key);
            return;
        }

        state.SessionId = undeployed.AuthoritySessionId;
        state.RequestId = undeployed.AuthorityRequestId;
    }

    private static SiegeEngineConstructionProgress GetSlot(SiegeEnginesContainer container, bool isRanged, int index)
    {
        var slots = isRanged ? container.DeployedRangedSiegeEngines : container.DeployedMeleeSiegeEngines;
        return index >= 0 && index < slots.Length ? slots[index] : null;
    }

    private void QueueOrSendSlotRequest(PendingSlotRequest request)
    {
        lock (revisionGate)
        {
            if (revisionEpoch == null)
            {
                // A post-save delta may be waiting ahead of the snapshot. Preserve the occupant visible when
                // the click was raised; snapshot handling discards this intent if replay touched its slot.
                pendingLocalRequests.Add(request);
                return;
            }

            SendSlotRequestLocked(request);
        }
    }

    // Caller holds revisionGate so an accepted delta cannot advance the slot between reading the expected
    // generation and placing the conditional command on the connection.
    private void SendSlotRequestLocked(PendingSlotRequest request)
    {
        var key = SlotKey(request.ContainerId, request.IsRanged, request.Index);
        long expectedRevision = slotRevisions.TryGetValue(key, out var revision) ? revision : 0L;

        if (request.IsDeploy)
        {
            deployRoute.Submit(new SiegeEngineDeployIntent(request, expectedRevision, revisionEpoch));
            return;
        }

        removeRoute.Submit(new SiegeEngineRemoveIntent(request, expectedRevision, revisionEpoch));
    }

    // Caller holds revisionGate through both this state transition and publication of the corresponding
    // ChangeSiegeEngine* command, preserving wire order across concurrent network delivery.
    private bool TryAdvanceSlotRevisionLocked(
        string epoch,
        string containerId,
        bool isRanged,
        int index,
        long revision,
        IMessage delta)
    {
        if (string.IsNullOrEmpty(epoch)) return false;

        // The connection queue flushes post-save deltas before the revision snapshot. Hold them in exact
        // wire order until that snapshot establishes the epoch; only then may they mutate the loaded state.
        if (revisionEpoch == null)
        {
            pendingSlotDeltas.Add(delta);
            return false;
        }

        // Only an authoritative snapshot may change epochs.
        if (!string.Equals(revisionEpoch, epoch, StringComparison.Ordinal)) return false;

        var key = SlotKey(containerId, isRanged, index);
        long current = slotRevisions.TryGetValue(key, out var existing) ? existing : 0L;
        if (revision <= current) return false;

        slotRevisions[key] = revision;
        return true;
    }

    private static bool DeltaBelongsToEpoch(IMessage delta, string epoch)
    {
        if (delta is NetworkChangeSiegeEngineDeployed deployed)
            return string.Equals(deployed.RevisionEpoch, epoch, StringComparison.Ordinal);
        if (delta is NetworkChangeSiegeEngineUndeployed undeployed)
            return string.Equals(undeployed.RevisionEpoch, epoch, StringComparison.Ordinal);
        return false;
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineDeployed>(HandleDeployed);
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineUndeployed>(HandleUndeployed);
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineReserveAdded>(HandleReserveAdded);
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineReserveRemoved>(HandleReserveRemoved);
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineProgress>(HandleProgress);
        messageBroker.Unsubscribe<NetworkChangeSiegeEngineHitpoints>(HandleHitpoints);
        messageBroker.Unsubscribe<NetworkAddSiegeEngineMissile>(HandleMissileAdded);
        messageBroker.Unsubscribe<NetworkSyncSiegeEngineSlotRevisions>(HandleSlotRevisionSnapshot);
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Unsubscribe<SiegeEngineDeployRequested>(HandleDeployRequested);
        messageBroker.Unsubscribe<SiegeEngineRemovalRequested>(HandleRemovalRequested);
        deployRoute.Dispose();
        removeRoute.Dispose();
    }
}
