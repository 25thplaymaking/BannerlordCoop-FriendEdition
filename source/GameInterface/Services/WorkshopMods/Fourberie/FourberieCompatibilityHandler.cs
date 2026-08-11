using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
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
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly Harmony harmony;
    private readonly FourberieRevisionGate revisionGate = new FourberieRevisionGate();
    private readonly object snapshotSync = new object();
    private readonly FourberieRequestLedger<NetPeer> requestLedger = new FourberieRequestLedger<NetPeer>(256);
    private readonly Dictionary<long, FourberieOperation> pendingOperations = new Dictionary<long, FourberieOperation>();

    private Assembly assembly;
    private string configurationFingerprint;
    private string lastPublishedFingerprint;
    private long serverRevision;
    private long nextRequestId;
    private bool compatible;
    private bool stateReady;
    private FourberieOperationExecutor operationExecutor;

    public FourberieCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        Harmony _)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.harmony = new Harmony(FourberieCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) FourberiePatchRuntime.Current = this;

        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkRequestFourberieState>(HandleStateRequest);
        messageBroker.Subscribe<NetworkRequestFourberieOperation>(HandleOperationRequest);
        messageBroker.Subscribe<NetworkFourberieOperationResult>(HandleOperationResult);
        messageBroker.Subscribe<NetworkFourberieState>(HandleState);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkRequestFourberieState>(HandleStateRequest);
        messageBroker.Unsubscribe<NetworkRequestFourberieOperation>(HandleOperationRequest);
        messageBroker.Unsubscribe<NetworkFourberieOperationResult>(HandleOperationResult);
        messageBroker.Unsubscribe<NetworkFourberieState>(HandleState);
        if (ReferenceEquals(FourberiePatchRuntime.Current, this)) FourberiePatchRuntime.Current = null;
    }

    public void PublishIfChanged()
    {
        if (!compatible || !stateReady || !ModInformation.IsServer) return;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
    }

    public bool TrySubmit(FourberieLocalOperation operation)
    {
        if (!ModInformation.IsClient || operation == null || !CanUseGameplayRoute(out var config))
            return false;

        string settlementId = string.Empty;
        string targetId = string.Empty;
        string secondaryTargetId = string.Empty;
        if (operation.Settlement != null && !objectManager.TryGetId(operation.Settlement, out settlementId))
            return false;
        if (operation.TargetHero != null && !objectManager.TryGetId(operation.TargetHero, out targetId))
            return false;
        if (operation.SecondarySettlement != null &&
            !objectManager.TryGetId(operation.SecondarySettlement, out secondaryTargetId))
            return false;

        var troops = new List<FourberieTroopSelection>();
        foreach (FourberieLocalTroopSelection troop in operation.Troops)
        {
            if (troop?.Troop == null || !objectManager.TryGetId(troop.Troop, out string troopId))
                return false;
            troops.Add(new FourberieTroopSelection(troopId, troop.Count));
        }

        long requestId = Interlocked.Increment(ref nextRequestId);
        var request = new NetworkRequestFourberieOperation(
            config.SessionId,
            requestId,
            revisionGate.Revision,
            operation.Operation,
            settlementId,
            targetId,
            secondaryTargetId,
            operation.IntValue,
            troops.ToArray());
        if (!FourberieOperationProtocol.IsRequestShapeValid(request)) return false;

        pendingOperations[requestId] = operation.Operation;
        network.SendAll(request);
        return true;
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

        operationExecutor = new FourberieOperationExecutor(assembly, objectManager);

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
                    MethodInfo postfix = pair.Key.Kind switch
                    {
                        FourberiePatchKind.ServerTick or FourberiePatchKind.ServerMutation =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ServerTickPostfix)),
                        FourberiePatchKind.ClientRoleRefresh =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientRoleRefreshPostfix)),
                        FourberiePatchKind.ClientCrimeRoomRead =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientCrimeRoomReadPostfix)),
                        FourberiePatchKind.ClientSchemeFilter =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientSchemeFilterPostfix)),
                        _ => null,
                    };
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
                // See HarmonyPatchInfoStabilizer for why this reads Harmony's inventory more than once
                // before failing closed: an unretried misread here aborts Coop startup outright, and
                // PurgeAndAssertAuditedSurface has already proved this surface empty a few lines above
                // with a retried remove-and-verify cycle inside the same lock.
                if (!HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(
                        () => !FourberieHarmonyIsolation.DescribeAssemblyPatches(assembly).Any()))
                {
                    throw new InvalidOperationException(
                        "Fourberie assembly-owned Harmony patches appeared while installing Coop guards: " +
                        string.Join("; ", FourberieHarmonyIsolation.DescribeAssemblyPatches(assembly)));
                }

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
            "Fourberie {Version} co-op authority adapter enabled ({Methods} routed methods, config {Fingerprint}, files {Files})",
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
            case FourberiePatchKind.BehaviorsAndModels:
                method = nameof(FourberieAuthorityPatches.InitializeBehaviorsAndModelsPrefix);
                break;
            case FourberiePatchKind.ClientOperationPresentation:
                method = nameof(FourberieAuthorityPatches.ClientOperationPresentationPrefix);
                break;
            case FourberiePatchKind.EnlistPartyConsequence:
                method = nameof(FourberieAuthorityPatches.EnlistPartyConsequencePrefix);
                break;
            case FourberiePatchKind.EnlistLadsConsequence:
                method = nameof(FourberieAuthorityPatches.EnlistLadsConsequencePrefix);
                break;
            case FourberiePatchKind.RecruitBanditsConsequence:
                method = nameof(FourberieAuthorityPatches.RecruitBanditsConsequencePrefix);
                break;
            case FourberiePatchKind.InsuranceScamConsequence:
                method = nameof(FourberieAuthorityPatches.InsuranceScamConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessStartConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessStartConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessUpgradeConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessUpgradeConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessDowngradeConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessDowngradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusUpgradeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusUpgradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusDowngradeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusDowngradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusResetConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusResetConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartyCreateConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartyCreateConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartyDisbandConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartyDisbandConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartySelectionConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartySelectionConsequencePrefix);
                break;
            case FourberiePatchKind.CrimeBaseResetConsequence:
                method = nameof(FourberieAuthorityPatches.CrimeBaseResetConsequencePrefix);
                break;
            case FourberiePatchKind.RoleSelectionPresentation:
                method = nameof(FourberieAuthorityPatches.ClientPresentationPrefix);
                break;
            case FourberiePatchKind.RoleAssignmentConsequence:
                method = nameof(FourberieAuthorityPatches.RoleAssignmentConsequencePrefix);
                break;
            case FourberiePatchKind.RoleRemovalConsequence:
                method = nameof(FourberieAuthorityPatches.RoleRemovalConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeVictimConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeVictimConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeTypeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeTypeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeLifecycleConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeLifecycleConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeOwnedReplacement:
                method = nameof(FourberieAuthorityPatches.SchemeOwnedReplacementPrefix);
                break;
            case FourberiePatchKind.SchemeStanceConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeStanceConsequencePrefix);
                break;
            case FourberiePatchKind.ClientRoleRefresh:
                method = nameof(FourberieAuthorityPatches.ClientRoleRefreshPrefix);
                break;
            case FourberiePatchKind.CorruptionLevelConsequence:
                method = nameof(FourberieAuthorityPatches.CorruptionLevelConsequencePrefix);
                break;
            case FourberiePatchKind.CrimeRoomSliderConsequence:
                method = nameof(FourberieAuthorityPatches.CrimeRoomSliderConsequencePrefix);
                break;
            case FourberiePatchKind.ClientCrimeRoomRead:
                method = nameof(FourberieAuthorityPatches.ClientCrimeRoomReadPrefix);
                break;
            case FourberiePatchKind.ContractConsequence:
                method = nameof(FourberieAuthorityPatches.ContractConsequencePrefix);
                break;
            case FourberiePatchKind.ClientSchemeFilter:
                method = nameof(FourberieAuthorityPatches.ClientSchemeFilterPrefix);
                break;
            case FourberiePatchKind.MainBaseConsequence:
                method = nameof(FourberieAuthorityPatches.MainBaseConsequencePrefix);
                break;
            case FourberiePatchKind.TerritorySelectionConsequence:
                method = nameof(FourberieAuthorityPatches.TerritorySelectionConsequencePrefix);
                break;
            case FourberiePatchKind.TerritoryAbandonConsequence:
                method = nameof(FourberieAuthorityPatches.TerritoryAbandonConsequencePrefix);
                break;
            case FourberiePatchKind.MissionInitialization:
                method = nameof(FourberieAuthorityPatches.MissionInitializationPrefix);
                break;
            case FourberiePatchKind.SeparatismLoyaltyComposition:
                method = nameof(FourberieAuthorityPatches.SeparatismLoyaltyCompositionPrefix);
                break;
            case FourberiePatchKind.RefreshHeroDicoOnly:
                method = nameof(FourberieAuthorityPatches.RefreshHeroDicoOnlyPrefix);
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
        FourberiePartyCommitSuppression.Reset();
        requestLedger.Reset();
        pendingOperations.Clear();
        nextRequestId = 0;
        lock (snapshotSync) revisionGate.Reset();
        stateReady = true;
        if (ModInformation.IsClient)
        {
            network.SendAll(new NetworkRequestFourberieState());
            return;
        }

        serverRevision = 0;
        lastPublishedFingerprint = null;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: false);
    }

    private bool CanUseGameplayRoute(out ModConfigSnapshot config)
    {
        config = null;
        return compatible && stateReady && configAuthority.TryGetCurrent(out config) &&
               capabilityRegistry.IsEnabled(FourberieCapabilitySource.ModuleId, FourberieCapabilitySource.Operation);
    }

    private void HandleOperationRequest(MessagePayload<NetworkRequestFourberieOperation> payload)
    {
        if (!ModInformation.IsServer || payload.Who is not NetPeer peer) return;
        GameThread.RunSafe(() => ApplyOperationRequest(peer, payload.What),
            context: nameof(FourberieCompatibilityHandler));
    }

    private void ApplyOperationRequest(NetPeer peer, NetworkRequestFourberieOperation request)
    {
        if (!FourberieOperationProtocol.IsRequestShapeValid(request) ||
            !CanUseGameplayRoute(out var config))
        {
            Logger.Warning("Rejected malformed or unavailable Fourberie operation from peer {Peer}", peer.Id);
            return;
        }
        if (!string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            SendOperationResult(peer, request, FourberieOperationStatus.StaleSession, config.SessionId);
            return;
        }

        string commandKey = FourberieOperationProtocol.CommandKey(request);
        FourberieReplayDecision replay = requestLedger.Inspect(
            peer, request.RequestId, commandKey, out NetworkFourberieOperationResult cached);
        if (replay == FourberieReplayDecision.Conflict)
        {
            DenyPeerOrAbortSession(peer,
                "reused Fourberie request ID " + request.RequestId + " with different payload");
            return;
        }
        if (replay == FourberieReplayDecision.Replay)
        {
            network.Send(peer, cached);
            SendSnapshotOrAbort(peer, onlyIfChanged: false);
            return;
        }
        if (!FourberieOperationProtocol.CanApplyAtRevision(
                request.Operation, request.ExpectedRevision, serverRevision))
        {
            SendOperationResult(peer, request, FourberieOperationStatus.StaleState, config.SessionId);
            SendSnapshotOrAbort(peer, onlyIfChanged: false);
            return;
        }
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty actorParty) ||
            actor == null || actorParty == null)
        {
            SendOperationResult(peer, request, FourberieOperationStatus.Rejected, config.SessionId);
            return;
        }

        FourberieOperationStatus status;
        try
        {
            status = operationExecutor.TryExecute(actor, actorParty, request, out string failure)
                ? FourberieOperationStatus.Accepted
                : FourberieOperationStatus.Rejected;
            if (failure != null)
                Logger.Warning("Rejected Fourberie operation {Operation} request {RequestId}: {Failure}",
                    request.Operation, request.RequestId, failure);
        }
        catch (Exception fatal)
        {
            DenyPeerOrAbortSession(null,
                "Fourberie operation " + request.RequestId + " could not roll back: " + fatal.Message);
            return;
        }

        if (status == FourberieOperationStatus.Accepted)
            SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
        var result = new NetworkFourberieOperationResult(
            config.SessionId, request.RequestId, status, serverRevision);
        requestLedger.Record(peer, request.RequestId, commandKey, result);
        network.Send(peer, result);
        if (status != FourberieOperationStatus.Accepted)
            SendSnapshotOrAbort(peer, onlyIfChanged: false);
    }

    private void SendOperationResult(
        NetPeer peer,
        NetworkRequestFourberieOperation request,
        FourberieOperationStatus status,
        string sessionId)
    {
        network.Send(peer, new NetworkFourberieOperationResult(
            sessionId, request.RequestId, status, serverRevision));
    }

    private void HandleOperationResult(MessagePayload<NetworkFourberieOperationResult> payload)
    {
        if (!ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !FourberieSnapshotOriginGuard.IsTrustedServerTransport(serverPeer, localIsClient: true) ||
            payload.What == null || !pendingOperations.TryGetValue(payload.What.RequestId, out FourberieOperation operation))
            return;
        pendingOperations.Remove(payload.What.RequestId);

        if (payload.What.Status == FourberieOperationStatus.Accepted)
        {
            if (!FourberieOperationProtocol.IsAbsoluteSetting(operation))
                InformationManager.DisplayMessage(new InformationMessage("Fourberie action accepted by the co-op server."));
            if (operation == FourberieOperation.StartInsuranceScam)
            {
                using (new AllowedThread())
                {
                    PlayerEncounter.LeaveSettlement();
                    PlayerEncounter.Finish(true);
                }
            }
            if (operation == FourberieOperation.AbandonTownCrimeBase)
            {
                Type behavior = assembly.GetType("Fourberie.FourberieBehavior", false, false);
                if (behavior != null)
                    AccessTools.Method(behavior, "DeleteVMLayer", Type.EmptyTypes)?.Invoke(null, null);
                if (Settlement.CurrentSettlement?.IsTown == true) GameMenu.SwitchToMenu("town");
            }
        }
        else
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "The Fourberie action could not be applied because its campaign state changed. Reopen the option and try again."));
        }
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
            RefreshClientLayout();
            Logger.Debug(
                "Atomically applied Fourberie state revision {Revision} ({Fingerprint})",
                snapshot.Revision,
                snapshot.StateFingerprint);
            rejection = null;
            return true;
        }
    }

    private void RefreshClientLayout()
    {
        if (!ModInformation.IsClient) return;
        try
        {
            Type behavior = assembly.GetType("Fourberie.FourberieBehavior", throwOnError: false, ignoreCase: false);
            object layer = behavior == null ? null : AccessTools.Field(behavior, "_layer")?.GetValue(null);
            if (layer != null)
                AccessTools.Method(layer.GetType(), "UpdateLayout", Type.EmptyTypes)?.Invoke(layer, null);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Fourberie state applied, but the open criminal-enterprise view could not refresh");
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

}
