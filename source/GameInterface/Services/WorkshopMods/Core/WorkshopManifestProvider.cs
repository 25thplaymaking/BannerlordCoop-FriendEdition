using Common.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Immutable result of the short, engine-affine discovery phase. Callers capture it on the
/// game thread, then pass it to <see cref="IWorkshopManifestProvider.BuildPreparedManifest"/>
/// on a worker thread. No TaleWorlds API is consulted during the build phase.
/// </summary>
public sealed class WorkshopManifestPreparation
{
    internal WorkshopManifestPreparation(
        WorkshopPeerRole peerRole,
        IEnumerable<WorkshopModuleRuntimeInfo> modules)
    {
        PeerRole = peerRole;
        Modules = (modules ?? throw new ArgumentNullException(nameof(modules))).ToArray();
    }

    public WorkshopPeerRole PeerRole { get; }
    public IReadOnlyList<WorkshopModuleRuntimeInfo> Modules { get; }
}

public interface IWorkshopManifestProvider : IGameAbstraction
{
    /// <summary>False only for isolated legacy unit-test compositions that do not load GameInterfaceModule.</summary>
    bool EnforceHandshake { get; }

    /// <summary>
    /// Captures active-module and module-path metadata. Production callers must invoke this on the
    /// game thread because the runtime discovery implementation reads TaleWorlds ModuleHelper state.
    /// Any failure is retained and makes subsequent joins fail closed for this session.
    /// </summary>
    WorkshopManifestPreparation PrepareManifest(WorkshopPeerRole peerRole);

    /// <summary>
    /// Hashes the frozen preparation and caches the resulting manifest. This is the only phase that
    /// performs the potentially multi-gigabyte package hash and is safe to run on a worker thread.
    /// </summary>
    WorkshopCompatibilityManifest BuildPreparedManifest(WorkshopManifestPreparation preparation);

    /// <summary>
    /// Non-blocking lookup used by connection handlers. It never performs discovery or hashing.
    /// </summary>
    bool TryGetPreparedManifest(
        WorkshopPeerRole peerRole,
        out WorkshopCompatibilityManifest manifest,
        out string unavailableReason);
}

/// <summary>
/// Runtime provider used by the actual client and server lifetime scopes. Discovery and file hashing
/// are injectable, and results are cached for the session so late joiners do not re-hash the package.
/// </summary>
public sealed class WorkshopManifestProvider : IWorkshopManifestProvider
{
    private static readonly ILogger Logger = LogManager.GetLogger<WorkshopManifestProvider>();
    private static readonly TimeSpan ConcurrentBuildWaitTimeout = TimeSpan.FromMinutes(2);

    private readonly IWorkshopModuleDiscovery discovery;
    private readonly WorkshopModuleFileHasher fileHasher;
    private readonly object manifestGate = new();
    private readonly Dictionary<WorkshopPeerRole, ManifestState> states = new();

    public WorkshopManifestProvider(IWorkshopModuleDiscovery discovery)
        : this(discovery, new WorkshopModuleFileHasher())
    {
    }

