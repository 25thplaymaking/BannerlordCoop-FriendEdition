using GameInterface.AutoSync;
using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

/// <summary>
/// The registrar is the single decision every Workshop module funnels through, and both halves of it
/// are load-bearing in opposite directions: saying "installed" when it is not aborts all of Coop's
/// patching, and saying "not installed" when it is silently drops the module's synchronization.
/// </summary>
public sealed class WorkshopModuleRegistrarTests
{
    private const string PinnedSha = "90930a1dfb48c8cf040b8bd2c89156a69838a8dc86b8ed97e0cd8475f2081257";

    private sealed class StubModule : IWorkshopModule
    {
        private readonly string installedSha;

        internal StubModule(string installedSha) => this.installedSha = installedSha;

        public string ModuleId => "Stub.Module";
        public ulong WorkshopId => 1234567890UL;
        public ModuleFingerprint Fingerprint =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", PinnedSha);
        public string PatchCategory => "CoopWorkshopStubPatches";
        public string ResolveInstalledSha256() => installedSha;

        public bool RegisterSyncCalled { get; private set; }
        public void RegisterSync(AutoSyncRegistry registry) => RegisterSyncCalled = true;
    }

    private static StubModule Installed() => new StubModule(PinnedSha);

    private static ModOptions OptionsWith(bool enabled) => new ModOptions(new ModOptionsData
    {
        WorkshopModules = new Dictionary<string, bool> { ["Stub.Module"] = enabled },
    });

    [Fact]
    public void Module_DisabledInConfig_IsNotLive()
    {
        var module = Installed();

        var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, OptionsWith(enabled: false));

        Assert.Empty(live);
    }

    [Fact]
    public void Module_EnabledButFingerprintMismatched_IsNotLive()
    {
        var module = new StubModule(new string('b', 64));

        var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, OptionsWith(enabled: true));

        Assert.Empty(live);
    }

    [Fact]
    public void Module_EnabledAndMatching_IsLive()
    {
        var module = Installed();

        var live = WorkshopModuleRegistrar.ResolveLiveModules(new[] { module }, OptionsWith(enabled: true));

        Assert.Same(module, Assert.Single(live));
    }

    /// <summary>A module the operator never mentioned behaves as it did before the key existed.</summary>
    [Fact]
    public void Module_NotMentionedInConfig_IsLive()
    {
        var module = Installed();

        var live = WorkshopModuleRegistrar.ResolveLiveModules(
            new[] { module },
            new ModOptions(new ModOptionsData()));

        Assert.Same(module, Assert.Single(live));
    }

    /// <summary>
    /// The failure this whole branch exists to prevent: a module that is not installed must not reach
    /// the point where its Harmony category is applied.
    /// </summary>
    [Fact]
    public void Module_Absent_IsNotInstalled()
    {
        var live = WorkshopModuleRegistrar.ResolveInstalledModules(new[] { new StubModule(null) });

        Assert.Empty(live);
    }

    [Fact]
    public void Module_InstalledAtThePinnedBuild_IsInstalledRegardlessOfConfig()
    {
        var module = Installed();

        var installed = WorkshopModuleRegistrar.ResolveInstalledModules(new[] { module });

        Assert.Same(module, Assert.Single(installed));
    }

    /// <summary>Hex case is a formatting choice of whoever hashed the DLL, not a different build.</summary>
    [Fact]
    public void Module_ReportingAnUppercaseHash_IsInstalled()
    {
        var module = new StubModule(PinnedSha.ToUpperInvariant());

        Assert.Single(WorkshopModuleRegistrar.ResolveInstalledModules(new[] { module }));
    }

    [Fact]
    public void NoDeclaredModules_ResolvesToNothing()
    {
        Assert.Empty(WorkshopModuleRegistrar.ResolveInstalledModules(null));
        Assert.Empty(WorkshopModuleRegistrar.ResolveInstalledModules(Enumerable.Empty<IWorkshopModule>()));
        Assert.Empty(WorkshopModuleRegistrar.ResolveLiveModules(null, new ModOptions(new ModOptionsData())));
    }

    /// <summary>
    /// Enablement rides in <see cref="ModOptions"/>, which is what the host publishes to every client
    /// and what the configuration digest covers — so a client cannot opt itself in or out.
    /// </summary>
    [Fact]
    public void DisablingAModule_ChangesTheConfigurationDigest()
    {
        string enabled = ModConfigSnapshotCodec.ComputeSha256(
            ModConfigSnapshot.CurrentProtocolVersion, "session", 1,
            new ModOptions(new ModOptionsData()), birthAndDeathEnabled: true);
        string disabled = ModConfigSnapshotCodec.ComputeSha256(
            ModConfigSnapshot.CurrentProtocolVersion, "session", 1,
            OptionsWith(enabled: false), birthAndDeathEnabled: true);

        Assert.NotEqual(enabled, disabled);
    }

    /// <summary>Two operator files that differ only in key order must not look like a config conflict.</summary>
    [Fact]
    public void DisabledModuleOrder_DoesNotChangeTheConfigurationDigest()
    {
        var first = new ModOptions(new ModOptionsData
        {
            WorkshopModules = new Dictionary<string, bool>
            {
                ["Bannerlord.Diplomacy"] = false,
                ["ImprovedGarrisons"] = false,
            },
        });
        var second = new ModOptions(new ModOptionsData
        {
            WorkshopModules = new Dictionary<string, bool>
            {
                ["ImprovedGarrisons"] = false,
                ["Bannerlord.Diplomacy"] = false,
            },
        });

        Assert.Equal(
            ModConfigSnapshotCodec.ComputeSha256(1, "session", 1, first, birthAndDeathEnabled: true),
            ModConfigSnapshotCodec.ComputeSha256(1, "session", 1, second, birthAndDeathEnabled: true));
    }

    [Fact]
    public void EnabledEntries_DoNotAppearInTheDenyList()
    {
        var options = new ModOptions(new ModOptionsData
        {
            WorkshopModules = new Dictionary<string, bool>
            {
                ["Bannerlord.Diplomacy"] = true,
                ["ImprovedGarrisons"] = false,
            },
        });

        Assert.Equal(new[] { "ImprovedGarrisons" }, options.DisabledWorkshopModules);
        Assert.True(options.IsWorkshopModuleEnabled("Bannerlord.Diplomacy"));
        Assert.False(options.IsWorkshopModuleEnabled("ImprovedGarrisons"));
        Assert.False(options.IsWorkshopModuleEnabled("improvedgarrisons"));
    }
}
