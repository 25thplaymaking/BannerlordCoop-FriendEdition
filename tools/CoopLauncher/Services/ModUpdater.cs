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
/// Each tier is a manifest + zip whose root entries are module folders, extracted over
/// <c>Modules\</c>. Both fail soft: an unreachable feed never blocks play, it runs what's installed.
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
        catch
        {
            return new(UpdateOutcome.Offline, $"Couldn't reach the {label} feed — using installed");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.ClientZipUrl))
            return new(UpdateOutcome.Failed, $"{label} feed was malformed — using installed");

        var installed = ReadText(versionFile);
        if (!IsNewer(manifest.Version, installed))
            return new(UpdateOutcome.UpToDate, $"{label}: up to date (build {installed ?? "—"})");

        string tempZip = Path.Combine(Path.GetTempPath(), $"coop-{label.Replace(' ', '-')}-{manifest.Version}.zip");
        try
        {
            await DownloadAsync(manifest.ClientZipUrl, tempZip, label, progress);

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                progress(-1, $"Verifying {label}…");
                var actual = await Sha256HexAsync(tempZip);
                if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new(UpdateOutcome.Failed, $"{label} failed integrity check — installed kept");
            }

            progress(-1, $"Installing {label}…");
            ExtractOverwrite(tempZip, modulesDir);
            WriteText(versionFile, manifest.Version);

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

    private static UpdateResult Combine(UpdateResult suite, UpdateResult client)
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

    private static void ExtractOverwrite(string zipPath, string destRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(destRoot);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;  // zip-slip guard

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
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
