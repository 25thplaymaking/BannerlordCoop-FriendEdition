using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Each tier is a manifest + SHA-256-pinned ZIP whose root entries are module folders. A manifest may
/// provide one HTTPS ZIP or ordered, individually pinned parts that reconstruct it. Updates are staged
/// and exact-replaced under <c>Modules\</c>; any install failure restores the previous module directories.
/// Manifest checks never download payloads. Every required feed must be verified before launch or install.
/// </summary>
public sealed class ModUpdater : IModUpdateService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly LauncherConfig _config;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, Task> _retryDelay;

    public ModUpdater(LauncherConfig config) : this(config, SharedHttp) { }

    internal ModUpdater(LauncherConfig config, HttpClient http, Func<TimeSpan, Task>? retryDelay = null)
    {
        _config = config;
        _http = http;
        _retryDelay = retryDelay ?? Task.Delay;
    }

    public async Task<ModUpdateCheck> CheckAsync(string modulesDir)
    {
        Task<TierUpdateCheck> suiteTask = CheckTierAsync(
            _config.SuiteManifestUrl,
            Path.Combine(modulesDir, "coop-suite-version.txt"),
            ArmoryComponent.ModSuite,
            "Mod suite");
        Task<TierUpdateCheck> clientTask = CheckTierAsync(
            _config.UpdateManifestUrl,
            Path.Combine(modulesDir, "Coop", "installed-version.txt"),
            ArmoryComponent.CoopClient,
            "Co-op client");

        await Task.WhenAll(suiteTask, clientTask);
        TierUpdateCheck suite = suiteTask.Result;
        TierUpdateCheck client = clientTask.Result;
        return new ModUpdateCheck(
            suite.Status, suite.Manifest,
            client.Status, client.Manifest);
    }

    private async Task<TierUpdateCheck> CheckTierAsync(
        string manifestUrl,
        string versionFile,
        ArmoryComponent component,
        string label)
    {
        string? installed = ReadText(versionFile);
        if (!TryGetHttpsUri(manifestUrl, out Uri? manifestUri))
            return Unverified(component, label, installed, $"{label} feed is not configured securely.");

        FeedFetchResult fetch = await FeedFetch.GetStringAsync(_http, manifestUri!, label, _retryDelay);
        if (fetch.Status != FeedFetchStatus.Success)
            return Unverified(component, label, installed, fetch.Detail);

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(fetch.Body!);
        }
        catch (JsonException)
        {
            return Unverified(component, label, installed, $"{label} feed was malformed.");
        }

        if (!IsManifestValid(manifest))
            return Unverified(component, label, installed, $"{label} feed was malformed.");

        bool updateAvailable = IsNewer(manifest!.Version, installed);
        var status = new ComponentUpdateStatus(
            component,
            label,
            installed,
            manifest.Version,
            manifest.Notes,
            updateAvailable ? ComponentUpdateState.UpdateAvailable : ComponentUpdateState.Current,
            updateAvailable
                ? installed is null ? $"{label} is not installed." : $"{label} update available."
                : $"{label} is current.");
        return new TierUpdateCheck(status, manifest);
    }

    /// <param name="progress">(fraction 0..1 or -1 for indeterminate, status line).</param>
    public async Task<UpdateResult> RunAsync(string modulesDir, Action<double, string> progress)
    {
        ModUpdateCheck check = await CheckAsync(modulesDir);
        if (check.SuiteStatus.State == ComponentUpdateState.Unverified)
            return new(UpdateOutcome.Failed, check.SuiteStatus.Detail);
        if (check.ClientStatus.State == ComponentUpdateState.Unverified)
            return new(UpdateOutcome.Failed, check.ClientStatus.Detail);
        return await InstallAsync(modulesDir, check, (_, fraction, message) => progress(fraction, message));
    }

    public async Task<UpdateResult> InstallAsync(
        string modulesDir,
        ModUpdateCheck check,
        Action<ArmoryComponent, double, string> progress)
    {
        if (check.SuiteStatus.State == ComponentUpdateState.Unverified ||
            check.ClientStatus.State == ComponentUpdateState.Unverified)
            return new(UpdateOutcome.Failed, "Required update feeds were not verified.");

        UpdateResult suite = new(UpdateOutcome.UpToDate, "mod suite current");
        if (check.SuiteStatus.State == ComponentUpdateState.UpdateAvailable)
        {
            if (check.SuiteManifest is null)
                return new(UpdateOutcome.Failed, "Mod suite update plan was incomplete.");
            suite = await InstallTierAsync(
                check.SuiteManifest,
                modulesDir,
                Path.Combine(modulesDir, "coop-suite-version.txt"),
                ArmoryComponent.ModSuite,
                "mod suite",
                progress);
            if (suite.Outcome == UpdateOutcome.Failed)
                return suite;
        }

        UpdateResult client = new(UpdateOutcome.UpToDate, "co-op client current");
        if (check.ClientStatus.State == ComponentUpdateState.UpdateAvailable)
        {
            if (check.ClientManifest is null)
                return new(UpdateOutcome.Failed, "Co-op client update plan was incomplete.");
            client = await InstallTierAsync(
                check.ClientManifest,
                modulesDir,
                Path.Combine(modulesDir, "Coop", "installed-version.txt"),
                ArmoryComponent.CoopClient,
                "co-op client",
                progress);
        }

        return Combine(suite, client);
    }

    private async Task<UpdateResult> InstallTierAsync(
        UpdateManifest manifest,
        string modulesDir,
        string versionFile,
        ArmoryComponent component,
        string label,
        Action<ArmoryComponent, double, string> progress)
    {
        if (!IsManifestValid(manifest))
            return new(UpdateOutcome.Failed, $"{label} update plan was invalid.");
        string tempZip = Path.Combine(Path.GetTempPath(), $"coop-update-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadPayloadAsync(
                manifest,
                tempZip,
                label,
                (fraction, message) => progress(component, fraction, message));

            progress(component, -1, $"Verifying {label}…");
            var actual = await Sha256HexAsync(tempZip);
            if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(UpdateOutcome.Failed, $"{label} failed integrity check — installed kept");

            progress(component, -1, $"Installing {label}…");
            InstallExact(tempZip, modulesDir, versionFile, manifest.Version);

            var note = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $" — {manifest.Notes}";
            return new(UpdateOutcome.Updated, $"{label} updated to {manifest.Version}{note}");
        }
        catch (Exception ex)
        {
            Log.Write($"{label} install failed: {ex}");
            return new(UpdateOutcome.Failed, $"{label} update failed — installed kept");
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

    private async Task DownloadAsync(string url, string dest, string label, Action<double, string> progress)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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

    private async Task DownloadPayloadAsync(
        UpdateManifest manifest,
        string dest,
        string label,
        Action<double, string> progress)
    {
        UpdatePart[] parts = manifest.Parts ?? [];
        if (parts.Length == 0)
        {
            await DownloadAsync(manifest.ClientZipUrl, dest, label, progress);
            return;
        }

        long total = parts.Sum(part => part.Bytes);
        long completed = 0;
        await using var dst = File.Create(dest);
        var buffer = new byte[131072];

        for (int index = 0; index < parts.Length; index++)
        {
            UpdatePart part = parts[index];
            using var resp = await _http.GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is long responseBytes && responseBytes != part.Bytes)
                throw new InvalidDataException(
                    $"{label} part {index + 1} length was {responseBytes}, expected {part.Bytes}");

            await using Stream src = await resp.Content.ReadAsStreamAsync();
            using IncrementalHash partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long partRead = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                partHash.AppendData(buffer, 0, read);
                partRead += read;
                long received = completed + partRead;
                progress(
                    received / (double)total,
                    $"Downloading {label}… part {index + 1} / {parts.Length}, " +
                    $"{received / 1_048_576.0:0} / {total / 1_048_576.0:0} MB");
            }

            if (partRead != part.Bytes)
                throw new InvalidDataException(
                    $"{label} part {index + 1} length was {partRead}, expected {part.Bytes}");
            string actualHash = Convert.ToHexString(partHash.GetHashAndReset()).ToLowerInvariant();
            if (!actualHash.Equals(part.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} part {index + 1} failed integrity check");
            completed += partRead;
        }
    }

    internal static bool IsManifestValid(UpdateManifest? manifest)
    {
        if (manifest is null || ParseParts(manifest.Version) is null)
            return false;

        string sha256 = manifest.Sha256?.Trim() ?? string.Empty;
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            return false;

        bool hasSingleUrl = !string.IsNullOrWhiteSpace(manifest.ClientZipUrl);
        UpdatePart[] parts = manifest.Parts ?? [];
        if (hasSingleUrl)
            return parts.Length == 0 && TryGetHttpsUri(manifest.ClientZipUrl, out _);

        if (parts.Length is 0 or > 1000)
            return false;

        const long githubAssetLimit = 2L * 1024 * 1024 * 1024;
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (UpdatePart? part in parts)
        {
            if (part is null || part.Bytes <= 0 || part.Bytes >= githubAssetLimit ||
                !TryGetHttpsUri(part.Url, out _) || !urls.Add(part.Url))
                return false;
            string partSha256 = part.Sha256?.Trim() ?? string.Empty;
            if (partSha256.Length != 64 || !partSha256.All(Uri.IsHexDigit))
                return false;
        }
        return true;
    }

    private static TierUpdateCheck Unverified(
        ArmoryComponent component,
        string label,
        string? installed,
        string detail) =>
        new(
            new ComponentUpdateStatus(
                component,
                label,
                installed,
                null,
                string.Empty,
                ComponentUpdateState.Unverified,
                detail),
            null);

    private static bool TryGetHttpsUri(string value, out Uri? uri)
    {
        bool valid = Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                     uri.Scheme == Uri.UriSchemeHttps;
        if (!valid) uri = null;
        return valid;
    }

    private sealed record TierUpdateCheck(
        ComponentUpdateStatus Status,
        UpdateManifest? Manifest);

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
                MoveStagedDirectoryWithRetry(stagedModule, destination);
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

    private static void MoveStagedDirectoryWithRetry(string source, string destination)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (IsTransientFileSystemContention(ex) && stopwatch.Elapsed < timeout)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static bool IsTransientFileSystemContention(Exception ex)
    {
        int win32Error = ex.HResult & 0xffff;
        return ex is UnauthorizedAccessException ||
               ex is IOException && win32Error is 5 or 32 or 33;
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
