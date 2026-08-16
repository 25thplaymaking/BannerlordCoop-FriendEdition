using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoopLauncher.Services;

public sealed class CampaignStatsSnapshot
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("campaignDay")] public int CampaignDay { get; set; }
    [JsonPropertyName("onlinePlayers")] public int OnlinePlayers { get; set; }
    // `players` is retained solely so launchers can render a host snapshot published before schema 2.
    [JsonPropertyName("players")] public List<CampaignLordStats> Players { get; set; } = [];
    [JsonPropertyName("lords")] public List<CampaignLordStats> Lords { get; set; } = [];

    public IEnumerable<CampaignLordStats> EnumerateLords()
        => Lords.Count > 0 ? Lords : Players.Select(CampaignLordStats.FromLegacyPlayer);
}

public sealed class CampaignLordStats
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("controller")] public string Controller { get; set; } = "ai";
    [JsonPropertyName("clan")] public string Clan { get; set; } = "";
    [JsonPropertyName("kingdom")] public string Kingdom { get; set; } = "Independent";
    [JsonPropertyName("culture")] public string Culture { get; set; } = "Unknown";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("gold")] public int Gold { get; set; }
    [JsonPropertyName("renown")] public int Renown { get; set; }
    [JsonPropertyName("influence")] public int Influence { get; set; }
    [JsonPropertyName("clanTier")] public int ClanTier { get; set; }
    [JsonPropertyName("partySize")] public int PartySize { get; set; }
    [JsonPropertyName("fiefs")] public int Fiefs { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("currentAction")] public string CurrentAction { get; set; } = "unknown";
    [JsonPropertyName("location")] public string Location { get; set; } = "Unknown";

    internal static CampaignLordStats FromLegacyPlayer(CampaignLordStats player)
    {
        player.Controller = "player";
        player.Status = player.Online ? "online" : "active";
        return player;
    }
}

public sealed record ReportSubmission(
    string ClientId,
    string Kind,
    string Title,
    string Description,
    string LauncherVersion,
    string GameVersion,
    string Logs);

public sealed record ReportResult(
    bool Success,
    string Message,
    string? IssueUrl,
    string? ReportId = null,
    string? UploadToken = null);

public sealed record ReportAttachmentResult(bool Success, string Message);

public sealed class PortalClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Uri? _baseUri;
    private readonly Uri? _manifestUri;
    private readonly HttpClient _http;
    private Uri? _discoveredBaseUri;

    public PortalClient(string portalUrl, string portalManifestUrl) : this(portalUrl, portalManifestUrl, SharedHttp) { }

    internal PortalClient(string portalUrl, string portalManifestUrl, HttpClient http)
    {
        _http = http;
        _baseUri = Uri.TryCreate(portalUrl?.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri) &&
                   uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
        _manifestUri = Uri.TryCreate(portalManifestUrl, UriKind.Absolute, out Uri? manifest) &&
                       manifest.Scheme == Uri.UriSchemeHttps
            ? manifest
            : null;
    }

    public bool IsConfigured => _baseUri is not null || _manifestUri is not null;

    public async Task<CampaignStatsSnapshot?> LoadStatsAsync()
    {
        Uri? baseUri = await ResolveBaseUriAsync();
        if (baseUri is null) return null;
        using HttpResponseMessage response = await _http.GetAsync(new Uri(baseUri, "stats"));
        if (!response.IsSuccessStatusCode) return null;
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CampaignStatsSnapshot>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<ReportResult> SubmitReportAsync(ReportSubmission report)
    {
        Uri? baseUri = await ResolveBaseUriAsync();
        if (baseUri is null)
            return new(false, "Automatic reporting is not configured for this launcher build.", null);

        string json = JsonSerializer.Serialize(report);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync(new Uri(baseUri, "reports"), content);
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            PortalReportResponse? parsed = JsonSerializer.Deserialize<PortalReportResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (response.IsSuccessStatusCode && parsed is not null)
                return new(true, parsed.Message ?? "Issue created.", parsed.IssueUrl,
                    parsed.ReportId, parsed.UploadToken);
            return new(false, parsed?.Message ?? $"Report service returned {(int)response.StatusCode}.", null);
        }
        catch (JsonException)
        {
            return new(false, $"Report service returned {(int)response.StatusCode}.", null);
        }
    }

    public async Task<ReportAttachmentResult> UploadReportBundleAsync(
        ReportResult report,
        string packagePath)
    {
        Uri? baseUri = await ResolveBaseUriAsync();
        if (baseUri is null || string.IsNullOrWhiteSpace(report.ReportId) ||
            string.IsNullOrWhiteSpace(report.UploadToken))
            return new(false, "The report was created, but this portal cannot receive attachments.");
        if (!File.Exists(packagePath))
            return new(false, "The local report attachment package is no longer available.");

        try
        {
            using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            content.Headers.ContentLength = stream.Length;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(baseUri, $"report-assets/{Uri.EscapeDataString(report.ReportId)}"))
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("X-Report-Token", report.UploadToken);
            using HttpResponseMessage response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return new(true, "Attachments were received with the report.");
            return new(false, $"The issue was created, but attachment upload returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, $"The issue was created, but attachment upload failed: {ex.Message}");
        }
    }

    private async Task<Uri?> ResolveBaseUriAsync()
    {
        if (_baseUri is not null) return _baseUri;
        if (_discoveredBaseUri is not null) return _discoveredBaseUri;
        if (_manifestUri is null) return null;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(_manifestUri);
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync();
            PortalDiscovery? discovery = JsonSerializer.Deserialize<PortalDiscovery>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (Uri.TryCreate(discovery?.PortalUrl?.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
                _discoveredBaseUri = uri;
            return _discoveredBaseUri;
        }
        catch
        {
            return null;
        }
    }

    private sealed class PortalReportResponse
    {
        public string? Message { get; set; }
        public string? IssueUrl { get; set; }
        public string? ReportId { get; set; }
        public string? UploadToken { get; set; }
    }

    private sealed class PortalDiscovery
    {
        public string? PortalUrl { get; set; }
    }
}
