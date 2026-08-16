using GameInterface.AutoSync;
using GameInterface;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

/// <summary>
/// Pins the shape every Workshop module declaration has to satisfy. These are the invariants a new
/// mod's author gets for free: an unusable fingerprint cannot be constructed at all, and identity is
/// content-addressed rather than trusting a version string a Workshop update can reuse.
/// </summary>
public sealed class WorkshopModuleContractTests
{
    private sealed class StubModule : IWorkshopModule
    {
        public string ModuleId => "Stub.Module";
        public ulong WorkshopId => 1234567890UL;
        public ModuleFingerprint Fingerprint =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", new string('a', 64));
        public string PatchCategory => "CoopWorkshopStubPatches";
        public string ResolveInstalledSha256() => null;
        public void RegisterSync(AutoSyncRegistry registry) { }
    }

    [Fact]
    public void Fingerprint_RejectsAnInvalidSha256()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", "not-a-hash"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fingerprint_RejectsAMissingAssemblyName(string assemblyName)
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleFingerprint(assemblyName, "1.0.0.0", new string('a', 64)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fingerprint_RejectsAMissingAssemblyVersion(string assemblyVersion)
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleFingerprint("Stub.Assembly", assemblyVersion, new string('a', 64)));
    }

    /// <summary>
    /// A 64-character string that merely looks like a hash is the failure mode that matters: it would
    /// sail through a length check and then never match anything, silently disabling the module.
    /// </summary>
    [Fact]
    public void Fingerprint_RejectsSixtyFourNonHexCharacters()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleFingerprint("Stub.Assembly", "1.0.0.0", new string('z', 64)));
    }

    /// <summary>Hex case is a formatting choice of whoever measured the file, not an identity.</summary>
    [Fact]
    public void Fingerprint_MatchesRegardlessOfHexCase()
    {
        var fingerprint = new ModuleFingerprint("Stub.Assembly", "1.0.0.0", new string('a', 64));

        Assert.True(fingerprint.Matches(new string('A', 64)));
        Assert.False(fingerprint.Matches(new string('b', 64)));
        Assert.False(fingerprint.Matches(null));
    }

    [Fact]
    public void Module_ExposesItsIdentityAndPatchCategory()
    {
        IWorkshopModule module = new StubModule();

        Assert.Equal("Stub.Module", module.ModuleId);
        Assert.Equal(1234567890UL, module.WorkshopId);
        Assert.Equal("CoopWorkshopStubPatches", module.PatchCategory);
        Assert.Equal("Stub.Assembly", module.Fingerprint.AssemblyName);
    }

    [Fact]
    public void GameInterfaceModule_DeclaresEveryCampaignGameplayModule()
    {
        var field = typeof(GameInterfaceModule).GetField(
            "DeclaredWorkshopModules",
            BindingFlags.Static | BindingFlags.NonPublic);
        var modules = Assert.IsType<IWorkshopModule[]>(field?.GetValue(null));
        var expected = new[]
        {
            "ImprovedGarrisons",
            "Fourberie",
            "Bannerlord.Diplomacy",
            "PlayerSettlement",
            "RebellionsAndDemographics",
        };

        Assert.Equal(expected, modules.Select(module => module.ModuleId));
        var catalog = new FriendEditionWorkshopModuleCatalog();
        Assert.All(modules, module =>
        {
            Assert.True(catalog.TryGet(module.ModuleId, out var entry));
            Assert.Equal(entry.WorkshopId, module.WorkshopId.ToString());
        });
    }
}
