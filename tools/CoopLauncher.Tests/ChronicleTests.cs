using System.IO;
using CoopLauncher;
using CoopLauncher.Services;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ChronicleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chronicle-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string HistoryPath => Path.Combine(_dir, "chronicle-history.json");

    [Fact]
    public void ResolveFeedUrl_ExplicitConfigWins_ElseDerivedFromClientFeed()
    {
        Assert.Equal(
            "https://example.com/mine.json",
            Chronicle.ResolveFeedUrl("https://example.com/mine.json", "https://x/client-stable/launcher.json"));

        Assert.Equal(
            "https://github.com/o/r/releases/download/client-stable/changelog.json",
            Chronicle.ResolveFeedUrl("", "https://github.com/o/r/releases/download/client-stable/launcher.json"));

        // Channel choice carries into the derived URL because it derives from the effective feed.
        Assert.Equal(
            "https://x/client-nightly/changelog.json",
            Chronicle.ResolveFeedUrl("", "https://x/client-nightly/launcher.json"));

        Assert.Null(Chronicle.ResolveFeedUrl("", ""));
        Assert.Null(Chronicle.ResolveFeedUrl("", "no-slashes"));
    }

    [Fact]
    public void Parse_ToleratesMalformedDocuments()
    {
        Assert.Empty(Chronicle.Parse(null));
        Assert.Empty(Chronicle.Parse(""));
        Assert.Empty(Chronicle.Parse("{ not json"));
        Assert.Empty(Chronicle.Parse("{\"an\":\"object, not a list\"}"));

        List<ChronicleEntry> entries = Chronicle.Parse(
            "[{\"version\":\"2026.08.14.0001\",\"date\":\"2026-08-14\",\"title\":\"Join freeze fixed\"," +
            "\"highlights\":[\"Clients no longer freeze on Applying patches\"]}]");
        ChronicleEntry entry = Assert.Single(entries);
        Assert.Equal("Join freeze fixed", entry.Title);
        Assert.True(entry.IsCurated);
    }

    [Fact]
    public void Merge_NewestFirst_AndBuildNotesNeverDuplicateACuratedVersion()
    {
        var curated = new List<ChronicleEntry>
        {
            new() { Version = "2026.08.13.2231", Date = "2026-08-13", Title = "Old" },
            new() { Version = "2026.08.14.0001", Date = "2026-08-14", Title = "New" },
        };
        var history = new List<ChronicleEntry>
        {
            // Same version as a curated entry — must be suppressed.
            new() { Version = "2026.08.14.0001", Date = "2026-08-14", Title = "Co-op client 2026.08.14.0001", Source = "build" },
            // Uncurated build — must appear.
            new() { Version = "2026.08.13.2356", Date = "2026-08-13", Title = "Co-op client 2026.08.13.2356", Source = "build" },
        };

        List<ChronicleEntry> merged = Chronicle.Merge(curated, history);

        Assert.Equal(3, merged.Count);
        Assert.Equal("New", merged[0].Title);
        Assert.Equal("Co-op client 2026.08.13.2356", merged[1].Title);
        Assert.Equal("Old", merged[2].Title);
    }

    [Fact]
    public void RecordBuildNotes_IsIdempotent_AndSkipsComponentsWithoutNotesOrVersion()
    {
        ArmorySnapshot snapshot = SnapshotWithClientNotes("2026.08.14.0001", "Fix the join freeze");

        Chronicle.RecordBuildNotes(snapshot, HistoryPath);
        Chronicle.RecordBuildNotes(snapshot, HistoryPath);

        List<ChronicleEntry> history = Chronicle.Parse(File.ReadAllText(HistoryPath));
        ChronicleEntry entry = Assert.Single(history);
        Assert.Equal("build", entry.Source);
        Assert.Equal("Co-op client 2026.08.14.0001", entry.Title);
        Assert.Equal("Fix the join freeze", Assert.Single(entry.Highlights));
    }

    [Fact]
    public void RecordBuildNotes_CapsHistory()
    {
        for (int i = 0; i < Chronicle.MaxHistoryEntries + 25; i++)
        {
            Chronicle.RecordBuildNotes(
                SnapshotWithClientNotes($"2026.08.14.{i:D4}", $"note {i}"), HistoryPath);
        }

        List<ChronicleEntry> history = Chronicle.Parse(File.ReadAllText(HistoryPath));
        Assert.Equal(Chronicle.MaxHistoryEntries, history.Count);
        // The newest entries survive the cap.
        Assert.Contains(history, entry => entry.Version == $"2026.08.14.{Chronicle.MaxHistoryEntries + 24:D4}");
    }

    private static ArmorySnapshot SnapshotWithClientNotes(string version, string notes)
    {
        ComponentUpdateStatus Silent(ArmoryComponent component, string label) =>
            new(component, label, "1", null, "", ComponentUpdateState.Current, "current");

        return new ArmorySnapshot(
            new LauncherUpdateCheck(Silent(ArmoryComponent.Launcher, "Launcher"), null),
            new ModUpdateCheck(
                Silent(ArmoryComponent.ModSuite, "Mod suite"), null,
                new ComponentUpdateStatus(
                    ArmoryComponent.CoopClient, "Co-op client", "0", version, notes,
                    ComponentUpdateState.UpdateAvailable, "update available"),
                null));
    }
}
