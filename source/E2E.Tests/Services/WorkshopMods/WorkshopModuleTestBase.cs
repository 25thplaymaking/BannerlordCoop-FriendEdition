using E2E.Tests.Environment;
using GameInterface.AutoSync;
using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods;

/// <summary>
/// The gates every Workshop module must pass before it may be enabled on a live server. Deriving a
/// class and supplying the module is the whole cost of covering a new mod.
///
/// <para>
/// The gates are written against an environment where the mod is NOT installed, because that is the
/// state CI and most developer machines are in, and because it is the state that historically broke
/// everything: one absent mod's adapter patches resolving no targets threw out of Harmony and
/// aborted every remaining Coop patch, AutoSync included. Every gate below is therefore a claim
/// about what a module does when it is absent, disabled, or mis-declared — the three ways a
/// declaration can be wrong without anyone noticing until a session desyncs.
/// </para>
/// <para>
/// The fourth kind of gate — a client intent round-trip — is added in the follow-on plan, with the
/// first routed action. It cannot be written before there is an action to route.
/// </para>
/// </summary>
public abstract class WorkshopModuleTestBase : IDisposable
{
    private readonly Lazy<E2ETestEnvironment> testEnvironment;

    /// <summary>
    /// A live server and two clients, each with Coop's patches applied. Built on first use rather
    /// than in the constructor: most gates below are pure and would otherwise pay for a full
    /// three-instance environment per gate, per module, on every CI run.
    /// </summary>
    protected E2ETestEnvironment TestEnvironment => testEnvironment.Value;

    /// <summary>The declaration under test. One line in the derived class.</summary>
    protected abstract IWorkshopModule Module { get; }

    protected WorkshopModuleTestBase(ITestOutputHelper output)
        => testEnvironment = new Lazy<E2ETestEnvironment>(() => new E2ETestEnvironment(output));

    public void Dispose()
    {
        if (testEnvironment.IsValueCreated) testEnvironment.Value.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The harness has no Workshop mods installed; constructing the environment already ran
    /// GameInterface.PatchAll() on the server and on every client. Reaching here at all is the
    /// assertion — this is the regression that the whole Workshop integration exists to prevent.
    /// </summary>
    [Fact]
    public void Absent_LoadsWithoutThrowing()
    {
        Assert.NotNull(TestEnvironment.Server);
        Assert.NotEmpty(TestEnvironment.Clients);
    }

    /// <summary>
    /// The decision that gates patch-category registration must say "no" here. If it ever says
    /// "yes" on a machine without the mod, Harmony applies a category whose patch classes resolve no
    /// targets and takes down every remaining Coop patch with it.
    /// </summary>
    [Fact]
    public void Absent_IsNotInstalledAndSoContributesNoPatchCategory()
    {
        Assert.Empty(WorkshopModuleRegistrar.ResolveInstalledModules(new[] { Module }));
        Assert.Null(Module.ResolveInstalledSha256());
    }

    /// <summary>
    /// The operator's kill switch has to reach THIS module. The failure it catches is a module whose
    /// ModuleId does not match the key an operator would write, which leaves the switch inert with
    /// nothing anywhere reporting a problem.
    /// </summary>
    [Fact]
    public void Disabled_IsNotLive()
    {
        var options = new ModOptions(new ModOptionsData
        {
            WorkshopModules = new Dictionary<string, bool> { [Module.ModuleId] = false },
        });

        Assert.False(options.IsWorkshopModuleEnabled(Module.ModuleId));
        Assert.Empty(WorkshopModuleRegistrar.ResolveLiveModules(new[] { Module }, options));
        Assert.NotEqual(
            Digest(new ModOptions(new ModOptionsData())),
            Digest(options));
    }

    /// <summary>Silence in the operator's file means the module behaves as it did before the key existed.</summary>
    [Fact]
    public void NotMentionedInConfig_IsEnabled()
    {
        Assert.True(new ModOptions(new ModOptionsData()).IsWorkshopModuleEnabled(Module.ModuleId));
    }

    [Fact]
    public void Fingerprint_IsPinnedToExactBytes()
    {
        Assert.Equal(64, Module.Fingerprint.Sha256.Length);
        Assert.NotEqual(new string('0', 64), Module.Fingerprint.Sha256);
        Assert.False(string.IsNullOrWhiteSpace(Module.Fingerprint.AssemblyName));
        Assert.False(string.IsNullOrWhiteSpace(Module.Fingerprint.AssemblyVersion));
    }

    /// <summary>
    /// The declaration, the packaged component set and the operator's config key all have to name
    /// one thing. A ModuleId that is not in the Friend Edition catalog is a module that can never be
    /// shipped, verified or configured, and nothing else would ever say so.
    /// </summary>
    [Fact]
    public void ModuleId_IsReconciledWithTheFriendEditionCatalog()
    {
        IWorkshopModuleCatalog catalog = new FriendEditionWorkshopModuleCatalog();

        Assert.True(
            catalog.TryGet(Module.ModuleId, out var expectation),
            $"{Module.ModuleId} is not a Friend Edition component.");
        Assert.Equal(Module.WorkshopId.ToString(), expectation.WorkshopId);
    }

    /// <summary>
    /// Patch categories are shared vocabulary between the module, its adapter patch classes and the
    /// registrar. A literal typed into one of the three is a category that is registered but never
    /// applied, or applied but never registered.
    /// </summary>
    [Fact]
    public void PatchCategory_IsOneOfTheDeclaredWorkshopCategories()
    {
        string[] declared = typeof(WorkshopPatchCategories)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue())
            .ToArray();

        Assert.Contains(Module.PatchCategory, declared);
    }

    /// <summary>
    /// RegisterSync runs against types resolved out of the mod assembly. Here there is no mod
    /// assembly, so it must resolve nothing and register nothing rather than throwing — AutoSync
    /// registration happens while the container is built, and an exception there takes the session
    /// down before it starts.
    /// </summary>
    [Fact]
    public void RegisterSync_WithTheModuleAbsent_RegistersNothingAndDoesNotThrow()
    {
        var registry = new AutoSyncRegistry();

        Module.RegisterSync(registry);

        Assert.Empty(registry.Registrations);
    }

    private static string Digest(ModOptions options) => ModConfigSnapshotCodec.ComputeSha256(
        ModConfigSnapshot.CurrentProtocolVersion,
        "0123456789abcdef0123456789abcdef",
        revision: 1,
        options,
        birthAndDeathEnabled: true);
}
