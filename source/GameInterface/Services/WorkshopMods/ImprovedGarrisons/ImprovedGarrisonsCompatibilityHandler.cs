using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using LiteNetLib;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal sealed class ImprovedGarrisonsRequestLedger<TKey>
{
    private sealed class PeerRequests
    {
        public readonly HashSet<long> Seen = new HashSet<long>();
        public readonly Queue<long> Order = new Queue<long>();
    }

    private readonly object sync = new object();
    private readonly Dictionary<TKey, PeerRequests> requests = new Dictionary<TKey, PeerRequests>();
    private readonly int capacityPerPeer;

    public ImprovedGarrisonsRequestLedger(int capacityPerPeer)
    {
        if (capacityPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(capacityPerPeer));
        this.capacityPerPeer = capacityPerPeer;
    }

    public bool HasSeen(TKey peer, long requestId)
    {
        lock (sync)
            return requests.TryGetValue(peer, out var peerRequests) && peerRequests.Seen.Contains(requestId);
    }

    public void Record(TKey peer, long requestId)
    {
        lock (sync)
        {
            if (!requests.TryGetValue(peer, out var peerRequests))
            {
                peerRequests = new PeerRequests();
                requests.Add(peer, peerRequests);
            }

            if (!peerRequests.Seen.Add(requestId)) return;
            peerRequests.Order.Enqueue(requestId);
            while (peerRequests.Order.Count > capacityPerPeer)
                peerRequests.Seen.Remove(peerRequests.Order.Dequeue());
        }
    }

    public void Reset()
    {
        lock (sync) requests.Clear();
    }
}

/// <summary>
/// Reflection-only compatibility boundary for Improved Garrisons 4.2.0.7. Native campaign
/// mutations, AI ticks, party creation and external persistence are server-authoritative. The
/// supported primitive UI settings are validated and routed client -&gt; server; complex selection
/// screens are explicitly denied until their roster/template payloads have authoritative commands.
/// </summary>
internal sealed class ImprovedGarrisonsCompatibilityHandler : IHandler, IImprovedGarrisonsPatchRuntime
{
    internal delegate bool CanonicalStateApplier(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure);

    internal const int MaxRequestManagerLength = 192;
    internal const int MaxRequestMethodLength = 96;
    internal const int MaxObjectIdLength = 256;
    internal const int MaxRequestValueLength = 256;
    internal const int MaxSnapshotValues = 16384;
    internal const int MaxSnapshotPropertyLength = 768;
    internal const int MaxSnapshotValueLength = 4096;
    internal const int MaxSnapshotCharacters = 4 * 1024 * 1024;

    private static readonly ILogger Logger = LogManager.GetLogger<ImprovedGarrisonsCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly Harmony harmony;
    private readonly HashSet<string> deniedNotifications = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, (ImprovedGarrisonsMethodSpec Spec, MethodInfo Method)> routedMethods =
        new Dictionary<string, (ImprovedGarrisonsMethodSpec, MethodInfo)>(StringComparer.Ordinal);
    private readonly ImprovedGarrisonsRequestLedger<NetPeer> requestLedger =
        new ImprovedGarrisonsRequestLedger<NetPeer>(256);

    private Assembly assembly;
    private long revision;
    private long nextRequestId;
    private string lastPublishedHash;
    private string lastAppliedHash;
    private bool compatible;
    private bool stateReady;

