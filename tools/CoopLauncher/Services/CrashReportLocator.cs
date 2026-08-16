using System.IO;

namespace CoopLauncher.Services;

/// <summary>Finds the bundle already prepared by the in-game crash reporter; it never collects a dump itself.</summary>
public sealed record CrashReportCandidate(
    string Id,
    string DirectoryPath,
    string ZipPath,
    string Summary,
    DateTime CreatedUtc);

public static class CrashReportLocator
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Mount and Blade II Bannerlord", "Coop Crash Reports");

    public static CrashReportCandidate? FindLatest()
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            return Directory.EnumerateDirectories(Root)
                .Select(CreateCandidate)
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate!.ZipPath))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static CrashReportCandidate? FindPending(string lastSubmittedId, long newerThanUtcTicks)
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            return Directory.EnumerateDirectories(Root)
                .Select(CreateCandidate)
                .Where(candidate => candidate is not null &&
                    candidate.CreatedUtc.Ticks > newerThanUtcTicks &&
                    !string.Equals(candidate.Id, lastSubmittedId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate!.CreatedUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static CrashReportCandidate? CreateCandidate(string directory)
    {
        string zip = Path.Combine(directory, "shareable.zip");
        if (!File.Exists(zip)) return null;

        string summary = "A diagnostic bundle is ready for review.";
        string report = Path.Combine(directory, "report.txt");
        try
        {
            if (File.Exists(report))
            {
                string[] lines = File.ReadAllLines(report).Take(12).ToArray();
                if (lines.Length > 0) summary = string.Join(Environment.NewLine, lines);
            }
        }
        catch
        {
            // A locked report remains reportable as a bundle; the summary is only convenience text.
        }

        return new CrashReportCandidate(
            Path.GetFileName(directory),
            directory,
            zip,
            summary,
            File.GetLastWriteTimeUtc(zip));
    }
}
