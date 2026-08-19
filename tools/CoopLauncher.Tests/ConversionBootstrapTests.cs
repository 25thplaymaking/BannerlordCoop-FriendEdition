using CoopLauncher.Services;
using System.IO;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ConversionBootstrapTests : IDisposable
{
    private readonly string _root;
    private readonly string _modules;
    private readonly string _workshop;

    public ConversionBootstrapTests()
    {
        // The bootstrap resolves the Workshop content root by walking up from Modules, so the
        // fixture has to reproduce the real Steam library shape exactly.
        _root = Path.Combine(Path.GetTempPath(), $"coop-conversion-{Guid.NewGuid():N}");
        _modules = Path.Combine(_root, "steamapps", "common", "Mount & Blade II Bannerlord", "Modules");
        _workshop = Path.Combine(_root, "steamapps", "workshop", "content", "261550");
        Directory.CreateDirectory(_modules);
        Directory.CreateDirectory(_workshop);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ConversionBootstrap.ConversionModule Module(string moduleId) =>
        ConversionBootstrap.Conversion.Single(module => module.ModuleId == moduleId);

    /// <summary>Stages a Workshop item that looks like the raw, unpatched subscription.</summary>
    private string StageWorkshopItem(string moduleId, string extraFile = "ModuleData/data.xml")
    {
        ConversionBootstrap.ConversionModule module = Module(moduleId);
        string item = Path.Combine(_workshop, module.WorkshopId);
        Directory.CreateDirectory(item);

        // The raw Workshop manifest declares the same Id and Version as the patched one; only the
        // dependency pins differ. That is exactly the case a version check would miss.
        File.WriteAllText(Path.Combine(item, "SubModule.xml"),
            $"<Module><Id value=\"{moduleId}\"/><Version value=\"{module.Version}\"/>" +
            "<DependedModules><DependedModule Id=\"Native\" DependentVersion=\"v1.4.7.3\"/></DependedModules></Module>");

        string payload = Path.Combine(item, extraFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.WriteAllText(payload, "workshop payload");
        return item;
    }

    [Fact]
    public void ShaderCacheNeutralisation_MovesTheSackAsideAndIsIdempotent()
    {
        // Europe 1100's sack is incomplete for the deferred render path, so the engine compiles the
        // missing variants mid-frame as a garment comes into view. Moving it aside makes the engine
        // use the base game's complete sources instead.
        string moduleDir = Path.Combine(_modules, "Europe1100", "Shaders", "D3D11");
        Directory.CreateDirectory(moduleDir);
        string sack = Path.Combine(moduleDir, "compressed_shader_cache.sack");
        File.WriteAllText(sack, "shaders");

        Assert.True(ConversionBootstrap.NeutralizeShaderCache(Path.Combine(_modules, "Europe1100")));
        Assert.False(File.Exists(sack));
        Assert.True(File.Exists(sack + ".disabled"));

        // A second launch must not fail, and must not clobber the preserved copy.
        Assert.False(ConversionBootstrap.NeutralizeShaderCache(Path.Combine(_modules, "Europe1100")));
        Assert.Equal("shaders", File.ReadAllText(sack + ".disabled"));
    }

    [Fact]
    public void ShaderCacheNeutralisation_IsAQuietNoOpWhenTheModuleShipsNoShaders()
    {
        Directory.CreateDirectory(Path.Combine(_modules, "Europe1100Expanded"));

        Assert.False(ConversionBootstrap.NeutralizeShaderCache(
            Path.Combine(_modules, "Europe1100Expanded")));
    }

    [Fact]
    public void AMissingModulesFolderFailsWithoutThrowing()
    {
        ConversionResult result = new ConversionBootstrap().Ensure(Path.Combine(_root, "nope"));

        Assert.Equal(ConversionOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void AnUnsubscribedWorkshopItemIsReportedRatherThanInstalled()
    {
        ConversionResult result = new ConversionBootstrap().Ensure(_modules);

        Assert.Equal(ConversionOutcome.MissingSubscription, result.Outcome);
        Assert.Equal(
            ConversionBootstrap.Conversion.Select(m => m.WorkshopId),
            result.MissingWorkshopIds);
        // Every conversion module is Workshop-sourced now, so nothing installs at all.
        Assert.Empty(Directory.GetDirectories(_modules));
    }

    [Fact]
    public void ASubscribedItemIsCopiedAcrossAndItsManifestIsRepinned()
    {
        StageWorkshopItem("Europe1100");
        StageWorkshopItem("Europe1100Expanded");
        StageWorkshopItem("SnowballingKingdoms - EOE 1100");

        ConversionResult result = new ConversionBootstrap().Ensure(_modules);

        Assert.Equal(ConversionOutcome.Installed, result.Outcome);
        string installed = Path.Combine(_modules, "Europe1100");
        Assert.Equal("workshop payload", File.ReadAllText(Path.Combine(installed, "ModuleData", "data.xml")));

        // The re-pinned manifest replaced the Workshop one: v1.4.8 natives, not v1.4.7.3.
        string manifest = File.ReadAllText(Path.Combine(installed, "SubModule.xml"));
        Assert.Contains("DependedModule Id=\"Native\" DependentVersion=\"v1.4.8\"", manifest);
    }

    [Fact]
    public void ASecondRunChangesNothing()
    {
        StageWorkshopItem("Europe1100");
        StageWorkshopItem("Europe1100Expanded");
        StageWorkshopItem("SnowballingKingdoms - EOE 1100");
        new ConversionBootstrap().Ensure(_modules);

        ConversionResult second = new ConversionBootstrap().Ensure(_modules);

        Assert.Equal(ConversionOutcome.UpToDate, second.Outcome);
    }

    [Fact]
    public void ASteamUpdateThatChangesTheWorkshopCopyIsMirroredAndOrphansAreRemoved()
    {
        StageWorkshopItem("Europe1100");
        StageWorkshopItem("Europe1100Expanded");
        StageWorkshopItem("SnowballingKingdoms - EOE 1100");
        new ConversionBootstrap().Ensure(_modules);

        string item = Path.Combine(_workshop, Module("Europe1100").WorkshopId);
        File.WriteAllText(Path.Combine(item, "ModuleData", "data.xml"), "a longer payload published by a later Steam update");
        string orphan = Path.Combine(_modules, "Europe1100", "ModuleData", "orphan.xml");
        File.WriteAllText(orphan, "left over from an older Workshop build");

        ConversionResult result = new ConversionBootstrap().Ensure(_modules);

        Assert.Equal(ConversionOutcome.Installed, result.Outcome);
        Assert.Equal("a longer payload published by a later Steam update",
            File.ReadAllText(Path.Combine(_modules, "Europe1100", "ModuleData", "data.xml")));
        // These modules are uncatalogued, so the handshake never catches drift; the mirror must.
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void ARawWorkshopManifestIsTreatedAsStaleEvenThoughItsVersionMatches()
    {
        StageWorkshopItem("Europe1100");
        string installed = Path.Combine(_modules, "Europe1100");
        Directory.CreateDirectory(installed);
        File.Copy(
            Path.Combine(_workshop, Module("Europe1100").WorkshopId, "SubModule.xml"),
            Path.Combine(installed, "SubModule.xml"));

        new ConversionBootstrap().Ensure(_modules);

        string manifest = File.ReadAllText(Path.Combine(installed, "SubModule.xml"));
        Assert.DoesNotContain("DependentVersion=\"v1.4.7.3\"", manifest);
    }

    [Fact]
    public void TheConversionSetMatchesTheLauncherModuleToken()
    {
        // A module installed but never activated is invisible; one activated but never installed
        // stops Bannerlord at the module screen. The two lists have to agree.
        foreach (ConversionBootstrap.ConversionModule module in ConversionBootstrap.Conversion)
            Assert.Contains($"*{module.ModuleId}*", LauncherConfig.CurrentModuleToken);
    }
}
