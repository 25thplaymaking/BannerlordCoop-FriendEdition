using GameInterface.Services.Modules;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopSuiteReceiptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coop-workshop-receipt-" + Guid.NewGuid());
    private readonly FriendEditionWorkshopModuleCatalog catalog = new();

    public WorkshopSuiteReceiptTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Coop"));
        foreach (var module in catalog.Modules)
            Directory.CreateDirectory(Path.Combine(root, module.ModuleId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Receipt_RequiresPinnedIdsVersionsAndAggregateDigest()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();

        Assert.True(receipt.TryValidate(catalog, out string error), error);

        receipt.Modules[0].SteamManifestId = "wrong";
        Assert.False(receipt.TryValidate(catalog, out error));
        Assert.Contains("unpinned", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Receipt_RequiresExactNumericLoadOrder()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();
        receipt.Modules.Single(module => module.ModuleId == "RBM").LoadOrder++;
        receipt.ReceiptSha256 = WorkshopSuiteReceipt.ComputeDigest(receipt.Modules);

        Assert.False(receipt.TryValidate(catalog, out string error));
        Assert.Contains("unpinned", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeDiscovery_AcceptsManagedSeparateModules_AndTracksInactiveVisualPackage()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();
        ModuleInfo[] active = ActiveOrder(exclude: "DismembermentPlus");
        var discovery = new RuntimeWorkshopModuleDiscovery(
            new FakeModuleInfoProvider(active),
            catalog,
            moduleId => Path.Combine(root, moduleId),
            new FakeReceiptProvider(receipt));

        IReadOnlyList<WorkshopModuleRuntimeInfo> modules = discovery.Discover();

        Assert.Equal(11, modules.Count);
        Assert.All(modules, module => Assert.True(module.ManagedDistributionComponent));
        Assert.All(modules, module => Assert.True(module.ActivationOrderValid));
        Assert.All(modules.Where(module => module.Expectation.ModuleId != "DismembermentPlus"),
            module => Assert.True(module.Active));
        Assert.False(Assert.Single(modules.Where(module =>
            module.Expectation.ModuleId == "DismembermentPlus")).Active);
    }

    [Fact]
    public void RuntimeDiscovery_FlagsWrongManagedActivationOrder()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();
        ModuleInfo[] wrongOrder = catalog.Modules
            .Select(ToModuleInfo)
            .Reverse()
            .Concat(new[]
            {
                new ModuleInfo("Native", true, false, ApplicationVersion.FromString("v1.4.3")),
                new ModuleInfo("Coop", false, false, ApplicationVersion.FromString("v0.1.0")),
            })
            .ToArray();
        var discovery = new RuntimeWorkshopModuleDiscovery(
            new FakeModuleInfoProvider(wrongOrder),
            catalog,
            moduleId => Path.Combine(root, moduleId),
            new FakeReceiptProvider(receipt));

        Assert.All(discovery.Discover(), module => Assert.False(module.ActivationOrderValid));
    }

    /// <summary>
    /// PlayerSettlement's adapter re-guards the module's load-time Harmony patches, so the module
    /// must be activated before Coop. Activating it in the usual after-Coop slot deadlocks client
    /// startup, so discovery must report that arrangement as an invalid activation order.
    /// </summary>
    [Fact]
    public void RuntimeDiscovery_RejectsPreCoopComponentPlacedAfterCoop()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();
        ModuleInfo[] afterCoop = ActiveOrder()
            .Where(module => module.Id != "PlayerSettlement")
            .Concat(new[] { ToModuleInfo(
                catalog.Modules.Single(module => module.ModuleId == "PlayerSettlement")) })
            .ToArray();
        var discovery = new RuntimeWorkshopModuleDiscovery(
            new FakeModuleInfoProvider(afterCoop),
            catalog,
            moduleId => Path.Combine(root, moduleId),
            new FakeReceiptProvider(receipt));

        Assert.All(discovery.Discover(), module => Assert.False(module.ActivationOrderValid));
    }

    [Fact]
    public void RuntimeDiscovery_RejectsIdenticalExternalWorkshopCopyAsUnmanaged()
    {
        WorkshopSuiteReceipt receipt = CreateReceipt();
        string externalRoot = Path.Combine(root, "external-workshop", "RBM");
        Directory.CreateDirectory(externalRoot);
        ModuleInfo[] active = ActiveOrder();
        var discovery = new RuntimeWorkshopModuleDiscovery(
            new FakeModuleInfoProvider(active),
            catalog,
            moduleId => moduleId == "RBM" ? externalRoot : Path.Combine(root, moduleId),
            new FakeReceiptProvider(receipt));

        WorkshopModuleRuntimeInfo rbm = Assert.Single(discovery.Discover().Where(module =>
            module.Expectation.ModuleId == "RBM"));

        Assert.True(rbm.Active);
        Assert.False(rbm.ManagedDistributionComponent);
    }

    private WorkshopSuiteReceipt CreateReceipt()
    {
        var modules = catalog.Modules.Select(module => new WorkshopSuiteReceiptEntry
        {
            ModuleId = module.ModuleId,
            WorkshopId = module.WorkshopId,
            SteamManifestId = module.SteamManifestId,
            Version = module.Version,
            LoadOrder = module.LoadOrder,
            ContentSha256 = ManifestFactory.StableHash(module.ModuleId, 'a'),
            ConfigurationSha256 = ManifestFactory.StableHash(module.ModuleId, 'b'),
        }).ToArray();

        return new WorkshopSuiteReceipt
        {
            SchemaVersion = WorkshopSuiteReceipt.CurrentSchemaVersion,
            Id = WorkshopSuiteReceipt.SuiteId,
            ModuleCount = modules.Length,
            Modules = modules,
            ReceiptSha256 = WorkshopSuiteReceipt.ComputeDigest(modules),
        };
    }

    private static ModuleInfo ToModuleInfo(WorkshopModuleExpectation module) =>
        new(module.ModuleId, false, false, ApplicationVersion.FromString(module.Version));

    /// <summary>
    /// The production activation order: frameworks, Native, the components that must precede Coop
    /// (PlayerSettlement), Coop, then the remaining managed components.
    /// </summary>
    private ModuleInfo[] ActiveOrder(string exclude = null)
    {
        var modules = catalog.Modules.Where(module => module.ModuleId != exclude).ToArray();
        return modules
            .Where(module => module.Role == WorkshopModuleRole.Framework)
            .OrderBy(module => module.LoadOrder)
            .Select(ToModuleInfo)
            .Concat(new[] { new ModuleInfo("Native", true, false, ApplicationVersion.FromString("v1.4.3")) })
            .Concat(modules
                .Where(module => module.Role != WorkshopModuleRole.Framework && module.LoadsBeforeCoop)
                .OrderBy(module => module.LoadOrder)
                .Select(ToModuleInfo))
            .Concat(new[] { new ModuleInfo("Coop", false, false, ApplicationVersion.FromString("v0.1.0")) })
            .Concat(modules
                .Where(module => module.Role != WorkshopModuleRole.Framework && !module.LoadsBeforeCoop)
                .OrderBy(module => module.LoadOrder)
                .Select(ToModuleInfo))
            .ToArray();
    }

    private sealed class FakeModuleInfoProvider : IModuleInfoProvider
    {
        private readonly IReadOnlyList<ModuleInfo> modules;
        public FakeModuleInfoProvider(IReadOnlyList<ModuleInfo> modules) => this.modules = modules;
        public IEnumerable<ModuleInfo> GetModuleInfos() => modules;
    }

    private sealed class FakeReceiptProvider : IWorkshopSuiteReceiptProvider
    {
        private readonly WorkshopSuiteReceipt receipt;
        public FakeReceiptProvider(WorkshopSuiteReceipt receipt) => this.receipt = receipt;
        public WorkshopSuiteReceipt Load(string coopModuleRoot) => receipt;
    }
}
