using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CoopLauncher.Services;

/// <summary>Finds the Bannerlord install so a friend never has to type a path.</summary>
public static class GameLocator
{
    private const string BannerlordAppId = "261550";
    private const string ClientBinRelative = @"bin\Win64_Shipping_Client";
    private const string ExeName = "Bannerlord.exe";

    public sealed record GameInstallation(
        string RootPath,
        string ExePath,
        string Version,
        string? SteamBuildId)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(SteamBuildId)
            ? $"Bannerlord {Version} — {RootPath}"
            : $"Bannerlord {Version} (Steam build {SteamBuildId}) — {RootPath}";
    }

    /// <summary>
    /// Resolves the client executable. Preference order: an explicit config path, then every Steam
    /// library on the machine. Returns null if Bannerlord can't be found (the UI then asks the user
    /// to set <c>gamePath</c>).
    /// </summary>
    public static string? FindBannerlordExe(string configuredGamePath)
    {
        return FindInstallations(configuredGamePath).FirstOrDefault()?.ExePath;
    }

    /// <summary>
    /// Returns every valid local Bannerlord root, with the configured root first. Version comes
    /// from Native/SubModule.xml (TaleWorlds' module declaration), not an inferred file timestamp.
    /// </summary>
    public static IReadOnlyList<GameInstallation> FindInstallations(string configuredGamePath)
    {
        var roots = new List<(string Root, string? Manifest)>();
        if (!string.IsNullOrWhiteSpace(configuredGamePath))
            roots.Add((configuredGamePath, FindSteamManifestForRoot(configuredGamePath)));

        foreach (string library in EnumerateSteamLibraries())
        {
            string root = Path.Combine(library, "steamapps", "common", "Mount & Blade II Bannerlord");
            roots.Add((root, Path.Combine(library, "steamapps", $"appmanifest_{BannerlordAppId}.acf")));
        }

        return roots
            .GroupBy(item => NormalizePath(item.Root), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => TryCreateInstallation(item.Root, item.Manifest))
            .Where(item => item is not null)
            .Cast<GameInstallation>()
            .ToArray();
    }

    public static bool VersionsMatch(string installed, string required) =>
        NormalizeVersion(installed).Equals(NormalizeVersion(required), StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeVersion(string version) =>
        (version ?? string.Empty).Trim().TrimStart('v', 'V');

    private static GameInstallation? TryCreateInstallation(string root, string? manifestPath)
    {
        try
        {
            string fullRoot = NormalizePath(root);
            string exe = Path.Combine(fullRoot, ClientBinRelative, ExeName);
            if (!File.Exists(exe)) return null;

            string version = ReadGameVersion(fullRoot) ?? "unknown";
            return new GameInstallation(fullRoot, exe, version, ReadSteamBuildId(manifestPath));
        }
        catch
        {
            return null;
        }
    }

    internal static string? ReadGameVersion(string root)
    {
        string subModule = Path.Combine(root, "Modules", "Native", "SubModule.xml");
        try
        {
            if (!File.Exists(subModule)) return null;
            XDocument document = XDocument.Load(subModule, LoadOptions.None);
            string? value = document.Descendants("Version")
                .Select(element => (string?)element.Attribute("value"))
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            return string.IsNullOrWhiteSpace(value) ? null : NormalizeVersion(value);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ReadSteamBuildId(string? manifestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
            Match match = Regex.Match(File.ReadAllText(manifestPath), "\\\"buildid\\\"\\s*\\\"(?<id>\\d+)\\\"");
            return match.Success ? match.Groups["id"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? FindSteamManifestForRoot(string root)
    {
        try
        {
            var common = Directory.GetParent(NormalizePath(root));
            var steamApps = common?.Parent;
            if (!string.Equals(common?.Name, "common", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(steamApps?.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.Combine(steamApps!.FullName, $"appmanifest_{BannerlordAppId}.acf");
        }
        catch
        {
            return null;
        }
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
