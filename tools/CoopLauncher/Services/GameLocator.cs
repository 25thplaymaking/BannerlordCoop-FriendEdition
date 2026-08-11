using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoopLauncher.Services;

/// <summary>Finds the Bannerlord install so a friend never has to type a path.</summary>
public static class GameLocator
{
    private const string BannerlordAppId = "261550";
    private const string ClientBinRelative = @"bin\Win64_Shipping_Client";
    private const string ExeName = "Bannerlord.exe";

    /// <summary>
    /// Resolves the client executable. Preference order: an explicit config path, then every Steam
    /// library on the machine. Returns null if Bannerlord can't be found (the UI then asks the user
    /// to set <c>gamePath</c>).
    /// </summary>
    public static string? FindBannerlordExe(string configuredGamePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredGamePath))
        {
            var exe = Path.Combine(configuredGamePath, ClientBinRelative, ExeName);
            if (File.Exists(exe)) return exe;
        }

        foreach (var lib in EnumerateSteamLibraries())
        {
            var root = Path.Combine(lib, "steamapps", "common", "Mount & Blade II Bannerlord");
            var exe = Path.Combine(root, ClientBinRelative, ExeName);
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    /// <summary>The <c>Modules</c> folder of the resolved install, for the updater to write into.</summary>
    public static string? FindModulesDir(string bannerlordExePath)
    {
        // exe is …\<root>\bin\Win64_Shipping_Client\Bannerlord.exe → up 3 to <root>.
        var root = Directory.GetParent(bannerlordExePath)?.Parent?.Parent?.FullName;
        if (root == null) return null;
        var modules = Path.Combine(root, "Modules");
        return Directory.Exists(modules) ? modules : null;
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var steamRoot = GetSteamRoot();
        if (steamRoot == null) yield break;

        // The main install is always a library.
        yield return steamRoot;

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        // Each library block lists a "path" and the apps it holds; only yield libraries that
        // actually contain Bannerlord so we don't probe unrelated drives.
        foreach (Match block in Regex.Matches(text, "\\{(.*?)\\}", RegexOptions.Singleline))
        {
            var body = block.Groups[1].Value;
            var pathMatch = Regex.Match(body, "\"path\"\\s*\"(.*?)\"");
            if (!pathMatch.Success) continue;
            if (!body.Contains($"\"{BannerlordAppId}\"")) continue;

            yield return pathMatch.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    private static string? GetSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && !string.IsNullOrWhiteSpace(p))
                return p.Replace('/', '\\');
        }
        catch { /* registry unavailable — fall through */ }

        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                 })
        {
            if (Directory.Exists(guess)) return guess;
        }
        return null;
    }
}
