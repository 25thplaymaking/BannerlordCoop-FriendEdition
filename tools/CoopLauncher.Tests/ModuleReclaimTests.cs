using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using CoopLauncher.Services;

namespace CoopLauncher.Tests;

/// <summary>
/// Covers the scan that finds module folders the co-op loadout no longer uses.
/// </summary>
/// <remarks>
/// This offers to delete gigabytes of a player's disk, so the tests that matter are the ones proving
/// it will not offer to delete the wrong thing: the game, the loadout, or anything a receipt or the
/// launch token still claims.
/// </remarks>
public class ModuleReclaimTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "reclaim-" + Guid.NewGuid().ToString("N"));

    private string Modules => Path.Combine(_root, "Modules");

    public ModuleReclaimTests() => Directory.CreateDirectory(Modules);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Module(string name, int bytes = 16)
    {
        string directory = Path.Combine(Modules, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
    }

    private static LauncherConfig Config(string token) => new() { ModuleToken = token };

    private const string Token = "_MODULES_*Native*Coop*Europe1100*_MODULES_";

    [Fact]
    public void BaseGameModulesAreNeverOffered()
    {
        Module("Native");
        Module("SandBox");
        Module("StoryMode");
        Module("Multiplayer");

        Assert.Empty(ModuleReclaim.Scan(Modules, Config(Token)));
    }

    [Fact]
    public void ModulesInTheLaunchTokenAreNeverOffered()
    {
        Module("Europe1100");

        Assert.Empty(ModuleReclaim.Scan(Modules, Config(Token)));
    }

    [Fact]
    public void ModulesNamedByAFeedReceiptAreNeverOffered()
    {
        Module("Bannerlord.Harmony");
        File.WriteAllLines(Path.Combine(Modules, "coop-suite-modules.txt"), new[] { "Bannerlord.Harmony" });

        Assert.Empty(ModuleReclaim.Scan(Modules, Config(Token)));
    }

    [Fact]
    public void AnUnusedModuleIsOfferedWithItsSize()
    {
        Module("LeftoverConversion", bytes: 2048);

        UnusedModule found = Assert.Single(ModuleReclaim.Scan(Modules, Config(Token)));

        Assert.Equal("LeftoverConversion", found.Name);
        Assert.Equal(2048, found.Bytes);
    }

    [Fact]
    public void AnUnreadableReceiptDoesNotMakeTheLoadoutLookUnused()
    {
        // The dangerous failure: if a locked or corrupt receipt read as "nothing installed", every
        // module it names would be offered for deletion. The scan must fall back to conservative.
        Module("Bannerlord.Harmony");
        string receipt = Path.Combine(Modules, "coop-suite-modules.txt");
        File.WriteAllLines(receipt, new[] { "Bannerlord.Harmony" });

        using var held = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.None);

        // Held open for exclusive access, so the read throws; the module must still not be offered
        // simply because the launcher could not confirm it.
        var offered = ModuleReclaim.Scan(Modules, Config(Token)).Select(module => module.Name).ToList();

        Assert.DoesNotContain("Bannerlord.Harmony", offered);
    }

    [Fact]
    public void ReceiptedLeftoversAreMarkedAsInstalledByTheLauncher()
    {
        // The player is told which folders are provably ours, because those are the safe ones — the
        // rest are mods they installed themselves.
        Module("OurOldModule");
        Module("TheirOwnMod");
        File.WriteAllLines(Path.Combine(Modules, "coop-suite-modules.txt"), new[] { "Europe1100" });

        var found = ModuleReclaim.Scan(Modules, Config(Token)).ToDictionary(module => module.Name);

        Assert.False(found["OurOldModule"].InstalledByLauncher);
        Assert.False(found["TheirOwnMod"].InstalledByLauncher);
    }

    [Fact]
    public void HiddenWorkspacesAreIgnored()
    {
        // Interrupted-update workspaces are ModUpdater's to sweep, not the player's to be asked about.
        Module(".coop-update-abc123");

        Assert.Empty(ModuleReclaim.Scan(Modules, Config(Token)));
    }

    [Fact]
    public void RemovingDeletesOnlyWhatWasPassedIn()
    {
        Module("Doomed");
        Module("Spared");

        var doomed = ModuleReclaim.Scan(Modules, Config(Token))
            .Where(module => module.Name == "Doomed")
            .ToList();
        var (removed, freed, failed) = ModuleReclaim.Remove(doomed);

        Assert.Equal(1, removed);
        Assert.True(freed > 0);
        Assert.Empty(failed);
        Assert.False(Directory.Exists(Path.Combine(Modules, "Doomed")));
        Assert.True(Directory.Exists(Path.Combine(Modules, "Spared")));
    }

    [Fact]
    public void AMissingModulesDirectoryScansToNothing()
    {
        Assert.Empty(ModuleReclaim.Scan(Path.Combine(_root, "no-such-dir"), Config(Token)));
    }

    [Fact]
    public void TokenParsingSkipsTheDelimiter()
    {
        var names = ModuleReclaim.ModulesFromToken(Token).ToList();

        Assert.Equal(new[] { "Native", "Coop", "Europe1100" }, names);
    }
}
