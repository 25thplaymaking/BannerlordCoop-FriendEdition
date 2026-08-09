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
        Assert.Equal(11, first.Entries.Length);
        Assert.Equal(11, server.Entries.Length); // includes trusted client-visual package digest
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

    /// <summary>
    /// The wine-hosted server engine cannot load some modules' real content (RBM crashes it), so
    /// such a module is activated server-side as a bare SubModule.xml stub. The server manifest
    /// must then advertise the receipt's audited pins — clients get byte-verified against the
    /// audited package — while the same stub on a CLIENT is a broken install and stays unmanaged.
    /// </summary>
    [Fact]
    public void Provider_ServerStub_AttestsReceiptPins_ClientStubStaysUnmanaged()
    {
        string stubRoot = Path.Combine(root, "stub-module");
        Directory.CreateDirectory(stubRoot);
        File.WriteAllText(Path.Combine(stubRoot, "SubModule.xml"), "<Module />");
        string pinnedContent = new string('a', 64);
        string pinnedConfiguration = new string('b', 64);
        var discovery = new StubDiscovery(stubRoot, pinnedContent, pinnedConfiguration);

        var serverProvider = new WorkshopManifestProvider(discovery);
        WorkshopCompatibilityManifest server = serverProvider.BuildPreparedManifest(
            serverProvider.PrepareManifest(WorkshopPeerRole.Server));

        WorkshopCompatibilityManifestEntry serverEntry = server.Entries.Single(
            entry => entry.ModuleId == "RBM");
        Assert.True(serverEntry.ManagedDistributionComponent);
        Assert.Equal(pinnedContent, serverEntry.ContentSha256);
        Assert.Equal(pinnedConfiguration, serverEntry.ConfigurationSha256);

        var clientProvider = new WorkshopManifestProvider(discovery);
        WorkshopCompatibilityManifest client = clientProvider.BuildPreparedManifest(
            clientProvider.PrepareManifest(WorkshopPeerRole.Client));
        WorkshopCompatibilityManifestEntry clientEntry = client.Entries.Single(
            entry => entry.ModuleId == "RBM");
        Assert.False(clientEntry.ManagedDistributionComponent);
        Assert.NotEqual(pinnedContent, clientEntry.ContentSha256);
    }

    private sealed class StubDiscovery : IWorkshopModuleDiscovery
    {
        private readonly string stubRoot;
        private readonly string pinnedContent;
        private readonly string pinnedConfiguration;
        private readonly FriendEditionWorkshopModuleCatalog catalog = new();

        public StubDiscovery(string stubRoot, string pinnedContent, string pinnedConfiguration)
        {
            this.stubRoot = stubRoot;
            this.pinnedContent = pinnedContent;
            this.pinnedConfiguration = pinnedConfiguration;
        }

        public IReadOnlyList<WorkshopModuleRuntimeInfo> Discover() =>
            catalog.Modules.Select(module => module.ModuleId == "RBM"
                ? new WorkshopModuleRuntimeInfo(
                    module, module.Version, stubRoot,
                    active: true,
                    managedDistributionComponent: true,
                    activationOrderValid: true,
                    pinnedContentSha256: pinnedContent,
                    pinnedConfigurationSha256: pinnedConfiguration)
                : new WorkshopModuleRuntimeInfo(
                    module, module.Version, Path.GetDirectoryName(stubRoot),
                    active: true,
                    managedDistributionComponent: true,
                    activationOrderValid: true,
                    pinnedContentSha256: new WorkshopModuleFileHasher().Hash(Path.GetDirectoryName(stubRoot)).ContentSha256,
                    pinnedConfigurationSha256: new WorkshopModuleFileHasher().Hash(Path.GetDirectoryName(stubRoot)).ConfigurationSha256))
            .ToArray();
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
