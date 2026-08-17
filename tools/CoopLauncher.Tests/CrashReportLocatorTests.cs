using CoopLauncher.Services;
using System.IO;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class CrashReportLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coop-crash-reports-" + Guid.NewGuid().ToString("N"));

    public CrashReportLocatorTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    private string CreateReport(string id, bool withBundle)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        if (withBundle) File.WriteAllBytes(Path.Combine(directory, "shareable.zip"), [1, 2, 3]);
        return directory;
    }

    /// <summary>
    /// The in-game reporter leaves a folder behind whenever it never finished writing its bundle,
    /// so a real crash-report root is a mix of complete and incomplete entries. FindLatest sorted
    /// by <c>candidate!.ZipPath</c> without discarding the nulls CreateCandidate returns for the
    /// incomplete ones, so the key selector threw, the catch reported "no crash report", and one
    /// stale folder disabled crash attachment entirely.
    /// </summary>
    [Fact]
    public void FindLatest_IgnoresReportFoldersWithoutABundle()
    {
        CreateReport("2026-08-05_22-56-42_client_672", withBundle: false);
        string complete = CreateReport("2026-08-06_10-00-00_client_900", withBundle: true);

        CrashReportCandidate? found = CrashReportLocator.FindLatestIn(root);

        Assert.NotNull(found);
        Assert.Equal(Path.GetFileName(complete), found!.Id);
    }

    [Fact]
    public void FindLatest_PrefersTheNewestBundle()
    {
        string older = CreateReport("2026-08-05_22-56-42_client_672", withBundle: true);
        File.SetLastWriteTimeUtc(Path.Combine(older, "shareable.zip"), new DateTime(2026, 8, 5, 22, 56, 42, DateTimeKind.Utc));
        string newer = CreateReport("2026-08-06_10-00-00_client_900", withBundle: true);
        File.SetLastWriteTimeUtc(Path.Combine(newer, "shareable.zip"), new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(Path.GetFileName(newer), CrashReportLocator.FindLatestIn(root)?.Id);
    }

    [Fact]
    public void FindLatest_ReturnsNullWhenNoFolderHasABundle()
    {
        CreateReport("2026-08-05_22-56-42_client_672", withBundle: false);

        Assert.Null(CrashReportLocator.FindLatestIn(root));
    }

    [Fact]
    public void FindPending_SkipsIncompleteAndAlreadySubmittedReports()
    {
        CreateReport("incomplete", withBundle: false);
        string submitted = CreateReport("already-submitted", withBundle: true);
        string pending = CreateReport("pending", withBundle: true);
        File.SetLastWriteTimeUtc(Path.Combine(submitted, "shareable.zip"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(pending, "shareable.zip"), DateTime.UtcNow);

        CrashReportCandidate? found = CrashReportLocator.FindPendingIn(
            root, "already-submitted", DateTime.UtcNow.AddMinutes(-5).Ticks);

        Assert.Equal("pending", found?.Id);
    }
}