    public WorkshopManifestProvider(IWorkshopModuleDiscovery discovery, WorkshopModuleFileHasher fileHasher)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.fileHasher = fileHasher ?? throw new ArgumentNullException(nameof(fileHasher));
    }

    public bool EnforceHandshake => true;

    public WorkshopManifestPreparation PrepareManifest(WorkshopPeerRole peerRole)
    {
        ValidatePeerRole(peerRole);

        lock (manifestGate)
        {
            ManifestState state = GetOrCreateState(peerRole);
            ThrowIfFailed(state);
            if (state.Preparation != null) return state.Preparation;

            try
            {
                state.Preparation = new WorkshopManifestPreparation(peerRole, discovery.Discover());
                return state.Preparation;
            }
            catch (Exception exception)
            {
                state.Failure = exception;
                throw;
            }
        }
    }

    public WorkshopCompatibilityManifest BuildPreparedManifest(WorkshopManifestPreparation preparation)
    {
        if (preparation == null) throw new ArgumentNullException(nameof(preparation));
        ValidatePeerRole(preparation.PeerRole);

        ManifestState state;
        lock (manifestGate)
        {
            state = GetOrCreateState(preparation.PeerRole);
            ThrowIfFailed(state);
            if (!ReferenceEquals(state.Preparation, preparation))
                throw new InvalidOperationException("The Workshop manifest preparation does not belong to this provider.");

            // A player can reconnect while the previous ValidateModuleState's worker is still
            // finishing the same immutable snapshot. Share that build instead of launching a
            // second multi-gigabyte hash or failing the reconnect spuriously. This wait occurs only
            // on worker/test threads; connection handlers use TryGetPreparedManifest and never wait.
            DateTime waitDeadline = DateTime.UtcNow + ConcurrentBuildWaitTimeout;
            while (state.Building && state.Manifest == null && state.Failure == null)
            {
                TimeSpan remaining = waitDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(manifestGate, remaining))
                    throw new TimeoutException("The existing Workshop manifest build did not finish in time.");
            }

            ThrowIfFailed(state);
            if (state.Manifest != null) return state.Manifest;
            state.Building = true;
        }

        try
        {
            var hashesByRoot = new Dictionary<string, WorkshopModuleHashResult>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<WorkshopCompatibilityManifestEntry>();
            foreach (var module in preparation.Modules)
            {
                if (!hashesByRoot.TryGetValue(module.RootPath, out var hashes))
                {
                    hashes = fileHasher.Hash(module.RootPath);
                    hashesByRoot.Add(module.RootPath, hashes);
                }

                bool pinsMatch = module.ManagedDistributionComponent &&
                    string.Equals(module.PinnedContentSha256, hashes.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(module.PinnedConfigurationSha256, hashes.ConfigurationSha256, StringComparison.OrdinalIgnoreCase);

                // Headless-server stub: the wine-hosted server engine cannot load some modules'
                // real content (RBM's combat parameters crash it natively), so those modules are
                // activated as a bare SubModule.xml. The receipt still carries the audited pins,
                // and the server advertises THOSE — every client is byte-verified against the
                // audited package even though the server itself cannot execute it. Clients never
                // take this branch: a stub on a client is a broken install, reported as-is.
                bool serverStubAttestation = !pinsMatch &&
                    preparation.PeerRole == WorkshopPeerRole.Server &&
                    module.ManagedDistributionComponent &&
                    hashes.SubModuleOnly &&
                    !string.IsNullOrEmpty(module.PinnedContentSha256) &&
                    !string.IsNullOrEmpty(module.PinnedConfigurationSha256);

                // Discovery already warns when a component fails its receipt/path/version checks,
                // but a component that passes all of those and then fails only on the live hash
                // used to publish itself as unmanaged in complete silence — the peer refusing
                // every join had no idea why, and the diagnosis lived entirely on the joiner's
                // screen. Name the module and which half diverged.
                if (!pinsMatch && !serverStubAttestation && module.ManagedDistributionComponent)
                {
                    Logger.Warning(
                        "'{ModuleId}' no longer matches its receipt pins and will be advertised as an " +
                        "unmanaged copy: content={ContentMatches} configuration={ConfigurationMatches} " +
                        "(root '{RootPath}'). Every peer will refuse to join until the package is restored.",
                        module.Expectation.ModuleId,
                        string.Equals(module.PinnedContentSha256, hashes.ContentSha256, StringComparison.OrdinalIgnoreCase),
                        string.Equals(module.PinnedConfigurationSha256, hashes.ConfigurationSha256, StringComparison.OrdinalIgnoreCase),
                        module.RootPath);
                }

                entries.Add(new WorkshopCompatibilityManifestEntry(
                    module.Expectation.ModuleId,
                    module.Expectation.WorkshopId,
                    module.Version,
                    module.Expectation.Role,
                    module.Expectation.Profile,
                    serverStubAttestation ? module.PinnedContentSha256 : hashes.ContentSha256,
                    serverStubAttestation ? module.PinnedConfigurationSha256 : hashes.ConfigurationSha256,
                    pinsMatch || serverStubAttestation,
                    module.ActivationOrderValid,
                    module.Expectation.LoadOrder,
                    module.Active));
            }

            var manifest = new WorkshopCompatibilityManifest(preparation.PeerRole, entries);
            if (!manifest.TryValidateWireShape(out string error))
                throw new InvalidOperationException("Generated an invalid Workshop manifest: " + error);

            lock (manifestGate)
            {
                state.Manifest = manifest;
                state.Building = false;
                Monitor.PulseAll(manifestGate);
            }
            return manifest;
        }
        catch (Exception exception)
        {
            lock (manifestGate)
            {
                state.Failure = exception;
                state.Building = false;
                Monitor.PulseAll(manifestGate);
            }
            throw;
        }
    }

    public bool TryGetPreparedManifest(
        WorkshopPeerRole peerRole,
        out WorkshopCompatibilityManifest manifest,
        out string unavailableReason)
    {
        ValidatePeerRole(peerRole);

        lock (manifestGate)
        {
            if (states.TryGetValue(peerRole, out ManifestState state))
            {
                if (state.Manifest != null)
                {
                    manifest = state.Manifest;
                    unavailableReason = null;
                    return true;
                }

                if (state.Failure != null)
                {
                    manifest = null;
                    unavailableReason =
                        $"preparation failed ({state.Failure.GetType().Name}); inspect the server log and private suite installation";
                    return false;
                }

                manifest = null;
                unavailableReason = state.Building
                    ? "preparation is still in progress"
                    : "runtime metadata was captured but hashing has not started";
                return false;
            }

            manifest = null;
            unavailableReason = "preparation has not started";
            return false;
        }
    }

    private ManifestState GetOrCreateState(WorkshopPeerRole peerRole)
    {
        if (!states.TryGetValue(peerRole, out ManifestState state))
        {
            state = new ManifestState();
            states.Add(peerRole, state);
        }
        return state;
    }

    private static void ThrowIfFailed(ManifestState state)
    {
        if (state.Failure != null)
        {
            throw new InvalidOperationException(
                "Workshop manifest preparation previously failed and is disabled for this session.",
                state.Failure);
        }
    }

    private static void ValidatePeerRole(WorkshopPeerRole peerRole)
    {
        if (peerRole != WorkshopPeerRole.Server && peerRole != WorkshopPeerRole.Client)
            throw new ArgumentOutOfRangeException(nameof(peerRole));
    }

    private sealed class ManifestState
    {
        public WorkshopManifestPreparation Preparation { get; set; }
        public WorkshopCompatibilityManifest Manifest { get; set; }
        public Exception Failure { get; set; }
        public bool Building { get; set; }
    }
}
