using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Registry.Messages;
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
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

/// <summary>
/// Fail-closed Player Settlement 7.5.0 boundary. The module remains separately packaged for its
/// assets and save type definitions, but construction/rebuild/overwrite is feature-blocked until a
/// controller-scoped transaction can register and roll back the complete generated object graph on
/// every peer. Empty-state campaigns and late joiners receive a revisioned readiness snapshot.
/// </summary>
internal sealed class PlayerSettlementCompatibilityHandler : IHandler, IPlayerSettlementPatchRuntime
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerSettlementCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
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
    private bool limitationNoticeShown;
    private bool objectRegistrationValidated;

    public PlayerSettlementCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        Harmony harmony)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        adapterHarmony = new Harmony(PlayerSettlementHarmonyIsolation.AdapterHarmonyOwner);

        compatible = TryInstall();
        if (compatible) PlayerSettlementPatchRuntime.Current = this;

        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkRequestPlayerSettlementState>(HandleStateRequest);
        messageBroker.Subscribe<NetworkPlayerSettlementState>(HandleState);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkRequestPlayerSettlementState>(HandleStateRequest);
        messageBroker.Unsubscribe<NetworkPlayerSettlementState>(HandleState);
        if (ReferenceEquals(PlayerSettlementPatchRuntime.Current, this))
            PlayerSettlementPatchRuntime.Current = null;
    }

    public void NotifyFeatureBlocked(string method)
    {
        method ??= "unknown Player Settlement action";
        if (!notifiedMethods.Add(method)) return;

        var message =
            $"Player Settlement action '{method}' is disabled in co-op: its v7.5.0 path combines local placement, random XML/object creation, MainHero/MainParty, and save reload without a controller-authorized transaction.";
        Logger.Warning(message);
        if (ModInformation.IsClient)
            InformationManager.DisplayMessage(new InformationMessage(message));
    }

    public void AddPersistenceBehavior(object campaignGameStarter)
    {
        if (!ModInformation.IsServer || campaignGameStarter is not CampaignGameStarter starter)
            throw new InvalidOperationException(
                "Player Settlement persistence behavior bootstrap received an invalid host starter");

        var behavior = Activator.CreateInstance(behaviorType) as CampaignBehaviorBase;
        if (behavior == null)
            throw new InvalidOperationException(
                "Player Settlement persistence behavior could not be constructed");

        // AddBehavior invokes RegisterEvents; that method is separately patched to return without
        // registering ticks, menus, MainHero/MainParty callbacks, or compatibility behaviors.
        starter.AddBehavior(behavior);
    }

    public void ValidateEmptyObjectRegistration(bool isSavedCampaign)
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
            PlayerSettlementUnavailableStoreAdmission.RequireEmpty(
                fallbackMetadataCaptured,
                fallbackMetadataEntries,
                fallbackMetadataFailure,
                fallbackLegacyInfo != null && LegacyInfoHasGeneratedObjects(fallbackLegacyInfo),
                LegacyConfigDirectoryExists());

            Logger.Warning(
                "Player Settlement early save store is unavailable on the dedicated host; admitted the campaign after proving embedded, in-process, and legacy generated settlement state is empty");
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
        PlayerSettlementStateAdmission.RequireEmpty(
            metadataCaptured,
            metadataEntries,
            metadataFailure,
            "object registration");

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
            "Player Settlement {Version} loaded in guarded/feature-blocked mode ({Removed} original/fixes patches removed; {Methods} entry points guarded)",
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
        if (ModInformation.IsClient)
        {
            ShowLimitationNotice();
            network.SendAll(new NetworkRequestPlayerSettlementState());
            return;
        }

        serverRevision = 0;
        lastServerFingerprint = null;
        if (!objectRegistrationValidated)
            throw new InvalidOperationException(
                "Player Settlement reached registry completion without guarded object registration validation");
        SendSnapshotOrAbort(peer: null);
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
        if (!compatible || payload.Who is not NetPeer serverPeer ||
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

    private void SendSnapshotOrAbort(NetPeer peer)
    {
        PlayerSettlementStateEntry[] entries;
        try
        {
            entries = AssertEmptyStateOrThrow("snapshot publication");
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(peer, exception.Message);
            return;
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
            PlayerSettlementFeatureStatus.GuardedFeatureBlocked,
            fingerprint,
            entries);
        if (!PlayerSettlementStateCodec.TryValidate(state, out var failure))
        {
            DenyPeerOrAbortSession(peer, "captured state was invalid: " + failure);
            return;
        }

        try
        {
            if (peer == null) network.SendAll(state);
            else network.Send(peer, state);
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(peer, "state publication failed: " + exception.Message);
            return;
        }

        serverRevision = nextRevision;
        lastServerFingerprint = fingerprint;
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

    internal PlayerSettlementStateEntry[] AssertEmptyStateOrThrow(string phase)
    {
        var captured = PlayerSettlementCanonicalState.TryCapture(
            assembly,
            out var entries,
            out _,
            out var failure);
        try
        {
            return PlayerSettlementStateAdmission.RequireEmpty(captured, entries, failure, phase);
        }
        catch (InvalidOperationException exception)
        {
            Logger.Fatal(exception.Message);
            throw;
        }
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

        if ((state.Entries?.Length ?? 0) != 0)
        {
            const string message =
                "Player Settlement objects were found in the host save, but this guarded build cannot safely register their generated object/component graph for a late joiner. The snapshot was rejected without mutating client state.";
            Logger.Fatal(message);
            rejection = message;
            return false;
        }

        if (!revisionGate.Commit(state.Revision, state.StateFingerprint))
        {
            rejection = "snapshot revision changed before it could be committed";
            return false;
        }
        ShowLimitationNotice();
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

    private void ShowLimitationNotice()
    {
        if (limitationNoticeShown || !ModInformation.IsClient) return;
        limitationNoticeShown = true;
        InformationManager.DisplayMessage(new InformationMessage(
            "Player Settlement 7.5.0 safety is active. Existing empty-state campaigns can load, but build, rebuild, overwrite, delete/edit, and save-reload actions are disabled until their generated object graph is server-authoritative."));
    }

    private static Assembly FindAssembly(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, name, StringComparison.Ordinal));
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
