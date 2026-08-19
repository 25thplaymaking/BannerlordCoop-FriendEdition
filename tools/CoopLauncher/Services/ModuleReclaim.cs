using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoopLauncher.Services;

/// <summary>One module directory that nothing in the co-op loadout uses.</summary>
public sealed record UnusedModule(string Name, string Path, long Bytes, bool InstalledByLauncher);

/// <summary>
/// Finds module directories nothing in the co-op loadout uses, so a player can get the disk back.
/// </summary>
/// <remarks>
/// Installing used to record only a version string, never which modules a feed had installed, so a
/// module dropped from a later suite stayed on disk for good. <see cref="ModUpdater"/> now keeps a
/// receipt and prunes retired modules automatically — but only for installs made after that change.
/// Anyone updating from an older launcher already has the accumulation, sometimes several conversions
/// worth, and no receipt that would let it be removed automatically.
/// <para>
/// This finds them by elimination rather than by provenance: anything that is not a base-game module,
/// not named in a feed receipt, and not in the configured module token is not part of what the group
/// plays. That set is honest but not authoritative — it also catches a player's own single-player
/// mods, which the launcher never installed and has no business deleting on its own. So the scan
/// reports; deleting is the player's explicit choice, and
/// <see cref="UnusedModule.InstalledByLauncher"/> marks the ones a receipt proves are ours.
/// </para>
/// </remarks>
public static class ModuleReclaim
{
    /// <summary>Shipped with the game. Never offered for removal, whatever else says otherwise.</summary>
    private static readonly HashSet<string> BaseGameModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle", "Multiplayer", "BirthAndDeath",
    };

    /// <summary>The co-op client itself, which is not in the suite receipt.</summary>
    private const string CoopModule = "Coop";

    public static IReadOnlyList<UnusedModule> Scan(string modulesDir, LauncherConfig config)
    {
        var results = new List<UnusedModule>();
        if (string.IsNullOrWhiteSpace(modulesDir) || !Directory.Exists(modulesDir)) return results;

        var inUse = new HashSet<string>(BaseGameModules, StringComparer.OrdinalIgnoreCase) { CoopModule };
        foreach (string name in ModulesFromToken(config?.ModuleToken)) inUse.Add(name);

        // A receipt names what a feed installed. Those are in use AND provably ours, which is what
        // separates "we put this here" from "the player did".
        var launcherOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string receipt in ReceiptPaths(modulesDir))
        {
            if (!TryReadReceipt(receipt, out IReadOnlyList<string> names))
            {
                // A receipt that exists but will not read leaves the launcher unable to tell the
                // loadout from the leftovers. Offering anything at that point could delete the mods
                // the group plays with, so the scan reports nothing at all.
                Log.Write("Skipping reclaim scan: a module receipt could not be read.");
                return results;
            }

            foreach (string name in names)
            {
                inUse.Add(name);
                launcherOwned.Add(name);
            }
        }

        foreach (string directory in SafeEnumerateDirectories(modulesDir))
        {
            string name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            if (inUse.Contains(name)) continue;

            results.Add(new UnusedModule(name, directory, DirectorySize(directory), launcherOwned.Contains(name)));
        }

        return results.OrderByDescending(module => module.Bytes).ToList();
    }

    /// <summary>Removes the given modules, returning how many went and how much came back.</summary>
    public static (int Removed, long BytesFreed, List<string> Failed) Remove(IEnumerable<UnusedModule> modules)
    {
        int removed = 0;
        long freed = 0;
        var failed = new List<string>();

        foreach (UnusedModule module in modules)
        {
            try
            {
                if (!Directory.Exists(module.Path)) continue;
                Directory.Delete(module.Path, recursive: true);
                removed++;
                freed += module.Bytes;
                Log.Write($"Reclaimed unused module {module.Name} ({module.Bytes / 1_048_576.0:0} MB)");
            }
            catch (Exception ex)
            {
                failed.Add(module.Name);
                Log.Write($"Could not remove {module.Name}: {ex.Message}");
            }
        }

        return (removed, freed, failed);
    }

    /// <summary>Module ids from the launch token, which is the set the game is told to load.</summary>
    internal static IEnumerable<string> ModulesFromToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) yield break;

        foreach (string part in token.Split('*', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.Trim();
            if (name.Length == 0 || name == "_MODULES_") continue;
            yield return name;
        }
    }

    private static IEnumerable<string> ReceiptPaths(string modulesDir)
    {
        yield return Path.Combine(modulesDir, "coop-suite-modules.txt");
        yield return Path.Combine(modulesDir, "Coop", "installed-modules.txt");
    }

    /// <summary>
    /// Reads a receipt. False means one exists but could not be read, which the caller must treat as
    /// "cannot tell what is in use" rather than "nothing is in use".
    /// </summary>
    private static bool TryReadReceipt(string path, out IReadOnlyList<string> names)
    {
        names = Array.Empty<string>();
        try
        {
            if (!File.Exists(path)) return true;
            names = File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read module receipt {path}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string modulesDir)
    {
        try { return Directory.EnumerateDirectories(modulesDir).ToList(); }
        catch (Exception ex)
        {
            Log.Write($"Could not enumerate modules: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    internal static long DirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* a file that vanished mid-scan just does not count */ }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not size {path}: {ex.Message}");
        }
        return total;
    }
}
