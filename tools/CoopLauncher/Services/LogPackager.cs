using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace CoopLauncher.Services;

public readonly record struct LogPackageResult(bool Success, string Message, string? ZipPath, int FileCount);

/// <summary>
/// Packages the launcher + co-op game logs into one timestamped zip on the Desktop so a member can send a
/// single file back when something breaks. On-demand and independent of the crash reporter: the softlocks
/// and black-screens that hang the game without a hard crash never trigger a crash bundle, so this is the
/// only way to capture their logs. Copies each file through a shared read handle so a log the game is still
/// writing can be captured, and skips the multi-GB native dumps (the managed stacks live in Coop_client.log).
/// </summary>
public static class LogPackager
{
    public static LogPackageResult Package(string? bannerlordExePath)
    {
        try
        {
            var sources = CollectSources(bannerlordExePath);
            if (sources.Count == 0)
                return new(false, "No logs were found to package yet — launch the game once, then try again.", null, 0);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                desktop = AppContext.BaseDirectory;
            string zipPath = Path.Combine(desktop, $"CalradiaCoop-Logs-{Timestamp()}.zip");

            int packaged = 0;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach ((string entryName, string filePath) in sources)
                {
                    try
                    {
                        using var src = new FileStream(
                            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using Stream dst = entry.Open();
                        src.CopyTo(dst);
                        packaged++;
                    }
                    catch
                    {
                        // A locked or vanished file must not fail the whole package — capture the rest.
                    }
                }
            }

            if (packaged == 0)
            {
                try { File.Delete(zipPath); } catch { /* best-effort */ }
                return new(false, "Found logs but none could be read (all locked).", null, 0);
            }

            Log.Write($"Packaged {packaged} log file(s) to {zipPath}");
            return new(true, $"Packaged {packaged} log file(s).", zipPath, packaged);
        }
        catch (Exception ex)
        {
            Log.Write($"Log packaging failed: {ex}");
            return new(false, $"Could not package logs: {ex.Message}", null, 0);
        }
    }

    /// <summary>Opens Explorer with the packaged zip pre-selected. Best-effort; never throws.</summary>
    public static void RevealInExplorer(string zipPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not reveal logs zip in Explorer: {ex.Message}");
        }
    }

    private static List<(string EntryName, string Path)> CollectSources(string? bannerlordExePath)
    {
        var result = new List<(string, string)>();

        void AddFile(string dir, string name, string category)
        {
            try
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) result.Add(($"{category}/{name}", p));
            }
            catch { /* skip */ }
        }

        void AddGlob(string dir, string pattern, string category)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, pattern))
                    result.Add(($"{category}/{Path.GetFileName(f)}", f));
            }
            catch { /* skip */ }
        }

        // The launcher's own log.
        try { AddFile(Path.GetDirectoryName(Log.Path)!, Path.GetFileName(Log.Path), "launcher"); }
        catch { /* skip */ }

        // Co-op + engine logs beside Bannerlord.exe (Win64_Shipping_Client).
        if (!string.IsNullOrWhiteSpace(bannerlordExePath))
        {
            string? binDir = Path.GetDirectoryName(bannerlordExePath);
            if (binDir != null)
            {
                foreach (string name in new[]
                         { "Coop_client.log", "Coop_firstchance.log", "Coop_server.log", "BLSE_lasterror.log" })
                    AddFile(binDir, name, "game");
                AddGlob(binDir, "rgl_log*.txt", "game");
                AddGlob(binDir, "watchdog_log*.txt", "game");
            }
        }

        return result;
    }

    private static string Timestamp()
    {
        DateTime now = DateTime.Now;
        return $"{now:yyyy-MM-dd_HH-mm-ss}";
    }
}
