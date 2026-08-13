using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal sealed class DiplomacyCompatibilityHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyCompatibilityHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IDiplomacyRuntime runtime;
    private readonly IModConfigAuthority configAuthority;
    private readonly IDiplomacyClientUiLifecycle uiLifecycle;
    private readonly object snapshotApplyGate = new();
    private readonly DiplomacyRevisionGate revisionGate = new();
    private readonly DiplomacySnapshotRequestGate<NetPeer> requestGate = new();
    private NetworkDiplomacySnapshot pendingSnapshot;
    private NetPeer pendingSnapshotPeer;
    private NetworkDiplomacySnapshot trustedSnapshot;
    private ModConfigSnapshot acceptedHostConfig;
    private bool campaignReady;
    private bool hostConfigLoaded;
    private bool loggedClientUiReadiness;

    internal DiplomacySnapshotApplyResult LastApplyResult { get; private set; }

    public DiplomacyCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IDiplomacyRuntime runtime,
        IModConfigAuthority configAuthority,
        IDiplomacyClientUiLifecycle uiLifecycle)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.runtime = runtime;
        this.configAuthority = configAuthority;
        this.uiLifecycle = uiLifecycle;

        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Subscribe<NetworkRequestDiplomacySnapshot>(HandleSnapshotRequest);
        messageBroker.Subscribe<NetworkDiplomacySnapshot>(HandleSnapshot);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfig);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Unsubscribe<NetworkRequestDiplomacySnapshot>(HandleSnapshotRequest);
        messageBroker.Unsubscribe<NetworkDiplomacySnapshot>(HandleSnapshot);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfig);
    }

    internal void HandleCampaignReady(MessagePayload<CampaignReady> _)
    {
        revisionGate.Reset();
        requestGate.Reset();
        pendingSnapshot = null;
        pendingSnapshotPeer = null;
        trustedSnapshot = null;
        acceptedHostConfig = null;
        hostConfigLoaded = false;
        loggedClientUiReadiness = false;
        campaignReady = false;
        if (!runtime.IsAvailable)
        {
            throw new System.InvalidOperationException(
                "Diplomacy runtime is unavailable for an enabled Friend Edition campaign. " +
                DiplomacyCompatibilityPolicy.DescribeResolutionFailure());
        }
        if (ModInformation.IsServer) runtime.ResetSnapshotRevision();
        uiLifecycle.ResetForCampaign();
        campaignReady = true;

        // Handler subscription order is not a trust boundary. If configuration authority already
        // committed the campaign snapshot, consume it; otherwise HostModConfigAccepted will resume
        // this handler after validation. Raw NetworkLoadModConfig is never observed here.
        if (configAuthority != null &&
            configAuthority.TryGetCurrent(out var accepted) &&
            configAuthority.IsCurrent(accepted))
        {
            AcceptHostConfig(accepted);
        }
    }

    internal void HandleSnapshotRequest(MessagePayload<NetworkRequestDiplomacySnapshot> payload)
    {
        if (!ModInformation.IsServer || !campaignReady || !HasCurrentHostConfig() ||
            payload?.Who is not NetPeer peer)
        {
            return;
        }

        // CampaignReady is raised on the joining client before NetworkPlayerCampaignEntered creates
        // its server-side Player mapping. The accepted mod-config identity is already pinned by the
        // module handshake, so use that completed barrier instead of a mapping that cannot exist yet.
        if (!payload.What.TryValidateWireShape(out string requestFailure))
        {
            Logger.Warning(
                "Disconnecting peer {Peer} after malformed Diplomacy snapshot request: {Failure}",
                peer.Id,
                requestFailure);
            peer.Disconnect();
            return;
        }
        if (!payload.What.Matches(acceptedHostConfig))
        {
            Logger.Warning(
                "Disconnecting peer {Peer} whose Diplomacy request did not match the accepted host configuration",
                peer.Id);
            peer.Disconnect();
            return;
        }
        if (!requestGate.TryAccept(peer)) return;

        Logger.Information(
            "Accepted pre-campaign Diplomacy snapshot request from peer {Peer}: config session={Session}, revision={Revision}",
            peer.Id,
            payload.What.ConfigSessionId,
            payload.What.ConfigRevision);

        GameThread.RunSafe(
            () => SendCurrentSnapshot(peer),
            true,
            nameof(DiplomacyCompatibilityHandler));
    }

    internal void HandleSnapshot(MessagePayload<NetworkDiplomacySnapshot> payload)
    {
        // The initial module/config handshake pins the authoritative transport peer. Requiring
        // that same object rejects locally published broker messages and any second/rogue peer,
        // even if a transport implementation later permits more than one client-side peer.
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer))
        {
            return;
        }

        if (!campaignReady || !HasCurrentHostConfig())
        {
            if (!DiplomacySnapshotCodec.TryValidate(payload.What, out var failure))
            {
                AbortClient(
                    serverPeer,
                    "Diplomacy snapshot received before host configuration was malformed: " + failure);
                return;
            }

            if (pendingSnapshot != null)
            {
                if (payload.What.Revision < pendingSnapshot.Revision) return;
                if (payload.What.Revision == pendingSnapshot.Revision &&
                    !string.Equals(
                        DiplomacySnapshotCodec.Identity(payload.What),
                        DiplomacySnapshotCodec.Identity(pendingSnapshot),
                        StringComparison.Ordinal))
                {
                    AbortClient(
                        serverPeer,
                        "Conflicting Diplomacy snapshots arrived before the host configuration barrier.");
                    return;
                }
            }

            pendingSnapshot = payload.What;
            pendingSnapshotPeer = serverPeer;
            return;
        }

        GameThread.RunSafe(
            () => ApplyTrustedSnapshot(
                payload.What,
                () => serverPeer.Disconnect(),
                message => InformationManager.DisplayMessage(new InformationMessage(message))),
            true,
            nameof(DiplomacyCompatibilityHandler));
    }

    internal void HandleHostModConfig(MessagePayload<HostModConfigAccepted> payload)
    {
        // This event is a local-only post-commit notification. A wire origin is always invalid,
        // and event identity alone is insufficient: the authority must still own this exact
        // session/revision/digest before Diplomacy can use it as a barrier.
        if (!IsTrustedHostConfig(payload)) return;

        var accepted = payload.What.Snapshot;
        GameThread.RunSafe(
            () => AcceptHostConfig(accepted),
            true,
            nameof(DiplomacyCompatibilityHandler));
    }

    internal bool IsTrustedHostConfig(MessagePayload<HostModConfigAccepted> payload) =>
        payload != null && payload.Who is not NetPeer && payload.What.Snapshot != null &&
        configAuthority != null && configAuthority.IsCurrent(payload.What.Snapshot);

    private void AcceptHostConfig(ModConfigSnapshot accepted)
    {
        if (accepted == null || configAuthority == null || !configAuthority.IsCurrent(accepted)) return;

        bool changed = !SameConfigIdentity(acceptedHostConfig, accepted);
        acceptedHostConfig = accepted;
        hostConfigLoaded = true;
        if (!campaignReady || !changed) return;

        if (ModInformation.IsServer)
        {
            SendCurrentSnapshot(peer: null);
            return;
        }
        if (!ModInformation.IsClient) return;

        if (pendingSnapshot != null)
        {
            var snapshot = pendingSnapshot;
            var peer = pendingSnapshotPeer;
            pendingSnapshot = null;
            pendingSnapshotPeer = null;
            if (peer == null)
            {
                Logger.Fatal("Rejected a queued Diplomacy snapshot without a trusted network peer.");
                return;
            }
            ApplyTrustedSnapshot(
                snapshot,
                () => peer.Disconnect(),
                message => InformationManager.DisplayMessage(new InformationMessage(message)));
            return;
        }

        Logger.Information(
            "Requesting authoritative Diplomacy snapshot: config session={Session}, revision={Revision}",
            acceptedHostConfig.SessionId,
            acceptedHostConfig.Revision);
        network.SendAll(new NetworkRequestDiplomacySnapshot(acceptedHostConfig));
    }

    private bool HasCurrentHostConfig() =>
        hostConfigLoaded && acceptedHostConfig != null &&
        configAuthority != null && configAuthority.IsCurrent(acceptedHostConfig);

    private static bool SameConfigIdentity(ModConfigSnapshot left, ModConfigSnapshot right) =>
        left != null && right != null &&
        left.ProtocolVersion == right.ProtocolVersion &&
        left.Revision == right.Revision &&
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);

    internal void ApplySnapshot(NetworkDiplomacySnapshot snapshot)
    {
        // Network paths are marshalled to the game thread, but keeping the revision check,
        // reflected mutation, and revision commit under one gate also protects direct/internal
        // callers. A concurrent equal revision must never mutate state and then lose its commit.
        lock (snapshotApplyGate) ApplySnapshotLocked(snapshot);
    }

    private void ApplySnapshotLocked(NetworkDiplomacySnapshot snapshot)
    {
        if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var validationFailure))
        {
            LastApplyResult = new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.MalformedSnapshot,
                validationFailure);
            LogRejected();
            return;
        }

        var revisionDecision = revisionGate.Evaluate(snapshot);
        switch (revisionDecision)
        {
            case DiplomacyRevisionDecision.AlreadyCurrent:
                LastApplyResult = new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.AlreadyCurrent);
                MarkClientUiReady();
                return;
            case DiplomacyRevisionDecision.Stale:
                LastApplyResult = new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.StaleRevision,
                    $"Snapshot revision {snapshot.Revision} is older than local revision {revisionGate.Revision}.");
                LogRejected();
                return;
            case DiplomacyRevisionDecision.Conflict:
                LastApplyResult = new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.RevisionConflict,
                    $"Snapshot revision {snapshot.Revision} conflicts with the already accepted fingerprints.");
                LogRejected();
                return;
            case DiplomacyRevisionDecision.Invalid:
                LastApplyResult = new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.MalformedSnapshot,
                    "Snapshot revision/fingerprint identity was invalid.");
                LogRejected();
                return;
        }

        LastApplyResult = runtime.ApplySnapshot(snapshot);
        if (!LastApplyResult.Succeeded)
        {
            LogRejected();
        }
        else
        {
            if (!revisionGate.Commit(snapshot))
            {
                LastApplyResult = new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.RevisionConflict,
                    "Snapshot revision changed while the state was being applied.");
                LogRejected();
                return;
            }
            trustedSnapshot = snapshot;
            if (!MarkClientUiReady()) return;
            Logger.Information(
                "Applied Diplomacy {Version} host settings/state snapshot revision {Revision}; " +
                "settings={SettingsFingerprint}, state={StateFingerprint}.",
                snapshot.AssemblyVersion,
                snapshot.Revision,
                snapshot.SettingsFingerprint,
                snapshot.StateFingerprint);
        }
    }

    private bool MarkClientUiReady()
    {
        if (uiLifecycle.TryMarkSnapshotReady(out var failure)) return true;

        LastApplyResult = new DiplomacySnapshotApplyResult(
            DiplomacySnapshotApplyStatus.ApplyFailed,
            "Diplomacy UI could not be enabled after the authoritative snapshot committed: " + failure);
        LogRejected();
        return false;
    }

    internal bool TryEnsureClientUiReady(out string failure)
    {
        lock (snapshotApplyGate)
        {
            if (!ModInformation.IsClient)
            {
                failure = null;
                return true;
            }
            if (trustedSnapshot == null)
            {
                failure = "No trusted authoritative Diplomacy snapshot has been applied for this campaign.";
                return false;
            }

            var readiness = runtime.ValidateUiReadiness(trustedSnapshot);
            if (!readiness.Succeeded)
            {
                Logger.Warning(
                    "Repairing Diplomacy client state before Kingdom UI construction ({Status}): {Detail}",
                    readiness.Status,
                    readiness.Detail);
                var repair = runtime.ApplySnapshot(trustedSnapshot);
                if (!repair.Succeeded)
                {
                    LastApplyResult = repair;
                    failure = "Authoritative Diplomacy client-state repair failed: " + repair.Detail;
                    return false;
                }

                readiness = runtime.ValidateUiReadiness(trustedSnapshot);
                if (!readiness.Succeeded)
                {
                    LastApplyResult = readiness;
                    failure = "Diplomacy client state was still unsafe after repair: " + readiness.Detail;
                    return false;
                }

                Logger.Information(
                    "Reapplied authoritative Diplomacy snapshot revision {Revision} before Kingdom UI construction",
                    trustedSnapshot.Revision);
            }

            if (!MarkClientUiReady())
            {
                failure = LastApplyResult.Detail;
                return false;
            }

            if (!loggedClientUiReadiness)
            {
                loggedClientUiReadiness = true;
                Logger.Information(
                    "Diplomacy Kingdom UI dependencies verified for authoritative revision {Revision}",
                    trustedSnapshot.Revision);
            }
            failure = null;
            return true;
        }
    }

    internal bool ApplyTrustedSnapshot(
        NetworkDiplomacySnapshot snapshot,
        System.Action abortConnection,
        System.Action<string> notify)
    {
        ApplySnapshot(snapshot);
        if (LastApplyResult.Succeeded) return true;

        string message =
            $"Diplomacy compatibility validation failed ({LastApplyResult.Status}). " +
            "The co-op connection was closed to prevent a divergent campaign. " +
            LastApplyResult.Detail;
        Logger.Fatal(message);
        notify?.Invoke(message);
        abortConnection?.Invoke();
        return false;
    }

    private void LogRejected() => Logger.Error(
        "Diplomacy host snapshot was rejected ({Status}): {Detail}",
        LastApplyResult.Status,
        LastApplyResult.Detail);

    private void SendCurrentSnapshot(NetPeer peer)
    {
        if (!runtime.IsAvailable)
        {
            DenyPeerOrAbortSession(peer, DiplomacyCompatibilityPolicy.DescribeResolutionFailure());
            return;
        }

        NetworkDiplomacySnapshot snapshot;
        try
        {
            snapshot = runtime.CaptureSnapshot();
        }
        catch (System.Exception ex)
        {
            DenyPeerOrAbortSession(peer, $"Diplomacy state capture threw: {ex.Message}");
            return;
        }
        if (snapshot == null)
        {
            DenyPeerOrAbortSession(peer, "authoritative Diplomacy state capture returned no snapshot");
            return;
        }
        if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var failure))
        {
            DenyPeerOrAbortSession(peer, "authoritative Diplomacy state was invalid: " + failure);
            return;
        }

        if (peer == null)
        {
            Logger.Information(
                "Broadcasting authoritative Diplomacy snapshot revision {Revision}; " +
                "settings={SettingsFingerprint}, state={StateFingerprint}",
                snapshot.Revision,
                snapshot.SettingsFingerprint,
                snapshot.StateFingerprint);
            network.SendAll(snapshot);
        }
        else
        {
            Logger.Information(
                "Sending authoritative Diplomacy snapshot revision {Revision}; " +
                "settings={SettingsFingerprint}, state={StateFingerprint} to peer {Peer}",
                snapshot.Revision,
                snapshot.SettingsFingerprint,
                snapshot.StateFingerprint,
                peer.Id);
            network.Send(peer, snapshot);
        }
    }

    private static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Diplomacy state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal("Aborting the Coop session because authoritative Diplomacy state is unavailable: {Failure}", failure);
        throw new System.InvalidOperationException(
            "The Coop session cannot continue without authoritative Diplomacy state: " + failure);
    }

    private static void AbortClient(NetPeer peer, string failure)
    {
        Logger.Fatal("Disconnecting from the Coop server: {Failure}", failure);
        InformationManager.DisplayMessage(new InformationMessage(
            failure + " The connection was closed to prevent campaign divergence."));
        peer?.Disconnect();
    }
}

