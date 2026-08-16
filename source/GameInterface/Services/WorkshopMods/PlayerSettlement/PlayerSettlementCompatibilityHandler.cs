using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Registry.Messages;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Barters;
using HarmonyLib;
using LiteNetLib;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal sealed class PlayerSettlementConstructionIntent
{
    internal PlayerSettlementConstructionIntent(NetworkRequestPlayerSettlementConstruction request) => Request = request;
    internal NetworkRequestPlayerSettlementConstruction Request { get; }
}

internal readonly struct PlayerSettlementConstructionPostState
{
    internal PlayerSettlementConstructionPostState(string ownerId, string garrisonId)
    {
        OwnerId = ownerId ?? string.Empty;
        GarrisonId = garrisonId ?? string.Empty;
    }

    internal string OwnerId { get; }
    internal string GarrisonId { get; }
}

/// <summary>
/// Exact-binary Player Settlement 7.5.0 boundary. Generated XML is loaded only by the host before
/// Coop registry enumeration; clients receive the resulting object graph through the normal
/// registries and verify it against a revisioned metadata snapshot. Player placement commits remain
/// separately guarded until their controller-scoped transaction is installed.
/// </summary>
internal sealed class PlayerSettlementCompatibilityHandler : IHandler, IPlayerSettlementPatchRuntime
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerSettlementCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly IAuthorityRouteHandle<PlayerSettlementSnapshotIntent, NetworkPlayerSettlementStateQueryResult> snapshotRoute;
    private readonly IAuthorityRouteHandle<PlayerSettlementConstructionIntent, NetworkPlayerSettlementConstructionResult> constructionRoute;
    private readonly Harmony adapterHarmony;
    private readonly HashSet<string> notifiedMethods = new HashSet<string>(StringComparer.Ordinal);
    private readonly PlayerSettlementRevisionGate revisionGate = new PlayerSettlementRevisionGate();

    private Assembly assembly;
    private Type behaviorType;
    private Type metadataType;
    private Type legacyInfoType;
    private FieldInfo metadataField;
    private string lastServerFingerprint;
    private long serverRevision;
    private bool compatible;
    private bool objectRegistrationValidated;
    private bool snapshotReady;
    internal WorkshopSnapshotReadiness SnapshotReadiness { get; private set; }
    internal string SnapshotSessionId { get; private set; }
    internal long SnapshotRevision { get; private set; } = -1;
    private PlayerSettlementConstructionBridge constructionBridge;

    public PlayerSettlementCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        Harmony harmony,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.authorityRequestRouter = authorityRequestRouter;
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        adapterHarmony = new Harmony(PlayerSettlementHarmonyIsolation.AdapterHarmonyOwner);

        compatible = TryInstall();
        if (compatible) PlayerSettlementPatchRuntime.Current = this;

        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<PlayerSettlementSnapshotIntent, NetworkRequestPlayerSettlementState,
                NetworkPlayerSettlementStateQueryResult>.Define(
                "workshop.player-settlement.snapshot", AuthorityRouteKind.BootstrapQuery,
                CreateSnapshotHeader,
                (_, header) => new NetworkRequestPlayerSettlementState(header),
                request => request.Header,
                result => result.Header,
                request => request.Header.TryValidate(out _) ? null : "invalid-player-settlement-snapshot-query",
                request => "snapshot:" + request.Header.SessionId + ":" + request.Header.ExpectedRevision,
                ValidateSnapshotHeader,
                ExecuteSnapshotQuery,
                CreateSnapshotTerminal,
                ProbeSnapshotApplied,
                _ => { },
                PresentSnapshotTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: false));

        constructionRoute = authorityRequestRouter.Register(
            AuthorityRoute<PlayerSettlementConstructionIntent, NetworkRequestPlayerSettlementConstruction,
                NetworkPlayerSettlementConstructionResult>.Define(
                "workshop.player-settlement.construction", AuthorityRouteKind.Command,
                CreateConstructionHeader,
                (intent, header) => new NetworkRequestPlayerSettlementConstruction(header, intent.Request),
                request => request.Header,
                result => result.Header,
                request => PlayerSettlementConstructionProtocol.TryValidate(request, out _) ? null : "invalid-construction-request",
                PlayerSettlementConstructionProtocol.CommandKey,
                ValidateConstructionHeader,
                ExecuteConstructionRoute,
                CreateConstructionTerminal,
                ProbeConstructionApplied,
                _ => StartSnapshotBootstrap(),
                PresentConstructionOutcome,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedConstructionResult));

        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkPlayerSettlementState>(HandleState);
        messageBroker.Subscribe<NetworkPlayerSettlementStateQueryResult>(HandleStateQueryResult);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkPlayerSettlementState>(HandleState);
        messageBroker.Unsubscribe<NetworkPlayerSettlementStateQueryResult>(HandleStateQueryResult);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        snapshotRoute.Dispose();
        constructionRoute.Dispose();
        if (ReferenceEquals(PlayerSettlementPatchRuntime.Current, this))
            PlayerSettlementPatchRuntime.Current = null;
    }

    public void NotifyFeatureBlocked(string method)
    {
        method ??= "unknown Player Settlement action";
        if (!notifiedMethods.Add(method)) return;

        var message =
            $"Player Settlement internal action '{method}' was rejected because it is outside the pinned v7.5.0 co-op route inventory.";
        Logger.Warning(message);
        if (ModInformation.IsClient)
            InformationManager.DisplayMessage(new InformationMessage(message));
    }

    public void AddBehavior(object campaignGameStarter)
    {
        if (campaignGameStarter is not CampaignGameStarter starter)
            throw new InvalidOperationException(
                "Player Settlement behavior bootstrap received an invalid campaign starter");

        var behavior = Activator.CreateInstance(behaviorType) as CampaignBehaviorBase;
        if (behavior == null)
            throw new InvalidOperationException(
                "Player Settlement persistence behavior could not be constructed");

        // RegisterEvents runs on both roles. Every subscribed callback has an exact role prefix:
        // clients keep placement/menu presentation and the host keeps persistence/completion.
        starter.AddBehavior(behavior);
    }

    public bool TrySubmitConstruction(object owner, MethodBase original, object[] arguments)
    {
        if (!CanUseConstructionRoute() || !constructionBridge.TryCapture(owner, original, arguments, 1,
                revisionGate.Revision, id => objectManager.TryGetId(id, out var value) ? value : string.Empty,
                out var request, out _)) return false;
        constructionRoute.Submit(new PlayerSettlementConstructionIntent(request));
        return true;
    }

    public void ValidateObjectRegistration(bool isSavedCampaign)
    {
        if (!ModInformation.IsServer)
            throw new InvalidOperationException(
                "Player Settlement object registration validation may only run on the host");

        if (!isSavedCampaign)
        {
            var existingInfo = legacyInfoType
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (existingInfo != null && LegacyInfoHasGeneratedObjects(existingInfo))
                throw new InvalidOperationException(
                    "Player Settlement failed closed during new-campaign registration: generated settlement state remains in the process");
            objectRegistrationValidated = true;
            return;
        }

        var behavior = behaviorType
            .GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as CampaignBehaviorBase;
        if (behavior == null || Campaign.Current == null)
            throw new InvalidOperationException(
                "Player Settlement persistence behavior is unavailable during object registration");

        var getStore = PlayerSettlementStoreResolver.ResolveGetStore(
            assembly,
            PlayerSettlementStoreResolver.ExtensionTypeName,
            typeof(Campaign),
            typeof(CampaignBehaviorBase),
            typeof(IDataStore));
        IDataStore store;
        try
        {
            // GetStore is an extension method owned by PlayerSettlement.dll. Decompiler output
            // renders it like a Campaign instance method, but invoking Campaign.GetStore through
            // reflection silently resolves nothing on the dedicated runtime.
            store = getStore.Invoke(
                null,
                new object[] { Campaign.Current, behavior }) as IDataStore;
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                "Player Settlement save metadata store resolution failed",
                exception.InnerException ?? exception);
        }
        if (store == null)
        {
            // Player Settlement's own RegisterSubModuleObjects treats an unavailable early store
            // as "no embedded metadata" and then checks its legacy external directory. Dedicated
            // CampaignBehaviorManager does not expose _campaignBehaviorDataStore at this point, so
            // GetStore legitimately returns null even for a healthy saved campaign. Preserve that
            // contract, but only after proving every other in-process/legacy source is empty.
            var currentMetadata = metadataField.GetValue(behavior);
            var fallbackMetadataCaptured = PlayerSettlementCanonicalState.TryCaptureMetadata(
                currentMetadata,
                out var fallbackMetadataEntries,
                out _,
                out var fallbackMetadataFailure);
            var fallbackLegacyInfo = legacyInfoType
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (!fallbackMetadataCaptured)
                throw new InvalidOperationException(
                    "Player Settlement failed closed during unavailable early-store admission: " +
                    (fallbackMetadataFailure ?? "metadata capture failed"));
            if (fallbackLegacyInfo != null && LegacyInfoHasGeneratedObjects(fallbackLegacyInfo))
                throw new InvalidOperationException(
                    "Player Settlement failed closed during unavailable early-store admission: in-process legacy metadata contains generated settlements");
            if (LegacyConfigDirectoryExists())
                throw new InvalidOperationException(
                    "Player Settlement failed closed during unavailable early-store admission: legacy external settlement metadata exists for this campaign");

            Logger.Information(
                "Player Settlement early save store is unavailable on the dedicated host; admitting {Count} already-captured v3 generated objects for authoritative XML registration",
                fallbackMetadataEntries.Length);
            objectRegistrationValidated = true;
            return;
        }
        if (store.IsSaving)
            throw new InvalidOperationException(
                "Player Settlement object registration received a saving data store");

        // Do not call Player Settlement's LoadEarlySync here: that method catches every exception
        // and would make an unreadable non-empty save look like an empty save. Invoke IDataStore's
        // exact generic contract directly so deserialization errors cross this fail-closed boundary.
        var hasMetadata = ReadStoreValue(store, metadataType, "PlayerSettlement_MetaV3", out var metadata);
        if (hasMetadata && metadata == null)
            throw new InvalidOperationException(
                "Player Settlement metadata exists but deserialized to null");

        var metadataCaptured = PlayerSettlementCanonicalState.TryCaptureMetadata(
            metadata,
            out var metadataEntries,
            out _,
            out var metadataFailure);
        if (!metadataCaptured)
            throw new InvalidOperationException(
                "Player Settlement failed closed during object registration: " +
                (metadataFailure ?? "metadata capture failed"));

        var hasLegacyInfo = ReadStoreValue(
            store,
            legacyInfoType,
            "PlayerSettlement_PlayerSettlementInfo",
            out var legacyInfo);
        if (hasLegacyInfo && legacyInfo == null)
            throw new InvalidOperationException(
                "Player Settlement legacy metadata exists but deserialized to null");
        if (legacyInfo != null && LegacyInfoHasGeneratedObjects(legacyInfo))
            throw new InvalidOperationException(
                "Player Settlement failed closed during object registration: the legacy save payload contains generated settlements");

        if (!hasMetadata && LegacyConfigDirectoryExists())
            throw new InvalidOperationException(
                "Player Settlement failed closed during object registration: legacy external settlement metadata exists for this campaign");

        // This is the first behavior mutation in the saved-campaign admission path. Every capture,
        // graph, legacy, and non-empty check above has completed successfully, so a rejected save
        // leaves the behavior's original metadata field untouched.
        metadataField.SetValue(behavior, metadata);
        Logger.Information(
            "Player Settlement admitted {Count} validated generated objects for authoritative XML registration",
            metadataEntries.Length);
        objectRegistrationValidated = true;
    }

    private bool TryInstall()
    {
        assembly = FindAssembly(PlayerSettlementCompatibilityManifest.AssemblyName);
        if (assembly == null)
        {
            Logger.Debug("Player Settlement is not loaded; compatibility adapter is inactive");
            return false;
        }

        var fixesAssembly = FindAssembly(PlayerSettlementCompatibilityManifest.FixesAssemblyName);
        // Module-load patches are already active at this point. Purge them before checking the
        // fingerprint so even an unsupported or incomplete optional install cannot retain direct
        // vanilla campaign mutations after Coop initialization fails.
        var removedOptionalPatches = PlayerSettlementHarmonyIsolation.RemoveModulePatches(
            assembly,
            fixesAssembly,
            adapterHarmony);
        if (!PlayerSettlementCompatibilityManifest.TryValidate(
                assembly,
                fixesAssembly,
                out var methods,
                out var failure))
        {
            // Logging and continuing would leave the unrecognized single-player implementation
            // active. Throwing aborts the Coop session container instead, which is the only safe
            // behavior when the exact method contract cannot be guarded.
            Logger.Fatal("Player Settlement co-op adapter failed closed: {Failure}", failure);
            throw new InvalidOperationException(
                "Player Settlement co-op compatibility validation failed: " + failure);
        }

        behaviorType = assembly.GetType(
            "BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour",
            throwOnError: true,
            ignoreCase: false);
        metadataType = assembly.GetType(
            "BannerlordPlayerSettlement.Saves.MetaV3",
            throwOnError: true,
            ignoreCase: false);
        legacyInfoType = assembly.GetType(
            "BannerlordPlayerSettlement.Saves.PlayerSettlementInfo",
            throwOnError: true,
            ignoreCase: false);
        metadataField = behaviorType.GetField(
            "_metaV3",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (metadataField == null || metadataField.FieldType != metadataType)
            throw new InvalidOperationException(
                "Player Settlement co-op compatibility validation failed: _metaV3 field shape changed");
        constructionBridge = new PlayerSettlementConstructionBridge(
            assembly,
            behaviorType,
            methods.Where(pair => pair.Key.Kind == PlayerSettlementPatchKind.ConstructionCommit)
                .Select(pair => pair.Value));

        lock (PatchSync)
        {
            var expectedPatches = methods
                .Select(pair => (Original: pair.Value, Prefix: PrefixFor(pair.Key.Kind)))
                .ToArray();
            if (!ReferenceEquals(patchedAssembly, assembly))
            {
                PlayerSettlementHarmonyIsolation.AssertNoPatchOverlap(
                    expectedPatches.Select(pair => pair.Original));
                var applied = new List<(MethodInfo Original, MethodInfo Prefix)>();
                try
                {
                    foreach (var pair in expectedPatches)
                    {
                        adapterHarmony.Patch(
                            pair.Original,
                            prefix: new HarmonyMethod(pair.Prefix));
                        applied.Add(pair);
                    }

                    PlayerSettlementHarmonyIsolation.AssertExactAdapterPatchInventory(
                        expectedPatches);
                    patchedAssembly = assembly;
                }
                catch (Exception exception)
                {
                    foreach (var patch in applied)
                        adapterHarmony.Unpatch(patch.Original, patch.Prefix);

                    Logger.Fatal(exception,
                        "Player Settlement co-op patching failed; rolled back all adapter detours");
                    throw new InvalidOperationException(
                        "Player Settlement co-op compatibility patching failed", exception);
                }
            }
            else
            {
                PlayerSettlementHarmonyIsolation.AssertExactAdapterPatchInventory(
                    expectedPatches);
            }
        }

        Logger.Information(
            "Player Settlement {Version} loaded with host-authoritative construction and graph replication ({Removed} original/fixes patches removed; {Methods} entry points guarded)",
            PlayerSettlementCompatibilityManifest.ModuleVersion,
            removedOptionalPatches,
            methods.Count);
        return true;
    }

    private static MethodInfo PrefixFor(PlayerSettlementPatchKind kind)
    {
        switch (kind)
        {
            case PlayerSettlementPatchKind.BootstrapPersistenceBehavior:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.BootstrapPersistenceBehaviorPrefix));
            case PlayerSettlementPatchKind.GuardedObjectRegistration:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.GuardedObjectRegistrationPrefix));
            case PlayerSettlementPatchKind.ServerPersistence:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.ServerPersistencePrefix));
            case PlayerSettlementPatchKind.ServerLifecycle:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.ServerLifecyclePrefix));
            case PlayerSettlementPatchKind.RoleLifecycle:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.RoleLifecyclePrefix));
            case PlayerSettlementPatchKind.ClientPresentation:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.ClientPresentationPrefix));
            case PlayerSettlementPatchKind.ConstructionCommit:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.ConstructionCommitPrefix));
            default:
                return AccessTools.Method(
                    typeof(PlayerSettlementAuthorityPatches),
                    nameof(PlayerSettlementAuthorityPatches.BlockedPrefix));
        }
    }

    internal void HandleAllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> _)
    {
        if (!compatible) return;

        revisionGate.Reset();
        snapshotReady = !ModInformation.IsClient;
        SnapshotReadiness = ModInformation.IsClient ? WorkshopSnapshotReadiness.Unknown : WorkshopSnapshotReadiness.Ready;
        SnapshotSessionId = null;
        SnapshotRevision = -1;
        if (ModInformation.IsClient)
        {
            StartSnapshotBootstrap();
            return;
        }

        serverRevision = 0;
        lastServerFingerprint = null;
        if (!objectRegistrationValidated)
            throw new InvalidOperationException(
                "Player Settlement reached registry completion without guarded object registration validation");
        SendSnapshotOrAbort(peer: null);
    }

    private void HandleHostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (payload?.What.Snapshot == null || !configAuthority.IsCurrent(payload.What.Snapshot)) return;
        if (ModInformation.IsClient && !string.Equals(SnapshotSessionId, payload.What.Snapshot.SessionId, StringComparison.Ordinal))
        {
            snapshotReady = false;
            SnapshotReadiness = WorkshopSnapshotReadiness.Unknown;
            SnapshotRevision = -1;
            revisionGate.Reset();
        }
        StartSnapshotBootstrap();
    }

    private void StartSnapshotBootstrap()
    {
        if (!compatible || !objectRegistrationValidated || !ModInformation.IsClient || snapshotReady ||
            !configAuthority.TryGetCurrent(out _)) return;
        SnapshotReadiness = WorkshopSnapshotReadiness.Loading;
        snapshotRoute.Submit(default);
    }

    private AuthorityRequestHeader CreateSnapshotHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateSnapshotHeader(AuthorityRequestHeader header)
    {
        if (!compatible || !objectRegistrationValidated || !configAuthority.TryGetCurrent(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "player-settlement-snapshot-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion || !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private AuthorityServerReply<NetworkPlayerSettlementStateQueryResult> ExecuteSnapshotQuery(
        AuthorityServerContext context, NetworkRequestPlayerSettlementState _)
    {
        if (!TryCaptureSnapshot(out var snapshot, out var failure))
        {
            Logger.Warning("Player Settlement snapshot query is unavailable: {Failure}", failure);
            return new AuthorityServerReply<NetworkPlayerSettlementStateQueryResult>(
                CreateSnapshotTerminal(context.Header, AuthorityResultStatus.Unavailable,
                    "player-settlement-snapshot-unavailable"), false);
        }
        return new AuthorityServerReply<NetworkPlayerSettlementStateQueryResult>(
            new NetworkPlayerSettlementStateQueryResult(context.Header, AuthorityResultStatus.Accepted, snapshot, null), true);
    }

    private static NetworkPlayerSettlementStateQueryResult CreateSnapshotTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new NetworkPlayerSettlementStateQueryResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeSnapshotApplied(NetworkPlayerSettlementStateQueryResult result) =>
        snapshotReady && result.Snapshot != null && revisionGate.Revision == result.Header.CommittedRevision
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;

    private void PresentSnapshotTerminal(AuthorityClientOutcome<NetworkPlayerSettlementStateQueryResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        snapshotReady = false;
        SnapshotReadiness = WorkshopSnapshotReadiness.Unavailable;
        Logger.Warning("Player Settlement snapshot bootstrap ended without readiness. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private void HandleStateRequest(MessagePayload<NetworkRequestPlayerSettlementState> payload)
    {
        if (!compatible || !ModInformation.IsServer || payload.Who is not NetPeer peer) return;
        GameThread.RunSafe(
            () => SendSnapshotOrAbort(peer),
            context: nameof(PlayerSettlementCompatibilityHandler));
    }

    private void HandleState(MessagePayload<NetworkPlayerSettlementState> payload)
    {
        if (!compatible || !configAuthority.TryGetCurrent(out _) || payload.Who is not NetPeer serverPeer ||
            !PlayerSettlementSnapshotOriginGuard.IsTrustedServerTransport(
                serverPeer,
                ModInformation.IsClient))
        {
            return;
        }
        GameThread.RunSafe(
            () =>
            {
                var accepted = ApplySnapshot(payload.What, out var failure);
                if (accepted)
                {
                    snapshotReady = true;
                    SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                    if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                    SnapshotRevision = payload.What.Revision;
                }
                if (!PlayerSettlementSnapshotFailurePolicy.MustDisconnect(
                        trustedServerTransport: true,
                        snapshotAccepted: accepted))
                {
                    return;
                }

                Logger.Fatal(
                    "Disconnecting from the Coop server because Player Settlement state could not be accepted: {Failure}",
                    failure);
                InformationManager.DisplayMessage(new InformationMessage(
                    "Player Settlement compatibility validation failed. The co-op connection was closed to prevent a divergent campaign. " +
                    failure));
                serverPeer.Disconnect();
            },
            context: nameof(PlayerSettlementCompatibilityHandler));
    }

    private void HandleStateQueryResult(MessagePayload<NetworkPlayerSettlementStateQueryResult> payload)
    {
        if (!compatible || !ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer) || payload.What.Header.Status != AuthorityResultStatus.Accepted ||
            payload.What.Snapshot == null)
            return;

        GameThread.RunSafe(() =>
        {
            if (ApplySnapshot(payload.What.Snapshot, out var failure))
            {
                snapshotReady = true;
                SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                SnapshotRevision = payload.What.Snapshot.Revision;
                return;
            }
            snapshotReady = false;
            Logger.Fatal("Disconnecting from the Coop server because Player Settlement state could not be accepted: {Failure}",
                failure);
            serverPeer.Disconnect();
        }, context: nameof(PlayerSettlementCompatibilityHandler));
    }

    private AuthorityRequestHeader CreateConstructionHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, revisionGate.Revision);
    }

    private AuthorityHeaderValidation ValidateConstructionHeader(AuthorityRequestHeader header)
    {
        if (!CanUseConstructionRoute())
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "construction-route-unavailable");
        if (!configAuthority.TryGetCurrent(out var config) || header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == revisionGate.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-construction-graph");
    }

    private bool CanUseConstructionRoute() => compatible && objectRegistrationValidated && snapshotReady &&
        capabilityRegistry.IsEnabled(PlayerSettlementCapabilitySource.ModuleId, PlayerSettlementCapabilitySource.Operation) &&
        authorityRequestRouter.IsRegistered("workshop.player-settlement.construction", AuthorityRouteKind.Command) &&
        authorityRequestRouter.IsRegistered("workshop.player-settlement.snapshot", AuthorityRouteKind.BootstrapQuery) &&
        (!ModInformation.IsClient || (SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
            configAuthority.TryGetCurrent(out var config) && SnapshotSessionId == config.SessionId));

    private AuthorityServerReply<NetworkPlayerSettlementConstructionResult> ExecuteConstructionRoute(
        AuthorityServerContext context, NetworkRequestPlayerSettlementConstruction request)
    {
        if (!playerManager.TryGetPlayer(context.Peer, out var player) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty party) || actor == null || party == null ||
            !actor.IsAlive || actor.Clan == null)
            return ConstructionReply(context.Header, request, AuthorityResultStatus.Unauthorized, "actor-not-eligible", null, null, null, false);
        PlayerSettlementStateEntry[] before;
        string beforeFingerprint;
        try
        {
            before = CaptureStateOrThrow("construction pre-capture");
            beforeFingerprint = PlayerSettlementStateCodec.ComputeHash(before);
        }
        catch (Exception exception) { return ConstructionReply(context.Header, request, AuthorityResultStatus.Unavailable, exception.Message, null, null, actor, false); }
        object behavior = behaviorType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        try
        {
            using (new BarterPlayerContext(actor, party))
                if (!constructionBridge.TryExecute(behavior, actor, party, request,
                    id => objectManager.TryGetObject(id, out Settlement value) ? value : null,
                    id => objectManager.TryGetObject(id, out CultureObject value) ? value : null, out var failure))
                    return ConstructionReply(context.Header, request, AuthorityResultStatus.Rejected, failure, null, null, actor, false);
            var after = CaptureStateOrThrow("construction post-capture");
            if (string.Equals(beforeFingerprint, PlayerSettlementStateCodec.ComputeHash(after), StringComparison.Ordinal) ||
                !TryGetConstructionDiff(request, before, after, out var changed))
                throw new InvalidOperationException("construction graph diff was missing, extra, or not operation-valid");
            if (!TryRegisterAndValidateAffectedSettlements(changed, out var postState))
                throw new InvalidOperationException("construction object registration/post-state validation failed");
            if (!SendSnapshotOrAbort(null)) throw new InvalidOperationException("construction snapshot publication failed");
            return ConstructionReply(context.Header, request, AuthorityResultStatus.Accepted, null, changed, lastServerFingerprint,
                actor, true, postState.OwnerId, postState.GarrisonId);
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(null, "construction mutation/publication ambiguity: " + exception.Message);
            return new AuthorityServerReply<NetworkPlayerSettlementConstructionResult>(
                CreateConstructionTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "ambiguous-construction"), false, true);
        }
    }

    internal static bool TryGetConstructionDiff(NetworkRequestPlayerSettlementConstruction request,
        PlayerSettlementStateEntry[] before, PlayerSettlementStateEntry[] after, out PlayerSettlementStateEntry[] changed)
    {
        changed = Array.Empty<PlayerSettlementStateEntry>();
        if (request == null || before == null || after == null) return false;

        // Metadata records are canonical identities. A construction operation may alter an existing
        // target and its directly-owned villages, but it may not silently remove an unrelated record.
        if (before.Any(prior => !after.Any(entry => SameGraphNode(prior, entry)))) return false;
        changed = after.Where(entry => !before.Any(prior => SameIdentity(prior, entry))).ToArray();
        if (changed.Length == 0) return false;

        return request.Operation switch
        {
            PlayerSettlementConstructionOperation.BuildTown =>
                IsExactNewRoot(changed, PlayerSettlementObjectKind.Town),
            PlayerSettlementConstructionOperation.BuildCastle =>
                IsExactNewRoot(changed, PlayerSettlementObjectKind.Castle),
            PlayerSettlementConstructionOperation.BuildVillage =>
                IsExactNewVillage(changed, request.BoundId),
            PlayerSettlementConstructionOperation.Rebuild or PlayerSettlementConstructionOperation.Overwrite =>
                IsExactTargetDiff(changed, request.TargetId),
            _ => false,
        };
    }

    private static bool SameIdentity(PlayerSettlementStateEntry left, PlayerSettlementStateEntry right) =>
        left != null && right != null && left.Kind == right.Kind && left.StringId == right.StringId &&
        left.ParentStringId == right.ParentStringId && left.XmlSha256 == right.XmlSha256 &&
        left.ComponentFingerprint == right.ComponentFingerprint;

    private static bool SameGraphNode(PlayerSettlementStateEntry left, PlayerSettlementStateEntry right) =>
        left != null && right != null && left.Kind == right.Kind && left.StringId == right.StringId &&
        left.ParentStringId == right.ParentStringId;

    private static bool IsExactNewRoot(PlayerSettlementStateEntry[] changed, PlayerSettlementObjectKind rootKind)
    {
        var roots = changed.Where(entry => string.IsNullOrEmpty(entry.ParentStringId)).ToArray();
        if (roots.Length != 1 || roots[0].Kind != rootKind) return false;
        return changed.All(entry => ReferenceEquals(entry, roots[0]) ||
            entry.Kind == PlayerSettlementObjectKind.BoundVillage && entry.ParentStringId == roots[0].StringId);
    }

    private static bool IsExactNewVillage(PlayerSettlementStateEntry[] changed, string boundId)
    {
        if (changed.Length != 1) return false;
        var entry = changed[0];
        return entry.Kind == PlayerSettlementObjectKind.ExtraVillage && string.IsNullOrEmpty(entry.ParentStringId) ||
            entry.Kind == PlayerSettlementObjectKind.BoundVillage && entry.ParentStringId == boundId;
    }

    private static bool IsExactTargetDiff(PlayerSettlementStateEntry[] changed, string targetId) =>
        !string.IsNullOrEmpty(targetId) && changed.Any(entry => entry.StringId == targetId) &&
        changed.All(entry => entry.StringId == targetId || entry.ParentStringId == targetId);

    private bool TryRegisterAndValidateAffectedSettlements(PlayerSettlementStateEntry[] changed,
        out PlayerSettlementConstructionPostState postState)
    {
        var capturedPostState = default(PlayerSettlementConstructionPostState);
        bool registered = objectManager.RunRegistrationTransaction(() =>
        {
            Settlement primary = null;
            foreach (var entry in changed)
            {
                var settlement = Settlement.All.FirstOrDefault(value => value?.StringId == entry.StringId);
                if (settlement == null) return false;
                if (!objectManager.Contains(entry.StringId) && !objectManager.AddExisting(entry.StringId, settlement)) return false;
                if (!objectManager.TryGetObject(entry.StringId, out Settlement registered) || !ReferenceEquals(registered, settlement))
                    return false;
                if (primary == null || string.IsNullOrEmpty(entry.ParentStringId)) primary = settlement;
            }

            string ownerId = primary?.OwnerClan?.StringId ?? string.Empty;
            MobileParty garrison = primary?.Town?.GarrisonParty;
            string garrisonId = string.Empty;
            if (garrison != null && !objectManager.TryGetId(garrison, out garrisonId)) return false;
            capturedPostState = new PlayerSettlementConstructionPostState(ownerId, garrisonId);
            return true;
        });
        postState = capturedPostState;
        return registered;
    }

    private AuthorityServerReply<NetworkPlayerSettlementConstructionResult> ConstructionReply(
        AuthorityRequestHeader header, NetworkRequestPlayerSettlementConstruction request, AuthorityResultStatus status,
        string message, PlayerSettlementStateEntry[] changed, string fingerprint, Hero actor, bool published,
        string ownerId = null, string garrisonId = null) =>
        new(new NetworkPlayerSettlementConstructionResult(header,
            status == AuthorityResultStatus.Accepted ? PlayerSettlementConstructionStatus.Accepted :
            status == AuthorityResultStatus.StaleState ? PlayerSettlementConstructionStatus.StaleState : PlayerSettlementConstructionStatus.Rejected,
            status, message, PlayerSettlementConstructionProtocol.CommandKey(request), fingerprint, changed,
            actor?.Gold ?? 0, ownerId ?? actor?.Clan?.StringId, garrisonId ?? string.Empty,
            status == AuthorityResultStatus.Accepted ? serverRevision : header.ExpectedRevision), published);

    private static NetworkPlayerSettlementConstructionResult CreateConstructionTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(header, PlayerSettlementConstructionStatus.Rejected, status, reason, string.Empty, string.Empty,
            Array.Empty<PlayerSettlementStateEntry>(), 0, string.Empty, string.Empty, header.ExpectedRevision);

    private bool IsExpectedConstructionResult(NetworkRequestPlayerSettlementConstruction request,
        NetworkPlayerSettlementConstructionResult result) => result.Header.RequestId == request.Header.RequestId &&
        result.Header.SessionId == request.Header.SessionId && result.CommandDigest == PlayerSettlementConstructionProtocol.CommandKey(request);

    private AuthorityCommitProbeResult ProbeConstructionApplied(NetworkPlayerSettlementConstructionResult result) =>
        result.Header.Status == AuthorityResultStatus.Accepted && SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
        SnapshotRevision == result.Revision && revisionGate.Fingerprint == result.GraphFingerprint &&
        ExactConstructionReplicaMatches(result)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private bool ExactConstructionReplicaMatches(NetworkPlayerSettlementConstructionResult result)
    {
        try
        {
            var entries = CaptureStateOrThrow("construction client probe");
            if (result.AffectedEntries == null || result.AffectedEntries.Length == 0 ||
                !result.AffectedEntries.All(expected => entries.Any(actual => SameIdentity(expected, actual)))) return false;
            foreach (var entry in result.AffectedEntries)
            {
                if (!objectManager.TryGetObject(entry.StringId, out Settlement settlement) || settlement == null ||
                    (!string.IsNullOrEmpty(result.OwnerId) && settlement.OwnerClan?.StringId != result.OwnerId)) return false;
            }
            if (!string.IsNullOrEmpty(result.GarrisonId) &&
                !objectManager.TryGetObject(result.GarrisonId, out MobileParty _)) return false;
            return Hero.MainHero != null && Hero.MainHero.Gold == result.ActorGold;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PresentConstructionOutcome(AuthorityClientOutcome<NetworkPlayerSettlementConstructionResult> outcome)
    {
        if (outcome.Applied)
        {
            InformationManager.DisplayMessage(new InformationMessage("Player Settlement construction committed by the host."));
            return;
        }
        if (outcome.Result?.AuthorityStatus == AuthorityResultStatus.StaleState)
        {
            snapshotReady = false;
            SnapshotReadiness = WorkshopSnapshotReadiness.Unknown;
            StartSnapshotBootstrap();
        }
    }

    private bool SendSnapshotOrAbort(NetPeer peer)
    {
        PlayerSettlementStateEntry[] entries;
        try
        {
            entries = CaptureStateOrThrow("snapshot publication");
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(peer, exception.Message);
            return false;
        }

        var fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
        var nextRevision = serverRevision;

        if (lastServerFingerprint != null &&
            !string.Equals(lastServerFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            nextRevision++;
        }

        var state = new NetworkPlayerSettlementState(
            PlayerSettlementCompatibilityManifest.AdapterVersion,
            nextRevision,
            PlayerSettlementFeatureStatus.Enabled,
            fingerprint,
            entries);
        if (!PlayerSettlementStateCodec.TryValidate(state, out var failure))
        {
            DenyPeerOrAbortSession(peer, "captured state was invalid: " + failure);
            return false;
        }

        try
        {
            if (peer == null) network.SendAll(state);
            else network.Send(peer, state);
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(peer, "state publication failed: " + exception.Message);
            return false;
        }

        serverRevision = nextRevision;
        lastServerFingerprint = fingerprint;
        return true;
    }

    private bool TryCaptureSnapshot(out NetworkPlayerSettlementState snapshot, out string failure)
    {
        snapshot = null;
        if (!objectRegistrationValidated)
        {
            failure = "authoritative object registration is not ready";
            return false;
        }
        PlayerSettlementStateEntry[] entries;
        try
        {
            entries = CaptureStateOrThrow("snapshot query");
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
        string fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
        long revision = lastServerFingerprint != null &&
            !string.Equals(lastServerFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            ? serverRevision + 1
            : serverRevision;
        snapshot = new NetworkPlayerSettlementState(
            PlayerSettlementCompatibilityManifest.AdapterVersion,
            revision,
            PlayerSettlementFeatureStatus.Enabled,
            fingerprint,
            entries);
        if (!PlayerSettlementStateCodec.TryValidate(snapshot, out failure))
        {
            failure = "captured state was invalid: " + failure;
            return false;
        }
        failure = null;
        return true;
    }

    private static bool ReadStoreValue(
        IDataStore store,
        Type valueType,
        string key,
        out object value)
    {
        var definition = typeof(IDataStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
                method.Name == nameof(IDataStore.SyncData) &&
                method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 2);
        var arguments = new object[] { key, null };
        try
        {
            var found = (bool)definition.MakeGenericMethod(valueType).Invoke(store, arguments);
            value = arguments[1];
            return found;
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Player Settlement metadata key {key} could not be read",
                exception.InnerException ?? exception);
        }
    }

    private static bool LegacyInfoHasGeneratedObjects(object legacyInfo)
    {
        foreach (var memberName in new[]
                 {
                     "Towns", "Castles", "PlayerVillages", "OverwriteSettlements",
                 })
        {
            var value = legacyInfo.GetType()
                .GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(legacyInfo);
            if (value == null)
                throw new InvalidOperationException(
                    $"Player Settlement legacy metadata list {memberName} is missing");
            if (value is not IEnumerable values)
                throw new InvalidOperationException(
                    $"Player Settlement legacy metadata list {memberName} has an unsupported shape");
            var enumerator = values.GetEnumerator();
            try
            {
                if (enumerator.MoveNext()) return true;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
        return false;
    }

    private static bool LegacyConfigDirectoryExists()
    {
        var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        var campaignId = Campaign.Current?.UniqueGameId;
        if (string.IsNullOrEmpty(personal) || string.IsNullOrEmpty(campaignId)) return false;
        return Directory.Exists(Path.Combine(
            personal,
            "Mount and Blade II Bannerlord",
            "Configs",
            "BannerlordPlayerSettlement",
            campaignId));
    }

    internal PlayerSettlementStateEntry[] CaptureStateOrThrow(string phase)
    {
        var captured = PlayerSettlementCanonicalState.TryCapture(
            assembly,
            out var entries,
            out _,
            out var failure);
        if (!captured)
        {
            var message =
                $"Player Settlement failed closed during {phase}: metadata capture failed ({failure ?? "unknown failure"}).";
            Logger.Fatal(message);
            throw new InvalidOperationException(message);
        }
        return entries ?? Array.Empty<PlayerSettlementStateEntry>();
    }

    internal bool ApplySnapshot(NetworkPlayerSettlementState state, out string rejection)
    {
        if (!PlayerSettlementStateCodec.TryValidate(state, out var failure))
        {
            Logger.Error("Rejected Player Settlement state: {Failure}", failure);
            rejection = failure;
            return false;
        }

        var decision = revisionGate.Evaluate(state.Revision, state.StateFingerprint);
        if (decision == PlayerSettlementRevisionDecision.AlreadyApplied)
        {
            rejection = null;
            return true;
        }
        if (decision != PlayerSettlementRevisionDecision.Apply)
        {
            Logger.Error(
                "Rejected Player Settlement state revision {Revision} ({Decision}); local revision is {LocalRevision}",
                state.Revision,
                decision,
                revisionGate.Revision);
            rejection = $"snapshot revision {state.Revision} was {decision} against local revision {revisionGate.Revision}";
            return false;
        }

        if (!PlayerSettlementObjectGraphRegistry.TryVerify(
                state.Entries,
                id => (objectManager.TryGetObject(id, out Settlement settlement) && settlement != null) ||
                      Settlement.All.Any(candidate => candidate != null &&
                          string.Equals(candidate.StringId, id, StringComparison.Ordinal)),
                out rejection))
        {
            Logger.Fatal(rejection);
            return false;
        }

        if (!revisionGate.Commit(state.Revision, state.StateFingerprint))
        {
            rejection = "snapshot revision changed before it could be committed";
            return false;
        }
        rejection = null;
        return true;
    }

    private static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Player Settlement state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal(
            "Aborting the Coop session because authoritative Player Settlement state is unavailable: {Failure}",
            failure);
        throw new InvalidOperationException(
            "The Coop session cannot continue without authoritative Player Settlement state: " + failure);
    }

    private static Assembly FindAssembly(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, name, StringComparison.Ordinal));
}

internal readonly struct PlayerSettlementSnapshotIntent
{
}

internal static class PlayerSettlementStoreResolver
{
    internal const string ExtensionTypeName =
        "BannerlordPlayerSettlement.Extensions.CampaignExtensions";

    internal static MethodInfo ResolveGetStore(
        Assembly assembly,
        string extensionTypeName,
        Type campaignType,
        Type behaviorType,
        Type storeType)
    {
        var extensionType = assembly?.GetType(
            extensionTypeName,
            throwOnError: false,
            ignoreCase: false);
        var method = extensionType == null
            ? null
            : AccessTools.Method(
                extensionType,
                "GetStore",
                new[] { campaignType, behaviorType });
        if (method == null || !method.IsStatic ||
            !storeType.IsAssignableFrom(method.ReturnType))
        {
            throw new InvalidOperationException(
                "Player Settlement co-op compatibility validation failed: exact CampaignExtensions.GetStore(Campaign, CampaignBehaviorBase) contract is unavailable");
        }

        return method;
    }
}
