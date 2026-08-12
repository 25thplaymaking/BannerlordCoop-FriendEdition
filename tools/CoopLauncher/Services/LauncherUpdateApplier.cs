using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace CoopLauncher.Services;

public sealed record LauncherApplyRequest(
    string StagedExecutablePath,
    string TargetExecutablePath,
    int PreviousProcessId,
    string ExpectedSha256,
    bool ContinuePreparation = false);

public sealed record LauncherCompletionRequest(
    string TargetExecutablePath,
    int HelperProcessId,
    string StageDirectory,
    string BackupPath,
    bool ContinuePreparation = false);

public readonly record struct LauncherApplyResult(bool Succeeded, string Message);

/// <summary>Performs the verified two-process handoff required to replace a running Windows exe.</summary>
public static class LauncherUpdateApplier
{
    public const string CompletionSwitch = "--launcher-update-complete";
    public const string SkipOnceSwitch = "--skip-launcher-update-once";
    public const string ContinuePreparationSwitch = "--continue-army-preparation";
    internal const string StagePrefix = ".calradia-launcher-update-";

    internal static bool ShouldSkipSelfUpdate(string[] args) =>
        args.Length == 1 && args[0] == SkipOnceSwitch;

    public static bool TryParseApplyArguments(
        string[] args,
        string runningExecutablePath,
        out LauncherApplyRequest? request)
    {
        request = null;
        bool continuePreparation = args.Length == 5 && args[4] == ContinuePreparationSwitch;
        if ((args.Length != 4 && !continuePreparation) ||
            args[0] != LauncherUpdateCommand.ApplySwitch ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int previousPid) ||
            !IsSha256(args[3]))
            return false;

        try
        {
            var candidate = new LauncherApplyRequest(
                Path.GetFullPath(runningExecutablePath),
                Path.GetFullPath(args[1]),
                previousPid,
                args[3].ToLowerInvariant(),
                continuePreparation);
            if (!IsSafeApplyLayout(candidate)) return false;
            request = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static LauncherApplyResult ApplyAndRelaunch(
        LauncherApplyRequest request,
        Func<int, bool>? waitForExit = null,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        if (!IsSafeApplyLayout(request))
            return new(false, "Launcher update paths were unsafe.");
        Func<int, bool> wait = waitForExit ?? WaitForProcessExit;
        Func<ProcessStartInfo, Process?> start = startProcess ?? (info => Process.Start(info));
        if (!HashMatches(request.StagedExecutablePath, request.ExpectedSha256))
        {
            if (wait(request.PreviousProcessId)) TryStartRollback(request.TargetExecutablePath, start);
            return new(false, "Launcher update failed its final integrity check.");
        }

        if (!wait(request.PreviousProcessId))
            return new(false, "The previous launcher did not exit in time.");

        string target = request.TargetExecutablePath;
        string stageDirectory = Path.GetDirectoryName(request.StagedExecutablePath)!;
        string backup = Path.Combine(stageDirectory, $"{Path.GetFileName(target)}.previous");
        string pending = Path.Combine(
            Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.pending-{Guid.NewGuid():N}");
        bool replaced = false;

        try
        {
            File.Copy(request.StagedExecutablePath, pending, overwrite: false);
            File.Replace(pending, target, backup, ignoreMetadataErrors: true);
            replaced = true;

            Process? relaunched = start(BuildCompletionStartInfo(
                target, Environment.ProcessId, stageDirectory, backup,
                request.ContinuePreparation));
            if (relaunched is null)
                throw new InvalidOperationException("The updated launcher process did not start.");

            return new(true, "Launcher updated and restarted.");
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher apply failed: {ex}");
            if (replaced) TryRestoreBackup(backup, target);
            TryDeleteFile(pending);
            TryStartRollback(target, start);
            return new(false, $"Launcher update could not be applied: {ex.Message}");
        }
    }

    public static bool TryParseCompletionArguments(
        string[] args,
        string targetExecutablePath,
        out LauncherCompletionRequest? request)
    {
        request = null;
        bool continuePreparation = args.Length == 5 && args[4] == ContinuePreparationSwitch;
        if ((args.Length != 4 && !continuePreparation) || args[0] != CompletionSwitch ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int helperPid))
            return false;

        try
        {
            var candidate = new LauncherCompletionRequest(
                Path.GetFullPath(targetExecutablePath),
                helperPid,
                Path.GetFullPath(args[2]),
                Path.GetFullPath(args[3]),
                continuePreparation);
            if (!IsSafeCompletionLayout(candidate)) return false;
            request = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupAfterStartup(
        LauncherCompletionRequest request,
        Func<int, bool>? waitForExit = null)
    {
        if (!IsSafeCompletionLayout(request)) return;
        try
        {
            if (!(waitForExit ?? WaitForProcessExit)(request.HelperProcessId))
                return;
            TryDeleteFile(request.BackupPath);
            if (Directory.Exists(request.StageDirectory))
                Directory.Delete(request.StageDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher update cleanup deferred: {ex.Message}");
        }
    }

    private static ProcessStartInfo BuildCompletionStartInfo(
        string target,
        int helperPid,
        string stageDirectory,
        string backup,
        bool continuePreparation)
    {
        var info = NewStartInfo(target);
        info.ArgumentList.Add(CompletionSwitch);
        info.ArgumentList.Add(helperPid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(stageDirectory);
        info.ArgumentList.Add(backup);
        if (continuePreparation)
            info.ArgumentList.Add(ContinuePreparationSwitch);
        return info;
    }

    private static ProcessStartInfo BuildRollbackStartInfo(string target)
    {
        var info = NewStartInfo(target);
        info.ArgumentList.Add(SkipOnceSwitch);
        return info;
    }

    private static ProcessStartInfo NewStartInfo(string target) => new()
    {
        FileName = target,
        WorkingDirectory = Path.GetDirectoryName(target)!,
        UseShellExecute = true,
    };

    private static bool IsSafeApplyLayout(LauncherApplyRequest request)
    {
        if (!File.Exists(request.StagedExecutablePath) || !File.Exists(request.TargetExecutablePath) ||
            !IsSha256(request.ExpectedSha256))
            return false;

        string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(request.TargetExecutablePath))!;
        string stageDirectory = Path.GetDirectoryName(Path.GetFullPath(request.StagedExecutablePath))!;
        return Path.GetFileName(stageDirectory).StartsWith(StagePrefix, StringComparison.Ordinal) &&
               PathEquals(Path.GetDirectoryName(stageDirectory), targetDirectory) &&
               !PathEquals(request.StagedExecutablePath, request.TargetExecutablePath);
    }

    private static bool IsSafeCompletionLayout(LauncherCompletionRequest request)
    {
        string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(request.TargetExecutablePath))!;
        string stageDirectory = Path.GetFullPath(request.StageDirectory);
        return Path.GetFileName(stageDirectory).StartsWith(StagePrefix, StringComparison.Ordinal) &&
               PathEquals(Path.GetDirectoryName(stageDirectory), targetDirectory) &&
               IsWithin(request.BackupPath, stageDirectory);
    }

    private static bool IsWithin(string path, string directory)
    {
        string root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForProcessExit(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRestoreBackup(string backup, string target)
    {
        try
        {
            if (File.Exists(backup)) File.Replace(backup, target, null, ignoreMetadataErrors: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher rollback failed: {ex}");
        }
    }

    private static void TryStartRollback(string target, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            if (File.Exists(target)) start(BuildRollbackStartInfo(target));
        }
        catch (Exception ex)
        {
            Log.Write($"Restored launcher could not restart: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is retried after a later successful startup.
        }
    }
}
