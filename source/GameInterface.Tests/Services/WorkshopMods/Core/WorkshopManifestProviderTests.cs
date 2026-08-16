using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopManifestProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coop-workshop-provider-" + Guid.NewGuid());

    public WorkshopManifestProviderTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "FriendEdition.dll"), new byte[] { 4, 2, 1 });
        File.WriteAllText(Path.Combine(root, "SubModule.xml"), "<Module />");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Provider_UsesInjectableDiscovery_AndCachesForLateJoiners()
    {
        var discovery = new FakeDiscovery(root);
        var provider = new WorkshopManifestProvider(discovery);

        WorkshopManifestPreparation clientPreparation = provider.PrepareManifest(WorkshopPeerRole.Client);
        Assert.Equal(1, discovery.CallCount);
        Assert.False(provider.TryGetPreparedManifest(
            WorkshopPeerRole.Client, out _, out string notBuiltReason));
        Assert.Contains("hashing has not started", notBuiltReason, StringComparison.OrdinalIgnoreCase);

        WorkshopCompatibilityManifest first = provider.BuildPreparedManifest(clientPreparation);
        Assert.Equal(1, discovery.CallCount); // worker build hashes only the frozen snapshot
        Assert.True(provider.TryGetPreparedManifest(
            WorkshopPeerRole.Client, out WorkshopCompatibilityManifest lateJoin, out _));
        WorkshopCompatibilityManifest server = provider.BuildPreparedManifest(
            provider.PrepareManifest(WorkshopPeerRole.Server));

        Assert.Same(first, lateJoin);
        Assert.Equal(2, discovery.CallCount); // once per peer-role manifest, never per joiner
        Assert.Equal(14, first.Entries.Length);
        Assert.Equal(14, server.Entries.Length);
        Assert.All(first.Entries, entry => Assert.True(entry.ManagedDistributionComponent));
    }

    [Fact]
    public void Provider_PersistsBuildFailure_AndLateJoinNeverRetriesHashing()
    {
        var discovery = new FakeDiscovery(root);
        var provider = new WorkshopManifestProvider(discovery);
        WorkshopManifestPreparation preparation = provider.PrepareManifest(WorkshopPeerRole.Server);
        Directory.Delete(root, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(() => provider.BuildPreparedManifest(preparation));
        Assert.False(provider.TryGetPreparedManifest(
            WorkshopPeerRole.Server, out WorkshopCompatibilityManifest manifest, out string reason));
        Assert.Null(manifest);
        Assert.Contains("preparation failed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => provider.BuildPreparedManifest(preparation));
        Assert.Equal(1, discovery.CallCount);
    }

    [Fact]
    public void Provider_PersistsDiscoveryFailure_ForAllFutureJoins()
    {
        var provider = new WorkshopManifestProvider(new ThrowingDiscovery());

        Assert.Throws<InvalidDataException>(() => provider.PrepareManifest(WorkshopPeerRole.Server));
        Assert.False(provider.TryGetPreparedManifest(
            WorkshopPeerRole.Server, out WorkshopCompatibilityManifest manifest, out string reason));
        Assert.Null(manifest);
        Assert.Contains("preparation failed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => provider.PrepareManifest(WorkshopPeerRole.Server));
    }

    [Fact]
    public void Provider_RetiredRbmIsNotAdvertisedOnEitherRole()
    {
        var discovery = new FakeDiscovery(root);
        var serverProvider = new WorkshopManifestProvider(discovery);
        WorkshopCompatibilityManifest server = serverProvider.BuildPreparedManifest(
            serverProvider.PrepareManifest(WorkshopPeerRole.Server));
        var clientProvider = new WorkshopManifestProvider(discovery);
        WorkshopCompatibilityManifest client = clientProvider.BuildPreparedManifest(
            clientProvider.PrepareManifest(WorkshopPeerRole.Client));

        Assert.DoesNotContain(server.Entries, entry => entry.ModuleId == "RBM");
        Assert.DoesNotContain(client.Entries, entry => entry.ModuleId == "RBM");
    }

    private sealed class FakeDiscovery : IWorkshopModuleDiscovery
    {
        private readonly string root;
        private readonly FriendEditionWorkshopModuleCatalog catalog = new();

        public FakeDiscovery(string root) => this.root = root;

        public int CallCount { get; private set; }

        public IReadOnlyList<WorkshopModuleRuntimeInfo> Discover()
        {
            CallCount++;
            return catalog.Modules.Select(module =>
                    new WorkshopModuleRuntimeInfo(
                        module,
                        module.Version,
                        root,
                        active: true,
                        managedDistributionComponent: true,
                        activationOrderValid: true,
                        pinnedContentSha256: new WorkshopModuleFileHasher().Hash(root).ContentSha256,
                        pinnedConfigurationSha256: new WorkshopModuleFileHasher().Hash(root).ConfigurationSha256))
                .ToArray();
        }
    }

    private sealed class ThrowingDiscovery : IWorkshopModuleDiscovery
    {
        public IReadOnlyList<WorkshopModuleRuntimeInfo> Discover() =>
            throw new InvalidDataException("Receipt is invalid.");
    }
}