    public ImprovedGarrisonsCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        Harmony _)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.harmony = new Harmony(ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) ImprovedGarrisonsPatchRuntime.Current = this;

        messageBroker.Subscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkRequestImprovedGarrisonsState>(Handle_StateRequest);
        messageBroker.Subscribe<NetworkRequestImprovedGarrisonsSettingChange>(Handle_SettingRequest);
        messageBroker.Subscribe<NetworkImprovedGarrisonsState>(Handle_State);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkRequestImprovedGarrisonsState>(Handle_StateRequest);
        messageBroker.Unsubscribe<NetworkRequestImprovedGarrisonsSettingChange>(Handle_SettingRequest);
        messageBroker.Unsubscribe<NetworkImprovedGarrisonsState>(Handle_State);
        if (ReferenceEquals(ImprovedGarrisonsPatchRuntime.Current, this)) ImprovedGarrisonsPatchRuntime.Current = null;
    }

    public bool TryRouteSetting(object manager, MethodBase method, object[] arguments)
    {
        if (!compatible || method?.DeclaringType == null || arguments == null || arguments.Length == 0 ||
            arguments[0] is not Town town || !objectManager.TryGetId(town, out var townId))
            return false;

        var key = RoutedKey(method.DeclaringType.FullName, method.Name);
        if (!routedMethods.ContainsKey(key)) return false;

        var value = arguments.Length == 1 ? string.Empty : ImprovedGarrisonsCanonicalState.FormatValue(arguments[1]);
        network.SendAll(new NetworkRequestImprovedGarrisonsSettingChange(
            Interlocked.Increment(ref nextRequestId),
            revision,
            method.DeclaringType.FullName,
            method.Name,
            townId,
            value));
        return true;
    }

    public void NotifyDenied(string method)
    {
        if (!deniedNotifications.Add(method ?? "unknown")) return;
        var message = $"Improved Garrisons action '{method}' is disabled in co-op until its selection payload is server-authoritative.";
        Logger.Warning(message);
        if (ModInformation.IsClient) InformationManager.DisplayMessage(new InformationMessage(message));
    }

    public void OnAuthoritativeTickCompleted()
    {
        if (compatible && stateReady && ModInformation.IsServer) PublishStateIfChanged();
    }

    public bool TryGetTownSettings(Town town, out object settings)
    {
        settings = null;
        if (!compatible || town == null || !TryGetSettingsDictionary(out var dictionary)) return false;

        var key = town.Name?.ToString();
        if (string.IsNullOrEmpty(key)) return false;

        // A client may receive the canonical dictionary before its player ownership registry is
        // fully populated. Existing server-provided entries remain authoritative in that window.
        if ((ModInformation.IsClient && dictionary.Contains(key)) || playerManager.Contains(town.OwnerClan))
        {
            settings = ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, town);
            return settings != null;
        }

        var npcType = assembly.GetType(
            "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.NPCGarrisonSettings", false);
        settings = npcType == null ? null : Activator.CreateInstance(npcType);
        return settings != null;
    }

    public void ReconcileSettlements()
    {
        if (!compatible || !TryGetSettingsDictionary(out var dictionary)) return;

        var knownTownNames = new HashSet<string>(StringComparer.Ordinal);
        var controlledTownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var settlement in Settlement.All.Where(item => item?.Town != null && (item.IsTown || item.IsCastle)))
        {
            var name = settlement.Town.Name?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            knownTownNames.Add(name);
            if (!playerManager.Contains(settlement.Town.OwnerClan)) continue;
            controlledTownNames.Add(name);
            ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, settlement.Town);
        }

        // Do not erase a server save before the player registry has loaded. Once it has entries,
        // remove only keys that can be tied to a known, currently non-player-owned settlement.
        if (ModInformation.IsServer && playerManager.Players.Count > 0)
        {
            var staleKeys = dictionary.Keys.Cast<object>()
                .OfType<string>()
                .Where(key => knownTownNames.Contains(key) && !controlledTownNames.Contains(key))
                .ToArray();
            foreach (var key in staleKeys) dictionary.Remove(key);
            PublishStateIfChanged();
        }
    }

    public void OnSettlementOwnerChanged(Settlement settlement)
    {
        if (!compatible || !ModInformation.IsServer || settlement?.Town == null ||
            (!settlement.IsTown && !settlement.IsCastle) || !TryGetSettingsDictionary(out var dictionary))
            return;

        var key = settlement.Town.Name?.ToString();
        if (string.IsNullOrEmpty(key)) return;
        if (playerManager.Contains(settlement.Town.OwnerClan))
            ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, settlement.Town);
        else
            dictionary.Remove(key);

        PublishStateIfChanged();
    }

    private bool TryInstall()
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                ImprovedGarrisonsCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.Debug("Improved Garrisons is not loaded; compatibility adapter is inactive");
            return false;
        }

        int removedModulePatches;
        try
        {
            // Improved Garrisons can patch TaleWorlds methods from OnSubModuleLoad, before Coop's
            // container exists. Purge by exact implementing assembly even before fingerprint/API
            // validation, so an unsupported binary cannot retain its early detours.
            removedModulePatches = ImprovedGarrisonsHarmonyIsolation.RemoveModulePatches(
                assembly,
                harmony,
                ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Improved Garrisons pre-Coop Harmony isolation failed");
            throw new InvalidOperationException(
                "Improved Garrisons installed an unverified Harmony patch before Coop startup. The campaign was aborted.",
                ex);
        }

        if (!ImprovedGarrisonsCompatibilityManifest.TryValidate(assembly, out var methods, out var failure))
        {
            Logger.Fatal("Improved Garrisons co-op adapter failed closed: {Failure}", failure);
            throw new InvalidOperationException(
                "Unsupported Improved Garrisons binary/API. Coop startup was aborted so the unguarded mod cannot mutate the campaign: " +
                failure);
        }

        foreach (var pair in methods.Where(pair => pair.Key.Kind == ImprovedGarrisonsPatchKind.RoutedSetting))
            routedMethods[RoutedKey(pair.Key.TypeName, pair.Key.MethodName)] = (pair.Key, pair.Value);

        lock (PatchSync)
        {
            var tickPostfix = AccessTools.Method(
                typeof(ImprovedGarrisonsAuthorityPatches),
                nameof(ImprovedGarrisonsAuthorityPatches.ServerTickPostfix));
            var expected = methods.Select(pair => (
                Original: pair.Value,
                Prefix: PrefixMethodFor(pair.Key.Kind),
                Postfix: pair.Key.Kind == ImprovedGarrisonsPatchKind.ServerTick ? tickPostfix : null))
                .ToArray();

            if (ReferenceEquals(patchedAssembly, assembly) ||
                ImprovedGarrisonsHarmonyIsolation.HasOwnerPatchesTargetingAssembly(
                    assembly,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId))
            {
                ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(
                    expected,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                    assembly,
                    expected.Select(guard => guard.Original),
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                patchedAssembly = assembly;
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

                ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(
                    expected,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                    assembly,
                    expected.Select(guard => guard.Original),
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                patchedAssembly = assembly;
            }
            catch (Exception ex)
            {
                foreach (var patch in applied)
                {
                    harmony.Unpatch(patch.Original, patch.Prefix);
                    if (patch.Postfix != null) harmony.Unpatch(patch.Original, patch.Postfix);
                }
                Logger.Fatal(ex, "Improved Garrisons co-op adapter patching failed; rolled back all adapter detours");
                throw new InvalidOperationException(
                    "Improved Garrisons co-op guard installation failed. Coop startup was aborted after rolling back partial detours.",
                    ex);
            }
        }

        Logger.Information(
            "Improved Garrisons {Version} co-op adapter enabled with {Count} guarded methods; removed {Removed} pre-Coop module patches",
            ImprovedGarrisonsCompatibilityManifest.SupportedModuleVersion,
            methods.Count,
            removedModulePatches);
        return true;
    }

    private static HarmonyMethod PrefixFor(ImprovedGarrisonsPatchKind kind)
        => new HarmonyMethod(PrefixMethodFor(kind));

    private static MethodInfo PrefixMethodFor(ImprovedGarrisonsPatchKind kind)
    {
        string name;
        switch (kind)
        {
            case ImprovedGarrisonsPatchKind.ServerTick:
            case ImprovedGarrisonsPatchKind.ServerTickNoPublication:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerTickPrefix);
                break;
            case ImprovedGarrisonsPatchKind.ServerLifecycle:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix);
                break;
            case ImprovedGarrisonsPatchKind.DeterministicInitialization:
                name = nameof(ImprovedGarrisonsAuthorityPatches.DeterministicInitializationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.RoutedSetting:
                name = nameof(ImprovedGarrisonsAuthorityPatches.RoutedSettingPrefix);
                break;
            case ImprovedGarrisonsPatchKind.DeniedClientUi:
                name = nameof(ImprovedGarrisonsAuthorityPatches.DeniedClientUiPrefix);
                break;
            case ImprovedGarrisonsPatchKind.StablePartyIdentity:
                name = nameof(ImprovedGarrisonsAuthorityPatches.StablePartyIdentityPrefix);
                break;
            case ImprovedGarrisonsPatchKind.FinanceRead:
                name = nameof(ImprovedGarrisonsAuthorityPatches.FinanceReadPrefix);
                break;
            case ImprovedGarrisonsPatchKind.ClientDefaultConfig:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ClientDefaultConfigPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerTownSettings:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerTownSettingsPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerSettlementInitialization:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerSettlementInitializationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerSettlementOwnerChanged:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerSettlementOwnerChangedPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PartyHomeResolver:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PartyHomeResolverPrefix);
                break;
            case ImprovedGarrisonsPatchKind.VillagePartySpawnResolver:
                name = nameof(ImprovedGarrisonsAuthorityPatches.VillagePartySpawnResolverPrefix);
                break;
            case ImprovedGarrisonsPatchKind.VillagePartyLookup:
                name = nameof(ImprovedGarrisonsAuthorityPatches.VillagePartyLookupPrefix);
                break;
            default:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix);
                break;
        }
        return AccessTools.Method(typeof(ImprovedGarrisonsAuthorityPatches), name);
    }

    private void Handle_AllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> payload)
    {
        if (!compatible) return;
        ImprovedGarrisonsAuthorityPatches.ResetTickLedger();
        requestLedger.Reset();
        revision = 0;
        nextRequestId = 0;
        lastPublishedHash = null;
        lastAppliedHash = null;
        stateReady = true;
        if (ModInformation.IsClient)
            network.SendAll(new NetworkRequestImprovedGarrisonsState());
        else
            PublishStateIfChanged();
    }

    private void Handle_StateRequest(MessagePayload<NetworkRequestImprovedGarrisonsState> payload)
    {
        if (!compatible || ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        if (!stateReady)
        {
            DenyPeerOrAbortSession(peer, "authoritative state is not ready");
            return;
        }
        GameThread.RunSafe(() => SendState(peer), context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_SettingRequest(MessagePayload<NetworkRequestImprovedGarrisonsSettingChange> payload)
    {
        if (!compatible || ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        if (!stateReady)
        {
            DenyPeerOrAbortSession(peer, "authoritative state is not ready for setting requests");
            return;
        }
        var request = payload.What;
        GameThread.RunSafe(
            () => ApplySettingRequest(peer, request),
            context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_State(MessagePayload<NetworkImprovedGarrisonsState> payload)
    {
        // Network messages are published with their transport peer as source. On a client the
        // only transport peer is its server connection; reject locally published/forged broker
        // messages that have no transport identity.
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
                serverPeer,
                localIsClient: ModInformation.IsClient))
            return;

        GameThread.RunSafe(
            () =>
            {
                if (TryApplyState(payload.What, out var rejection)) return;

                stateReady = false;
                Logger.Fatal(
                    "Disconnecting from the Coop server because Improved Garrisons state could not be accepted: {Failure}",
                    rejection);
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        "Improved Garrisons compatibility validation failed. The co-op connection was closed to prevent a divergent campaign. " +
                        rejection));
                }
                catch (Exception displayException)
                {
                    Logger.Error(displayException, "Could not display the Improved Garrisons compatibility failure");
                }
                finally
                {
                    serverPeer.Disconnect();
                }
            },
            context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void ApplySettingRequest(NetPeer peer, NetworkRequestImprovedGarrisonsSettingChange request)
    {
        if (!IsRequestShapeValid(request))
        {
            Logger.Warning("Rejected malformed Improved Garrisons request {RequestId} from peer {Peer}",
                request.RequestId, peer.Id);
            SendState(peer);
            return;
        }

        if (requestLedger.HasSeen(peer, request.RequestId))
        {
            // Commands such as ReturnRecruiter are not inherently idempotent. A replay receives
            // the resulting state but can never invoke the native method twice.
            SendState(peer);
            return;
        }

        if (request.ExpectedRevision != revision)
        {
            Logger.Warning(
                "Rejected stale Improved Garrisons request {RequestId} from peer {Peer}: expected revision {Expected}, server is {Actual}",
                request.RequestId, peer.Id, request.ExpectedRevision, revision);
            SendState(peer);
            return;
        }

        if (!TryResolveOwnedTown(peer, request.TownId, out var town)) return;
        if (!routedMethods.TryGetValue(RoutedKey(request.ManagerType, request.Method), out var route))
        {
            Logger.Warning("Rejected unknown Improved Garrisons UI operation {Type}.{Method}", request.ManagerType, request.Method);
            return;
        }

        var parameters = route.Method.GetParameters();
        var arguments = new object[parameters.Length];
        arguments[0] = town;
        if (parameters.Length == 2)
        {
            if (!ImprovedGarrisonsCanonicalState.TryParseValue(request.Value, parameters[1].ParameterType, out var parsed) ||
                !IsValueAllowed(route.Method.Name, parsed))
            {
                Logger.Warning("Rejected invalid Improved Garrisons value for {Method}: {Value}", route.Method.Name, request.Value);
                return;
            }
            arguments[1] = parsed;
        }

        var manager = ResolveManager(route.Method.DeclaringType);
        if (manager == null)
        {
            Logger.Error("Cannot resolve Improved Garrisons manager {Type}", route.Method.DeclaringType?.FullName);
            return;
        }

        // Primitive setters are expected to touch only canonical Improved Garrisons state, but
        // reflection targets can still throw after a partial assignment. Capture a detached
        // rollback image before invoking so a failed request cannot strand the server between
        // revisions or become repeatable.
        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly,
                objectManager,
                out var rollbackValues,
                out var rollbackHash,
                out var captureFailure))
        {
            DenyPeerOrAbortSession(
                peer,
                "could not capture rollback state for setting request " + request.RequestId + ": " + captureFailure);
            return;
        }

        // Record immediately before invoking. If an upstream method partially mutates and throws,
        // retrying the same request ID must still be unable to duplicate that mutation.
        requestLedger.Record(peer, request.RequestId);
        try
        {
            route.Method.Invoke(manager, arguments);
        }
        catch (Exception ex)
        {
            var reported = ex is TargetInvocationException invocation
                ? invocation.InnerException ?? invocation
                : ex;
            if (!TryRestoreCanonicalState(rollbackValues, rollbackHash, out var rollbackFailure))
            {
                DenyPeerOrAbortSession(
                    peer: null,
                    "setting request " + request.RequestId + " failed after a partial mutation and rollback failed: " +
                    rollbackFailure);
            }

            Logger.Error(reported,
                "Improved Garrisons operation {Type}.{Method} failed for request {RequestId}",
                request.ManagerType, request.Method, request.RequestId);
            PublishStateIfChanged(peer);
            return;
        }

        PublishStateIfChanged(peer);
    }

    private bool TryRestoreCanonicalState(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> rollbackValues,
        string expectedHash,
        out string failure)
    {
        if (!ImprovedGarrisonsCanonicalState.TryApply(
                assembly,
                objectManager,
                rollbackValues,
                out failure))
            return false;

        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly,
                objectManager,
                out _,
                out var restoredHash,
                out failure))
            return false;

        if (!string.Equals(restoredHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"rollback hash {restoredHash ?? "missing"} did not restore {expectedHash ?? "missing"}";
            return false;
        }

        failure = null;
        return true;
    }

    private bool TryResolveOwnedTown(NetPeer peer, string townId, out Town town)
    {
        town = null;
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject<Clan>(player.ClanId, out var clan) ||
            !objectManager.TryGetObject(townId, out town) ||
            town?.OwnerClan != clan)
        {
            Logger.Warning("Rejected Improved Garrisons request from peer {Peer}: town {TownId} is not owned by its clan", peer.Id, townId);
            return false;
        }
        return true;
    }

    private object ResolveManager(Type type)
    {
        var instance = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        if (instance != null) return instance;

        if (type?.FullName == "ImprovedGarrisons.SaveSystem.GarrisonBehavior")
            return assembly.GetType("ImprovedGarrisons.Main", false)
                ?.GetProperty("GarrisonBehavior", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
        return null;
    }

    private void SendState(NetPeer peer)
    {
        PublishStateIfChanged(peer);
    }

    private void PublishStateIfChanged(NetPeer peer = null)
    {
        if (!ModInformation.IsServer) return;
        if (!stateReady)
        {
            // Campaign callbacks can run while object registration is still assembling the
            // authoritative graph. A connected requester may never proceed without a snapshot;
            // incidental pre-registration callbacks simply wait for the initial publication.
            if (peer != null)
                DenyPeerOrAbortSession(peer, "authoritative state is not ready");
            return;
        }
        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly, objectManager, out var values, out var hash, out var failure))
        {
            DenyPeerOrAbortSession(peer, "could not capture Improved Garrisons server state: " + failure);
            return;
        }

        var initialPublication = lastPublishedHash == null;
        var changed = !initialPublication &&
                      !string.Equals(lastPublishedHash, hash, StringComparison.OrdinalIgnoreCase);
        var publishedRevision = changed ? revision + 1 : revision;

        var message = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            publishedRevision,
            hash,
            values);
        if (!IsSnapshotShapeValid(message, out failure))
        {
            DenyPeerOrAbortSession(peer, "captured Improved Garrisons server state was invalid: " + failure);
            return;
        }

        try
        {
            if (initialPublication || changed) network.SendAll(message);
            else if (peer != null) network.Send(peer, message);
        }
        catch (Exception ex)
        {
            DenyPeerOrAbortSession(peer, "authoritative snapshot publication failed: " + ex.Message);
            return;
        }

        revision = publishedRevision;
        lastPublishedHash = hash;
    }

    internal bool ApplyState(NetworkImprovedGarrisonsState state)
        => TryApplyState(state, out _);

    internal bool TryApplyState(NetworkImprovedGarrisonsState state, out string rejection)
    {
        if (!TryAcceptState(
                revision,
                lastAppliedHash,
                state,
                ApplyCanonicalValues,
                out var acceptedRevision,
                out var acceptedHash,
                out rejection))
            return false;

        revision = acceptedRevision;
        lastAppliedHash = acceptedHash;
        return true;
    }

    private bool ApplyCanonicalValues(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure) =>
        ImprovedGarrisonsCanonicalState.TryApply(assembly, objectManager, values, out failure);

    internal static bool TryAcceptState(
        long currentRevision,
        string currentHash,
        NetworkImprovedGarrisonsState state,
        CanonicalStateApplier apply,
        out long acceptedRevision,
        out string acceptedHash,
        out string rejection)
    {
        acceptedRevision = currentRevision;
        acceptedHash = currentHash;
        rejection = null;

        if (!IsSnapshotShapeValid(state, out var failure))
        {
            rejection = "malformed authoritative state: " + failure;
            Logger.Error("Rejected Improved Garrisons state: {Failure}", rejection);
            return false;
        }

        var values = state.Values ?? Array.Empty<ImprovedGarrisonsStateValue>();
        var wireHash = ImprovedGarrisonsCanonicalState.ComputeHash(values);
        if (!string.Equals(wireHash, state.CanonicalHash, StringComparison.OrdinalIgnoreCase))
        {
            rejection = $"corrupt state revision {state.Revision}: hash {wireHash} != {state.CanonicalHash}";
            Logger.Error("Rejected Improved Garrisons {Failure}", rejection);
            return false;
        }

        if (!CanApplyState(currentRevision, currentHash, state.Revision, wireHash))
        {
            rejection = $"stale or conflicting state {state.Revision}/{wireHash}; current is {currentRevision}/{currentHash}";
            Logger.Warning("Rejected Improved Garrisons {Failure}", rejection);
            return false;
        }

        if (state.Revision == currentRevision &&
            string.Equals(currentHash, wireHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (apply == null)
        {
            rejection = "canonical state applier is unavailable";
            Logger.Error("Rejected Improved Garrisons state: {Failure}", rejection);
            return false;
        }

        if (!apply(values, out failure))
        {
            rejection = "canonical state could not be applied: " + (failure ?? "unknown semantic failure");
            Logger.Error("Rejected Improved Garrisons state revision {Revision}: {Failure}", state.Revision, rejection);
            return false;
        }

        // Revision/hash are committed only after the detached canonical transaction succeeds.
        acceptedRevision = state.Revision;
        acceptedHash = wireHash;
        return true;
    }

    internal static bool CanApplyState(
        long currentRevision,
        string currentHash,
        long incomingRevision,
        string incomingHash)
    {
        if (incomingRevision < currentRevision) return false;
        if (incomingRevision > currentRevision) return true;
        return currentHash == null || string.Equals(currentHash, incomingHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        failure = string.IsNullOrWhiteSpace(failure) ? "unknown authoritative-state failure" : failure;
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Improved Garrisons state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal(
            "Aborting the Coop session because authoritative Improved Garrisons state is unavailable: {Failure}",
            failure);
        throw new InvalidOperationException(
            "The Coop session cannot continue without authoritative Improved Garrisons state: " + failure);
    }

    internal static bool IsRequestShapeValid(NetworkRequestImprovedGarrisonsSettingChange request) =>
        request.RequestId > 0 && request.ExpectedRevision >= 0 &&
        HasLength(request.ManagerType, 1, MaxRequestManagerLength) &&
        HasLength(request.Method, 1, MaxRequestMethodLength) &&
        HasLength(request.TownId, 1, MaxObjectIdLength) &&
        HasLength(request.Value, 0, MaxRequestValueLength);

    internal static bool IsSnapshotShapeValid(NetworkImprovedGarrisonsState state, out string failure)
    {
        failure = null;
        if (state == null) { failure = "snapshot is null"; return false; }
        if (!string.Equals(state.AdapterVersion, ImprovedGarrisonsCompatibilityManifest.AdapterVersion, StringComparison.Ordinal))
        {
            failure = $"adapter version {state.AdapterVersion ?? "missing"} is unsupported";
            return false;
        }
        if (state.Revision < 0) { failure = "revision is negative"; return false; }
        if (!HasLength(state.CanonicalHash, 64, 64) || state.CanonicalHash.Any(character => !Uri.IsHexDigit(character)))
        {
            failure = "canonical hash is not a 64-character hexadecimal SHA-256";
            return false;
        }

        var values = state.Values;
        if (values == null) { failure = "values are null"; return false; }
        if (values.Length > MaxSnapshotValues)
        {
            failure = $"value count {values.Length} exceeds {MaxSnapshotValues}";
            return false;
        }

        long characters = 0;
        var canonicalKeys = new HashSet<Tuple<string, string, string>>();
        foreach (var value in values)
        {
            if (value == null || !HasLength(value.Scope, 1, 32) ||
                !HasLength(value.TargetId, 0, MaxObjectIdLength) ||
                !HasLength(value.Property, 1, MaxSnapshotPropertyLength) ||
                !HasLength(value.Value, 0, MaxSnapshotValueLength))
            {
                failure = "a canonical value has a null or oversized field";
                return false;
            }
            if (!canonicalKeys.Add(Tuple.Create(value.Scope, value.TargetId, value.Property)))
            {
                failure = $"duplicate canonical property {value.Scope}/{value.TargetId}/{value.Property}";
                return false;
            }
            characters += value.Scope.Length + value.TargetId.Length + value.Property.Length + value.Value.Length;
            if (characters > MaxSnapshotCharacters)
            {
                failure = $"canonical payload exceeds {MaxSnapshotCharacters} characters";
                return false;
            }
        }

        return true;
    }

    internal static bool IsValueAllowed(string method, object value)
    {
        if (value is bool) return true;
        if (value is float percentage) return percentage >= 0f && percentage <= 1f;
        if (value is not int integer) return value == null;

        if (method == "SetTownMaxUpgradeTier") return integer >= 1 && integer <= 10;
        if (method == "SetRecruiterAmountToRecruit") return integer >= 1 && integer <= 150;
        return integer >= 0 && integer <= 10000;
    }

    private bool TryGetSettingsDictionary(out IDictionary dictionary)
    {
        dictionary = null;
        var behavior = assembly?.GetType("ImprovedGarrisons.Main", false)
            ?.GetProperty("GarrisonBehavior", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        dictionary = behavior?.GetType()
            .GetProperty("SettlementSettingsData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(behavior) as IDictionary;
        return dictionary != null;
    }

    private static bool HasLength(string value, int minimum, int maximum) =>
        value != null && value.Length >= minimum && value.Length <= maximum;

    private static string RoutedKey(string type, string method) => (type ?? string.Empty) + "::" + (method ?? string.Empty);
}