/// <summary>Small bounded replay/rate gate for reflection-heavy state captures.</summary>
internal sealed class DiplomacySnapshotRequestGate<TPeer> where TPeer : class
{
    private const int MaximumPeers = 64;
    private readonly object sync = new();
    private readonly Dictionary<TPeer, long> lastAccepted = new();
    private readonly Func<long> timestamp;
    private readonly long minimumInterval;

    internal DiplomacySnapshotRequestGate(Func<long> timestamp = null, long minimumInterval = 0)
    {
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.minimumInterval = minimumInterval > 0 ? minimumInterval : Stopwatch.Frequency;
    }

    internal bool TryAccept(TPeer peer)
    {
        if (peer == null) return false;
        lock (sync)
        {
            long now = timestamp();
            if (lastAccepted.TryGetValue(peer, out long previous) &&
                now >= previous && now - previous < minimumInterval)
            {
                return false;
            }

            if (!lastAccepted.ContainsKey(peer) && lastAccepted.Count >= MaximumPeers)
            {
                var oldest = default(TPeer);
                long oldestTimestamp = long.MaxValue;
                foreach (var pair in lastAccepted)
                {
                    if (pair.Value >= oldestTimestamp) continue;
                    oldest = pair.Key;
                    oldestTimestamp = pair.Value;
                }
                if (oldest != null) lastAccepted.Remove(oldest);
            }

            lastAccepted[peer] = now;
            return true;
        }
    }

