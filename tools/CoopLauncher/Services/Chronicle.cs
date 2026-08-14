using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoopLauncher.Services;

/// <summary>One Chronicle row: a curated release write-up or an accumulated one-line build note.</summary>
public sealed class ChronicleEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>ISO date (yyyy-MM-dd); ordering key together with <see cref="Version"/>.</summary>
    [JsonPropertyName("date")] public string Date { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>Plain friend-readable bullets ("Kingdom tab no longer goes black"), not commit subjects.</summary>
    [JsonPropertyName("highlights")] public List<string> Highlights { get; set; } = new();

    /// <summary>"curated" for authored entries, "build" for accumulated manifest notes.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "curated";

    [JsonIgnore] public bool IsCurated => !string.Equals(Source, "build", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Chronicle tab's content pipeline. Two inputs, one ordered list:
/// <list type="bullet">
/// <item><description><b>Curated feed</b> — <c>changelog.json</c> published beside
/// <c>launcher.json</c> on the same release tag, authored per release in friend-readable
/// language. Fetched through the same resilient <see cref="FeedFetch"/> path as the manifests and
/// cached locally, so the tab renders offline and survives a GitHub blip.</description></item>
/// <item><description><b>Build notes</b> — every armory check records the manifests' one-line
/// <c>notes</c> into a local capped history, so builds that never got a curated write-up still
/// appear (members who skip builds would otherwise miss those lines entirely).</description></item>
/// </list>
/// Everything here is presentation-side convenience: any failure degrades to an empty or partial
/// chronicle and must never affect the update/launch flow.
/// </summary>
public static class Chronicle
{
    internal const int MaxHistoryEntries = 200;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalradiaCoop", "chronicle-cache.json");

    public static string HistoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalradiaCoop", "chronicle-history.json");

    /// <summary>
    /// The curated feed URL: an explicit config value wins; otherwise derive it from the client
    /// feed by swapping the file name, so the chronicle always rides the member's chosen channel.
    /// Null when neither yields a usable URL (chronicle simply stays empty).
    /// </summary>
    internal static string? ResolveFeedUrl(string chronicleUrl, string updateManifestUrl)
    {
        if (!string.IsNullOrWhiteSpace(chronicleUrl)) return chronicleUrl;
        if (string.IsNullOrWhiteSpace(updateManifestUrl)) return null;

        int slash = updateManifestUrl.LastIndexOf('/');
        if (slash < 0) return null;
        return updateManifestUrl.Substring(0, slash + 1) + "changelog.json";
    }

    /// <summary>Tolerant parse: a malformed document yields no entries, never an exception.</summary>
    internal static List<ChronicleEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<ChronicleEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<ChronicleEntry>>(json, Options);
            return entries?.Where(entry => entry is not null).ToList() ?? new List<ChronicleEntry>();
        }
        catch
        {
            return new List<ChronicleEntry>();
        }
    }

    /// <summary>
    /// Merge curated entries with the accumulated build notes: newest first (date, then version,
    /// ordinal descending — the feed versions are zero-padded timestamps so ordinal order is
    /// chronological), with build notes deduplicated against curated entries covering the same
    /// version so a release never appears twice.
    /// </summary>
    internal static List<ChronicleEntry> Merge(
        IReadOnlyList<ChronicleEntry> curated,
        IReadOnlyList<ChronicleEntry> history)
    {
        var curatedVersions = new HashSet<string>(
            curated.Where(entry => !string.IsNullOrWhiteSpace(entry.Version)).Select(entry => entry.Version),
            StringComparer.OrdinalIgnoreCase);

        return curated
            .Concat(history.Where(entry => !curatedVersions.Contains(entry.Version)))
            .OrderByDescending(entry => entry.Date, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Version, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Record each verified component's one-line manifest note into the local history, keyed by
    /// component + version so re-checks are idempotent. Silently no-ops on IO failure.
    /// </summary>
    public static void RecordBuildNotes(ArmorySnapshot snapshot) =>
        RecordBuildNotes(snapshot, HistoryPath);

    internal static void RecordBuildNotes(ArmorySnapshot snapshot, string historyPath)
    {
        try
        {
            List<ChronicleEntry> history = LoadList(historyPath);
            bool changed = false;

            foreach (ComponentUpdateStatus component in snapshot.Components)
            {
                string? version = component.AvailableVersion;
                if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(component.Notes))
                    continue;

                string title = $"{component.Label} {version}";
                if (history.Any(entry =>
                        string.Equals(entry.Version, version, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Title, title, StringComparison.OrdinalIgnoreCase)))
                    continue;

                history.Add(new ChronicleEntry
                {
                    Version = version,
                    Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    Title = title,
                    Highlights = new List<string> { component.Notes },
                    Source = "build",
                });
                changed = true;
            }

            if (!changed) return;
            if (history.Count > MaxHistoryEntries)
                history = history.Skip(history.Count - MaxHistoryEntries).ToList();
            SaveList(historyPath, history);
        }
        catch
        {
            // Chronicle history is best-effort.
        }
    }

    /// <summary>
    /// Fetch the curated feed (falling back to the local cache on any failure) and merge with the
    /// accumulated build notes. Never throws.
    /// </summary>
    public static async Task<List<ChronicleEntry>> LoadAsync(LauncherConfig config)
    {
        List<ChronicleEntry> curated = new();
        string? url = ResolveFeedUrl(config.ChronicleUrl, config.UpdateManifestUrl);
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            try
            {
                FeedFetchResult result = await FeedFetch.GetStringAsync(
                    SharedHttp, uri, "Chronicle", Task.Delay);
                if (result.Status == FeedFetchStatus.Success && result.Body is not null)
                {
                    curated = Parse(result.Body);
                    if (curated.Count > 0) SaveList(CachePath, curated);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Chronicle fetch failed: {ex.Message}");
            }
        }

        if (curated.Count == 0) curated = LoadList(CachePath);
        return Merge(curated, LoadList(HistoryPath));
    }

    private static List<ChronicleEntry> LoadList(string path)
    {
        try
        {
            if (File.Exists(path)) return Parse(File.ReadAllText(path));
        }
        catch
        {
            // Fall through to empty.
        }
        return new List<ChronicleEntry>();
    }

    private static void SaveList(string path, List<ChronicleEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
        }
        catch
        {
            // Best-effort cache.
        }
    }
}
