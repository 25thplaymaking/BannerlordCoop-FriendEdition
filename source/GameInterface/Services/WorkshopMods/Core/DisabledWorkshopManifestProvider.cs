using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Intentionally outside the GameInterface namespace: ServiceModule scans that namespace for
// IGameAbstraction implementations, and this fallback must never replace the enforcing runtime
// provider in a real client/server lifetime scope.
namespace FriendEdition.WorkshopCompatibility;

public sealed class DisabledWorkshopManifestProvider : IWorkshopManifestProvider
{
    private readonly IWorkshopModuleCatalog catalog;

    public DisabledWorkshopManifestProvider()
        : this(new FriendEditionWorkshopModuleCatalog())
    {
    }

    public DisabledWorkshopManifestProvider(IWorkshopModuleCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public bool EnforceHandshake => false;

    public WorkshopManifestPreparation PrepareManifest(WorkshopPeerRole peerRole) =>
        new(peerRole, Array.Empty<WorkshopModuleRuntimeInfo>());

    public WorkshopCompatibilityManifest BuildPreparedManifest(WorkshopManifestPreparation preparation)
    {
        if (preparation == null) throw new ArgumentNullException(nameof(preparation));
        return CreateManifest(preparation.PeerRole);
    }

    public bool TryGetPreparedManifest(
        WorkshopPeerRole peerRole,
        out WorkshopCompatibilityManifest manifest,
        out string unavailableReason)
    {
        manifest = CreateManifest(peerRole);
        unavailableReason = null;
        return true;
    }

    private WorkshopCompatibilityManifest CreateManifest(WorkshopPeerRole peerRole)
    {
        var entries = catalog.Modules
            .Select(module => new WorkshopCompatibilityManifestEntry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                module.Role,
                module.Profile,
                HashIdentity(module, "content"),
                HashIdentity(module, "configuration"),
                managedDistributionComponent: true,
                loadOrder: module.LoadOrder,
                active: peerRole == WorkshopPeerRole.Server
                    ? module.FeatureActiveExpectedOnServer
                    : module.FeatureActiveExpectedOnClient));
        return new WorkshopCompatibilityManifest(peerRole, entries);
    }

    private static string HashIdentity(WorkshopModuleExpectation module, string kind)
    {
        string identity = string.Join("|", kind, module.ModuleId, module.WorkshopId, module.SteamManifestId, module.Version,
            ((int)module.Role).ToString(), ((int)module.Profile).ToString());
        using var sha = SHA256.Create();
        return WorkshopCompatibilityManifest.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(identity)));
    }
}
