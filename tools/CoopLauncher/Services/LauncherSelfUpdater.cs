using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CoopLauncher.Services;

public enum LauncherUpdateOutcome { Disabled, UpToDate, Restarting, Offline, Failed }

public readonly record struct LauncherUpdateResult(LauncherUpdateOutcome Outcome, string Message);

public sealed record LauncherUpdateCommand(
    string StagedExecutablePath,
    string TargetExecutablePath,
    int PreviousProcessId,
    string ExpectedSha256)
{
    public const string ApplySwitch = "--apply-launcher-update";

    public ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = StagedExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(TargetExecutablePath)!,
            UseShellExecute = true,
        };
        info.ArgumentList.Add(ApplySwitch);
        info.ArgumentList.Add(TargetExecutablePath);
        info.ArgumentList.Add(PreviousProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(ExpectedSha256);
        return info;
    }
}

/// <summary>Checks and stages updates for the portable launcher executable.</summary>
public sealed class LauncherSelfUpdater
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly LauncherConfig _config;
    private readonly HttpClient _http;

    public LauncherSelfUpdater(LauncherConfig config) : this(config, SharedHttp) { }

    internal LauncherSelfUpdater(LauncherConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
    }

    public async Task<LauncherUpdateResult> CheckAndStageAsync(
        string executablePath,
        Version currentVersion,
        Action<double, string> progress,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        if (string.IsNullOrWhiteSpace(_config.LauncherManifestUrl))
            return new(LauncherUpdateOutcome.Disabled, "launcher updates off");

        LauncherUpdateManifest? manifest;
        try
        {
            progress(-1, "Checking launcher…");
            using HttpResponseMessage response = await _http.GetAsync(_config.LauncherManifestUrl);
            if (!response.IsSuccessStatusCode)
                return new(LauncherUpdateOutcome.Failed,
                    $"launcher feed returned HTTP {(int)response.StatusCode} — update required");
            try
            {
                manifest = JsonSerializer.Deserialize<LauncherUpdateManifest>(
                    await response.Content.ReadAsStringAsync());
            }
            catch (JsonException)
            {
                return new(LauncherUpdateOutcome.Failed, "launcher feed was malformed — update required");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return new(LauncherUpdateOutcome.Failed,
                $"launcher feed returned HTTP {(int)ex.StatusCode} — update required");
        }
        catch (HttpRequestException)
        {
            return new(LauncherUpdateOutcome.Offline, "Couldn't reach launcher feed — using installed");
        }
        catch (TaskCanceledException)
        {
            return new(LauncherUpdateOutcome.Offline, "Launcher update check timed out — using installed");
        }

        if (!IsManifestValid(manifest))
            return new(LauncherUpdateOutcome.Failed, "launcher feed was malformed — update required");
        if (!IsNewer(manifest!.Version, currentVersion))
            return new(LauncherUpdateOutcome.UpToDate, $"launcher: up to date ({currentVersion})");

        string target = Path.GetFullPath(executablePath);
        string? targetDirectory = Path.GetDirectoryName(target);
        if (targetDirectory is null || !File.Exists(target))
            return new(LauncherUpdateOutcome.Failed, "launcher executable path is invalid");

        string stageDirectory = Path.Combine(
            targetDirectory, $".calradia-launcher-update-{Guid.NewGuid():N}");
        string stagedExecutable = Path.Combine(stageDirectory, Path.GetFileName(target));
        bool handedOff = false;
        try
        {
            Directory.CreateDirectory(stageDirectory);
            progress(-1, $"Downloading launcher {manifest.Version}…");
            using HttpResponseMessage response = await _http.GetAsync(
                manifest.LauncherUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return new(LauncherUpdateOutcome.Failed,
                    $"launcher download returned HTTP {(int)response.StatusCode} — installed kept");

            await using (Stream source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(stagedExecutable))
                await source.CopyToAsync(destination);

            progress(-1, "Verifying launcher…");
            string actualSha = await Sha256HexAsync(stagedExecutable);
            if (!actualSha.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(LauncherUpdateOutcome.Failed,
                    "launcher failed integrity check — installed kept");

            var command = new LauncherUpdateCommand(
                stagedExecutable, target, Environment.ProcessId, actualSha);
            Process? process = (startProcess ?? Process.Start)(command.CreateStartInfo());
            if (process is null)
                return new(LauncherUpdateOutcome.Failed, "launcher update could not start — installed kept");

            handedOff = true;
            string note = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $" — {manifest.Notes}";
            return new(LauncherUpdateOutcome.Restarting,
                $"launcher update {manifest.Version} ready; restarting{note}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return new(LauncherUpdateOutcome.Failed,
                $"launcher download returned HTTP {(int)ex.StatusCode} — installed kept");
        }
        catch (HttpRequestException)
        {
            return new(LauncherUpdateOutcome.Offline, "Couldn't download launcher — using installed");
        }
        catch (TaskCanceledException)
        {
            return new(LauncherUpdateOutcome.Offline, "Launcher download timed out — using installed");
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher staging failed: {ex}");
            return new(LauncherUpdateOutcome.Failed, "launcher update failed — installed kept");
        }
        finally
        {
            if (!handedOff)
                TryDeleteDirectory(stageDirectory);
        }
    }

    internal static bool IsManifestValid(LauncherUpdateManifest? manifest)
    {
        if (manifest is null || !Version.TryParse(manifest.Version, out _))
            return false;
        if (!Uri.TryCreate(manifest.LauncherUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string sha256 = manifest.Sha256?.Trim() ?? string.Empty;
        return sha256.Length == 64 && sha256.All(Uri.IsHexDigit);
    }

    internal static bool IsNewer(string remote, Version current) =>
        Version.TryParse(remote, out Version? remoteVersion) && remoteVersion > current;

    private static async Task<string> Sha256HexAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: a later launch cleans stale staging directories.
        }
    }
}