    internal void Reset()
    {
        lock (sync) lastAccepted.Clear();
    }
}

internal interface IDiplomacySnapshotPublisher : IGameAbstraction
{
    void PublishIfChanged();
}

internal sealed class DiplomacySnapshotPublisher : IDiplomacySnapshotPublisher
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacySnapshotPublisher>();

    private readonly INetwork network;
    private readonly IDiplomacyRuntime runtime;
    private long lastPublishedRevision = -1;

    public DiplomacySnapshotPublisher(INetwork network, IDiplomacyRuntime runtime)
    {
        this.network = network;
        this.runtime = runtime;
    }

    public void PublishIfChanged()
    {
        if (!ModInformation.IsServer) return;

        try
        {
            if (!runtime.IsAvailable)
            {
                throw new System.InvalidOperationException(
                    "Diplomacy runtime is unavailable: " +
                    DiplomacyCompatibilityPolicy.DescribeResolutionFailure());
            }

            var snapshot = runtime.CaptureSnapshot();
            if (snapshot == null)
                throw new System.InvalidOperationException(
                    "Authoritative Diplomacy state capture returned no snapshot.");
            if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var failure))
                throw new System.InvalidOperationException("Authoritative Diplomacy state was invalid: " + failure);

            if (snapshot.Revision < lastPublishedRevision) lastPublishedRevision = -1;
            if (snapshot.Revision == lastPublishedRevision) return;

            lastPublishedRevision = snapshot.Revision;
            network.SendAll(snapshot);
        }
        catch (System.Exception ex)
        {
            Logger.Fatal(ex, "Aborting because changed Diplomacy compatibility state could not be published.");
            throw new System.InvalidOperationException(
                "The Coop session cannot continue without authoritative Diplomacy state publication.",
                ex);
        }
    }
}
