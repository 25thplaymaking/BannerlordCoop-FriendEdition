using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CoopLauncher.Services;

public enum UpdateOutcome { Disabled, UpToDate, Updated, Offline, Failed }

public readonly record struct UpdateResult(UpdateOutcome Outcome, string Message);

/// <summary>
/// Keeps a client's install in step with the group's published feeds, in two tiers:
/// <list type="bullet">
/// <item>the <b>mod suite</b> — every non-base module a friend needs (frameworks + workshop mods),
/// byte-exact so the join handshake matches. Large (~1 GB) but changes rarely.</item>
/// <item>the <b>co-op client</b> — Coop's own assemblies. Small, changes every build.</item>
/// </list>
/// Each tier is a manifest + signed zip whose root entries are module folders. Updates are staged and
/// exact-replaced under <c>Modules\</c>; any install failure restores the previous module directories.
/// An unreachable feed still runs what's installed, but a reached-and-invalid required update fails closed.
/// </summary>
public sealed class ModUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly LauncherConfig _config;
    public ModUpdater(LauncherConfig config) => _config = config;

    /// <param name="progress">(fraction 0..1 or -1 for indeterminate, status line).</param>
    public async Task<UpdateResult> RunAsync(string modulesDir, Action<double, string> progress)
    {
        // Suite first: the client assemblies mean nothing if the modules they patch aren't present.
        var suite = await InstallTierAsync(
            _config.SuiteManifestUrl, modulesDir,
            versionFile: Path.Combine(modulesDir, "coop-suite-version.txt"),
            label: "mod suite", progress);
        if (suite.Outcome == UpdateOutcome.Failed)
            return suite;

        var client = await InstallTierAsync(
            _config.UpdateManifestUrl, modulesDir,
            versionFile: Path.Combine(modulesDir, "Coop", "installed-version.txt"),
            label: "co-op client", progress);

        // Surface the more interesting of the two outcomes to the UI.
        return Combine(suite, client);
    }

    private async Task<UpdateResult> InstallTierAsync(
        string manifestUrl, string modulesDir, string versionFile, string label,
        Action<double, string> progress)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return new(UpdateOutcome.Disabled, $"{label}: updates off");

        UpdateManifest? manifest;
        try
        {
            progress(-1, $"Checking {label}…");
            manifest = JsonSerializer.Deserialize<UpdateManifest>(await Http.GetStringAsync(manifestUrl));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return new(UpdateOutcome.Failed,
                $"{label} feed returned HTTP {(int)ex.StatusCode} — update required");
        }
        catch
        {
            return new(UpdateOutcome.Offline, $"Couldn't reach the {label} feed — using installed");
        }

        if (manifest is null || !IsManifestValid(manifest))
            return new(UpdateOutcome.Failed, $"{label} feed was malformed — using installed");

        var installed = ReadText(versionFile);
        if (!IsNewer(manifest.Version, installed))
            return new(UpdateOutcome.UpToDate, $"{label}: up to date (build {installed ?? "—"})");

        string tempZip = Path.Combine(Path.GetTempPath(), $"coop-update-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(manifest.ClientZipUrl, tempZip, label, progress);

            progress(-1, $"Verifying {label}…");
            var actual = await Sha256HexAsync(tempZip);
            if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(UpdateOutcome.Failed, $"{label} failed integrity check — installed kept");

            progress(-1, $"Installing {label}…");
            InstallExact(tempZip, modulesDir, versionFile, manifest.Version);

            var note = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $" — {manifest.Notes}";
            return new(UpdateOutcome.Updated, $"{label} updated to {manifest.Version}{note}");
        }
        catch
        {
            return new(UpdateOutcome.Failed, $"{label} update failed — using installed");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* best-effort */ }
        }
    }

    internal static UpdateResult Combine(UpdateResult suite, UpdateResult client)
    {
        // Rank so the message the user sees reflects the most actionable state.
        static int Rank(UpdateOutcome o) => o switch
        {
            UpdateOutcome.Failed => 5,
            UpdateOutcome.Offline => 4,
            UpdateOutcome.Updated => 3,
            UpdateOutcome.UpToDate => 2,
            UpdateOutcome.Disabled => 1,
            _ => 0,
        };
        var winner = Rank(suite.Outcome) >= Rank(client.Outcome) ? suite : client;
        if (winner.Outcome is UpdateOutcome.Failed or UpdateOutcome.Offline)
            return winner;

        // When both simply updated or are current, prefer a concise combined line.
        if (suite.Outcome == UpdateOutcome.Updated || client.Outcome == UpdateOutcome.Updated)
            return new(UpdateOutcome.Updated,
                (suite.Outcome == UpdateOutcome.Updated ? suite.Message + "; " : "") +
                (client.Outcome == UpdateOutcome.Updated ? client.Message : "co-op client current"));
        return winner;
    }

    private static async Task DownloadAsync(string url, string dest, string label, Action<double, string> progress)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[131072];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0)
                progress(read / (double)total, $"Downloading {label}… {read / 1_048_576.0:0} / {total / 1_048_576.0:0} MB");
            else
                progress(-1, $"Downloading {label}… {read / 1_048_576.0:0} MB");
        }
    }

    internal static bool IsManifestValid(UpdateManifest? manifest)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.ClientZipUrl))
            return false;

        string sha256 = manifest.Sha256?.Trim() ?? string.Empty;
        return sha256.Length == 64 && sha256.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Stage a zip under the destination volume, exact-replace each top-level module directory, and
    /// restore every previous directory if any move or version write fails.
    /// </summary>
    internal static void InstallExact(string zipPath, string modulesDir, string versionFile, string version)
    {
        string modulesFull = Path.GetFullPath(modulesDir);
        Directory.CreateDirectory(modulesFull);

        string workspace = Path.Combine(modulesFull, $".coop-update-{Guid.NewGuid():N}");
        string stageRoot = Path.Combine(workspace, "stage");
        string backupRoot = Path.Combine(workspace, "backup");
        var replacements = new List<(string Destination, string Backup, bool HadExisting)>();
        byte[]? previousVersion = null;
        bool versionExisted = false;
        bool committed = false;

        try
        {
            Directory.CreateDirectory(stageRoot);
            Directory.CreateDirectory(backupRoot);
            ExtractToStage(zipPath, stageRoot);

            if (Directory.GetFiles(stageRoot, "*", SearchOption.TopDirectoryOnly).Length > 0)
                throw new InvalidDataException("Update zip may contain only top-level module directories");

            string[] stagedModules = Directory.GetDirectories(stageRoot);
            if (stagedModules.Length == 0 || Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories).Length == 0)
                throw new InvalidDataException("Update zip contains no module files");

            string versionFull = Path.GetFullPath(versionFile);
            versionExisted = File.Exists(versionFull);
            if (versionExisted) previousVersion = File.ReadAllBytes(versionFull);

            foreach (string stagedModule in stagedModules.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string moduleName = Path.GetFileName(stagedModule);
                string destination = Path.Combine(modulesFull, moduleName);
                string backup = Path.Combine(backupRoot, moduleName);
                if (File.Exists(destination))
                    throw new InvalidDataException($"Module destination is a file: {moduleName}");

                bool hadExisting = Directory.Exists(destination);
                replacements.Add((destination, backup, hadExisting));
                if (hadExisting) Directory.Move(destination, backup);
                Directory.Move(stagedModule, destination);
            }

            WriteText(versionFull, version);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                for (int i = replacements.Count - 1; i >= 0; i--)
                {
                    var replacement = replacements[i];
                    if (Directory.Exists(replacement.Destination))
                        Directory.Delete(replacement.Destination, recursive: true);
                    if (replacement.HadExisting && Directory.Exists(replacement.Backup))
                        Directory.Move(replacement.Backup, replacement.Destination);
                }

                string versionFull = Path.GetFullPath(versionFile);
                bool versionInsideReplacedModule = replacements.Any(replacement =>
                    IsWithin(versionFull, replacement.Destination));
                if (!versionInsideReplacedModule)
                {
                    if (versionExisted && previousVersion != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(versionFull)!);
                        File.WriteAllBytes(versionFull, previousVersion);
                    }
                    else if (File.Exists(versionFull))
                    {
                        File.Delete(versionFull);
                    }
                }
            }

            TryDeleteDirectory(workspace);
        }
    }

    private static void ExtractToStage(string zipPath, string stageRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        string rootWithSeparator = Path.GetFullPath(stageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(stageRoot, entry.FullName));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update zip entry escapes the staging root: {entry.FullName}");

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        string directoryWithSeparator = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A successful install must not be reported as failed only because antivirus held a staging file.
        }
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static void WriteText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value.Trim());
    }

    /// <summary>Dotted-numeric compare ("2026.8.11.2" &gt; "2026.8.11.1"); any unparsable diff = newer.</summary>
    private static bool IsNewer(string remote, string? local)
    {
        if (string.IsNullOrWhiteSpace(local)) return true;
        if (remote.Trim() == local.Trim()) return false;

        var r = ParseParts(remote);
        var l = ParseParts(local);
        if (r is null || l is null) return remote.Trim() != local.Trim();

        for (int i = 0; i < Math.Max(r.Length, l.Length); i++)
        {
            int rv = i < r.Length ? r[i] : 0;
            int lv = i < l.Length ? l[i] : 0;
            if (rv != lv) return rv > lv;
        }
        return false;
    }

    private static int[]? ParseParts(string v)
    {
        var segs = v.Trim().Split('.');
        var parts = new int[segs.Length];
        for (int i = 0; i < segs.Length; i++)
            if (!int.TryParse(segs[i], out parts[i])) return null;
        return parts;
    }

    private static async Task<string> Sha256HexAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
