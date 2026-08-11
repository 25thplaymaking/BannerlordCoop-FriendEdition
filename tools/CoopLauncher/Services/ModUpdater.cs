using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CoopLauncher.Services;

public enum UpdateOutcome { Disabled, UpToDate, Updated, Offline, Failed }

public readonly record struct UpdateResult(UpdateOutcome Outcome, string Message);

/// <summary>
/// Keeps the installed client build in step with the group's published feed. Reports progress so the
/// launcher's rail can narrate the pull, and fails soft: an unreachable feed never blocks play, it
/// just runs the build already on disk.
/// </summary>
public sealed class ModUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string VersionFile = "installed-version.txt";

    private readonly LauncherConfig _config;
    public ModUpdater(LauncherConfig config) => _config = config;

    /// <param name="progress">(fraction 0..1 or -1 for indeterminate, status line).</param>
    public async Task<UpdateResult> RunAsync(string modulesDir, Action<double, string> progress)
    {
        if (string.IsNullOrWhiteSpace(_config.UpdateManifestUrl))
            return new(UpdateOutcome.Disabled, "Updates off — launching installed build");

        UpdateManifest? manifest;
        try
        {
            progress(-1, "Checking for updates…");
            var json = await Http.GetStringAsync(_config.UpdateManifestUrl);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json);
        }
        catch
        {
            return new(UpdateOutcome.Offline, "Couldn't reach the update server — launching installed build");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.ClientZipUrl))
            return new(UpdateOutcome.Failed, "Update feed was malformed — launching installed build");

        var coopDir = Path.Combine(modulesDir, "Coop");
        var installed = ReadInstalledVersion(coopDir);
        if (!IsNewer(manifest.Version, installed))
            return new(UpdateOutcome.UpToDate, $"Up to date (build {installed ?? "—"})");

        string tempZip = Path.Combine(Path.GetTempPath(), $"coop-{manifest.Version}.zip");
        try
        {
            await DownloadAsync(manifest.ClientZipUrl, tempZip, progress);

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                progress(-1, "Verifying download…");
                var actual = await Sha256HexAsync(tempZip);
                if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new(UpdateOutcome.Failed, "Update failed integrity check — installed build kept");
            }

            progress(-1, "Installing update…");
            ExtractOverwrite(tempZip, modulesDir);
            WriteInstalledVersion(coopDir, manifest.Version);

            var note = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $" — {manifest.Notes}";
            return new(UpdateOutcome.Updated, $"Updated to build {manifest.Version}{note}");
        }
        catch
        {
            return new(UpdateOutcome.Failed, "Update failed — launching installed build");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* temp cleanup best-effort */ }
        }
    }

    private static async Task DownloadAsync(string url, string dest, Action<double, string> progress)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0)
                progress(read / (double)total, $"Downloading update… {read / 1_048_576.0:0.0} / {total / 1_048_576.0:0.0} MB");
            else
                progress(-1, $"Downloading update… {read / 1_048_576.0:0.0} MB");
        }
    }

    private static void ExtractOverwrite(string zipPath, string destRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(destRoot);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            // Guard against zip-slip: never let an entry escape the Modules folder.
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string? ReadInstalledVersion(string coopDir)
    {
        try
        {
            var f = Path.Combine(coopDir, VersionFile);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch { return null; }
    }

    private static void WriteInstalledVersion(string coopDir, string version)
    {
        Directory.CreateDirectory(coopDir);
        File.WriteAllText(Path.Combine(coopDir, VersionFile), version.Trim());
    }

    /// <summary>Dotted-numeric compare ("2026.8.10.2" &gt; "2026.8.10.1"); any unparsable diff = newer.</summary>
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
