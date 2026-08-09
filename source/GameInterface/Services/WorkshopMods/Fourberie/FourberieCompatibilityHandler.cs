using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Late-bound safety boundary for Fourberie 1.4.7.5. The original assembly stays a separate
/// runtime module, while this adapter blocks its overlapping campaign/model surface and
/// unrouteable singleton-player actions, fingerprints external configuration, and supplies a
/// revisioned stable-ID snapshot format for its audited persisted fields.
/// </summary>
internal sealed class FourberieCompatibilityHandler : IHandler, IFourberiePatchRuntime
{
    private static readonly ILogger Logger = LogManager.GetLogger<FourberieCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly Harmony harmony;
    private readonly HashSet<string> notifiedActions = new HashSet<string>(StringComparer.Ordinal);
    private readonly FourberieRevisionGate revisionGate = new FourberieRevisionGate();
    private readonly object snapshotSync = new object();

    private Assembly assembly;
    private string configurationFingerprint;
    private string lastPublishedFingerprint;
    private long serverRevision;
    private bool compatible;
    private bool limitationNoticeShown;
    private bool stateReady;

    public FourberieCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        Harmony _)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.harmony = new Harmony(FourberieCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) FourberiePatchRuntime.Current = this;

        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkRequestFourberieState>(HandleStateRequest);
        messageBroker.Subscribe<NetworkFourberieState>(HandleState);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkRequestFourberieState>(HandleStateRequest);
        messageBroker.Unsubscribe<NetworkFourberieState>(HandleState);
        if (ReferenceEquals(FourberiePatchRuntime.Current, this)) FourberiePatchRuntime.Current = null;
    }

    public void NotifyUnsupported(string method)
    {
        method ??= "unknown Fourberie action";
        if (!notifiedActions.Add(method)) return;

        var message = $"Fourberie entry point '{method}' is disabled in co-op: its singleton campaign/model flow has no validated controller-authorized authority route.";
        Logger.Warning(message);
        if (ModInformation.IsClient)
            InformationManager.DisplayMessage(new InformationMessage(message));
    }

    public void PublishIfChanged()
    {
        if (!compatible || !stateReady || !ModInformation.IsServer) return;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
    }

    private bool TryInstall()
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                FourberieCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.Debug("Fourberie is not loaded; compatibility adapter is inactive");
            return false;
        }

        if (!FourberieCompatibilityManifest.TryValidate(assembly, out var methods, out var failure))
        {
            Logger.Fatal("Fourberie co-op adapter failed closed before patching: {Failure}", failure);
            throw new InvalidOperationException(
                "Unsupported Fourberie binary/API. Coop startup was aborted so the unguarded mod cannot mutate the rendered client campaign: " +
                failure);
        }

        if (!FourberieConfigurationFingerprint.TryComputeForAssembly(
                assembly,
                out configurationFingerprint,
                out var selectedFiles,
                out failure))
        {
            Logger.Fatal("Fourberie co-op adapter failed closed: {Failure}", failure);
            throw new InvalidOperationException(
                "Fourberie configuration could not be canonicalized. Coop startup was aborted before Fourberie campaign code could run: " +
                failure);
        }

        lock (PatchSync)
        {
            FourberieHarmonyIsolation.PurgeAndAssertAuditedSurface(assembly, harmony);

            if (!FourberieRuntimeSurface.TryAssertNoActiveCampaignSurface(assembly, out failure))
            {
                Logger.Fatal("Fourberie co-op adapter found an already-active incompatible runtime: {Failure}", failure);
                throw new InvalidOperationException(failure);
            }

            var expected = methods
                .Select(pair =>
                {
                    var prefix = PrefixFor(pair.Key.Kind);
                    var postfix = pair.Key.Kind == FourberiePatchKind.ServerTick ||
                                  pair.Key.Kind == FourberiePatchKind.ServerMutation
                        ? AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ServerTickPostfix))
                        : null;
                    return (Original: pair.Value, Prefix: prefix, Postfix: postfix);
                })
                .ToArray();

            if (ReferenceEquals(patchedAssembly, assembly))
            {
                FourberieHarmonyIsolation.AssertOnlyAdapterGuards(expected, harmony.Id);
                return true;
            }

            var applied = new List<(MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)>();
            try
            {
                foreach (var guard in expected)
                {
                    harmony.Patch(
                        guard.Original,
                        prefix: new HarmonyMethod(guard.Prefix),
                        postfix: guard.Postfix == null ? null : new HarmonyMethod(guard.Postfix));
                    applied.Add(guard);
                }

                FourberieHarmonyIsolation.AssertOnlyAdapterGuards(expected, harmony.Id);
                var modulePatches = FourberieHarmonyIsolation.DescribeAssemblyPatches(assembly).ToArray();
                if (modulePatches.Length != 0)
                    throw new InvalidOperationException(
                        "Fourberie assembly-owned Harmony patches appeared while installing Coop guards: " +
                        string.Join("; ", modulePatches));

                patchedAssembly = assembly;
            }
            catch (Exception exception)
            {
                foreach (var patch in applied)
                {
                    harmony.Unpatch(patch.Original, patch.Prefix);
                    if (patch.Postfix != null) harmony.Unpatch(patch.Original, patch.Postfix);
                }
                Logger.Fatal(exception, "Fourberie co-op patching failed; rolled back adapter detours");
                throw new InvalidOperationException(
                    "Fourberie co-op guard installation failed. Coop startup was aborted after rolling back partial detours.",
                    exception);
            }
        }

        Logger.Information(
            "Fourberie {Version} co-op feature-blocking adapter enabled ({Methods} guarded methods, config {Fingerprint}, files {Files})",
            FourberieCompatibilityManifest.SupportedModuleVersion,
            methods.Count,
            configurationFingerprint,
            string.Join(", ", selectedFiles.Select(System.IO.Path.GetFileName)));
        return true;
    }

    private static MethodInfo PrefixFor(FourberiePatchKind kind)
    {
        string method;
        switch (kind)
        {
            case FourberiePatchKind.ServerTick:
            case FourberiePatchKind.ServerMutation:
                method = nameof(FourberieAuthorityPatches.ServerTickPrefix);
                break;
            case FourberiePatchKind.UnsupportedPlayerAction:
                method = nameof(FourberieAuthorityPatches.UnsupportedPlayerActionPrefix);
                break;
            case FourberiePatchKind.ClientPresentation:
                method = nameof(FourberieAuthorityPatches.ClientPresentationPrefix);
                break;
            case FourberiePatchKind.FinanceRead:
                method = nameof(FourberieAuthorityPatches.FinanceReadPrefix);
                break;
            default:
                method = nameof(FourberieAuthorityPatches.ServerOnlyPrefix);
                break;
        }

        return AccessTools.Method(typeof(FourberieAuthorityPatches), method);
    }

    private void HandleAllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> _)
    {
        if (!compatible) return;

        if (!FourberieRuntimeSurface.TryAssertNoActiveCampaignSurface(assembly, out var runtimeFailure))
            throw new InvalidOperationException(runtimeFailure);

        FourberieAuthorityPatches.ResetTickLedger();
        lock (snapshotSync) revisionGate.Reset();
        stateReady = true;
        if (ModInformation.IsClient)
        {
            ShowLimitationNotice();
            network.SendAll(new NetworkRequestFourberieState());
            return;
        }

        serverRevision = 0;
        lastPublishedFingerprint = null;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: false);
    }

    private void HandleStateRequest(MessagePayload<NetworkRequestFourberieState> payload)
    {
        if (!compatible || !ModInformation.IsServer || payload.Who is not NetPeer peer) return;
        GameThread.RunSafe(
            () => SendSnapshotOrAbort(peer, onlyIfChanged: false),
            context: nameof(FourberieCompatibilityHandler));
    }

    private void HandleState(MessagePayload<NetworkFourberieState> payload)
    {
        // A network command is published with its transport peer as the broker source. A
        // rendered client has one server connection, so requiring that transport identity
        // rejects locally published/forged snapshots without trusting payload fields.
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !FourberieSnapshotOriginGuard.IsTrustedServerTransport(serverPeer, localIsClient: true))
            return;
        GameThread.RunSafe(
            () =>
            {
                if (TryApplySnapshot(payload.What, out var failure)) return;

                Logger.Fatal(
                    "Disconnecting from the Coop server because Fourberie state could not be accepted: {Failure}",
                    failure);
                InformationManager.DisplayMessage(new InformationMessage(
                    "Fourberie compatibility validation failed. The co-op connection was closed to prevent a divergent campaign. " +
                    failure));
                serverPeer.Disconnect();
            },
            context: nameof(FourberieCompatibilityHandler));
    }

    private void SendSnapshotOrAbort(NetPeer peer, bool onlyIfChanged)
    {
        if (!FourberieCanonicalState.TryCapture(
                assembly,
                objectManager,
                out var entries,
                out var stateFingerprint,
                out var failure))
        {
            DenyPeerOrAbortSession(peer, "could not capture Fourberie server state: " + failure);
            return;
        }

        var changed = !string.Equals(
            lastPublishedFingerprint,
            stateFingerprint,
            StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            if (onlyIfChanged) return;
        }

        var nextRevision = serverRevision;
        if (changed && lastPublishedFingerprint != null) nextRevision++;

        var snapshot = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            nextRevision,
            configurationFingerprint,
            stateFingerprint,
            entries);
        if (!FourberieStateCodec.TryValidate(snapshot, out failure))
        {
            DenyPeerOrAbortSession(peer, "captured Fourberie server state was invalid: " + failure);
            return;
        }
        var broadcast = peer == null || changed;
        try
        {
            // If a late-join request discovers state newer than the last broadcast, publish that
            // revision to every peer. Advancing the global fingerprint after replying only to the
            // requester would make the ordinary change detector suppress the update forever for
            // already-connected clients.
            if (broadcast) network.SendAll(snapshot);
            else network.Send(peer, snapshot);
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(
                broadcast ? null : peer,
                "authoritative snapshot publication failed: " + exception.Message);
            return;
        }

        // Publication and the server watermark commit form one operation: a failed send must not
        // make a later retry look unchanged.
        if (changed)
        {
            serverRevision = nextRevision;
            lastPublishedFingerprint = stateFingerprint;
        }
    }

    private bool TryApplySnapshot(NetworkFourberieState snapshot, out string rejection)
    {
        lock (snapshotSync)
        {
            if (!FourberieStateCodec.TryValidate(snapshot, out var failure))
            {
                Logger.Error("Rejected Fourberie snapshot: {Failure}", failure);
                rejection = failure;
                return false;
            }

            if (!string.Equals(
                    configurationFingerprint,
                    snapshot.ConfigurationFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                var message = $"Fourberie configuration mismatch: local {configurationFingerprint}, server {snapshot.ConfigurationFingerprint}. Reinstall the private bundle; state was not applied.";
                Logger.Fatal(message);
                rejection = message;
                return false;
            }

            var decision = revisionGate.Evaluate(snapshot.Revision, snapshot.StateFingerprint);
            if (decision == FourberieRevisionDecision.AlreadyApplied)
            {
                rejection = null;
                return true;
            }
            if (decision != FourberieRevisionDecision.Apply)
            {
                Logger.Error(
                    "Rejected Fourberie snapshot revision {Revision} ({Decision}); local revision is {LocalRevision}",
                    snapshot.Revision,
                    decision,
                    revisionGate.Revision);
                rejection = $"snapshot revision {snapshot.Revision} was {decision} against local revision {revisionGate.Revision}";
                return false;
            }

            if (!FourberieCanonicalState.TryBeginApply(
                    assembly,
                    objectManager,
                    snapshot.Entries,
                    out var transaction,
                    out failure))
            {
                Logger.Error("Could not apply Fourberie snapshot revision {Revision}: {Failure}", snapshot.Revision, failure);
                rejection = failure;
                return false;
            }

            if (!revisionGate.Commit(snapshot.Revision, snapshot.StateFingerprint))
            {
                transaction.TryRollback(out var rollbackFailure);
                Logger.Error(
                    "Fourberie snapshot revision changed while applying; state rollback result: {Rollback}",
                    rollbackFailure ?? "success");
                rejection = "snapshot revision changed while applying" +
                            (rollbackFailure == null ? string.Empty : "; " + rollbackFailure);
                return false;
            }

            transaction.Commit();
            Logger.Debug(
                "Atomically applied Fourberie state revision {Revision} ({Fingerprint})",
                snapshot.Revision,
                snapshot.StateFingerprint);
            rejection = null;
            return true;
        }
    }

    private static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Fourberie state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal("Aborting the Coop session because authoritative Fourberie state is unavailable: {Failure}", failure);
        throw new InvalidOperationException(
            "The Coop session cannot continue without authoritative Fourberie state: " + failure);
    }

    private void ShowLimitationNotice()
    {
        if (limitationNoticeShown) return;
        limitationNoticeShown = true;
        InformationManager.DisplayMessage(new InformationMessage(
            "Fourberie co-op guard is active. Its campaign behaviors, model replacements, menus, shortcuts, conversations, and missions are blocked, not integrated. Only presentation assets remain available."));
    }
}
