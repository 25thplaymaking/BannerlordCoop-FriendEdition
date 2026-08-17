using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoopLauncher;

/// <summary>
/// The one file a group admin edits to point the launcher at a different server or build feed.
/// Ships beside the .exe as <c>launcher-config.json</c>; a missing file falls back to the baked
/// defaults below so a stripped download still launches.
/// </summary>
public sealed class LauncherConfig
{
    internal const string CurrentModuleToken =
        "_MODULES_*Bannerlord.Harmony*Bannerlord.ButterLib*Bannerlord.UIExtenderEx*Bannerlord.MBOptionScreen" +
        "*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*OpenSourceSaddlery*OpenSourceWeaponry" +
        "*OpenSourceArmory*PlayerSettlement*Coop*ImprovedGarrisons" +
        "*DismembermentPlus*Fourberie*Bannerlord.Diplomacy*UnblockableThrust*RebellionsAndDemographics*_MODULES_";

    // The production token shipped before the gear modules and R&D were activated. Launcher self-
    // updates deliberately preserve the adjacent private config, so migrate only this exact known
    // default and leave genuinely customized module lists untouched.
    internal const string LegacyModuleTokenBeforeGearAndDemographics =
        "_MODULES_*Bannerlord.Harmony*Bannerlord.ButterLib*Bannerlord.UIExtenderEx*Bannerlord.MBOptionScreen" +
        "*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*PlayerSettlement*Coop*ImprovedGarrisons" +
        "*DismembermentPlus*Fourberie*Bannerlord.Diplomacy*UnblockableThrust*_MODULES_";

    /// <summary>Shown as the launcher's display title.</summary>
    public string GroupName { get; set; } = "Calradia Co-op";

    /// <summary>Co-op host the launcher joins and probes for the online banner.</summary>
    public string ServerHost { get; set; } = "205.209.116.114";
    public int ServerPort { get; set; } = 4200;

    /// <summary>
    /// Group-private join token. The public build deliberately leaves this empty; group admins
    /// distribute a private local config separately from the launcher release.
    /// </summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>
    /// The launch order token passed to <c>Bannerlord.exe /singleplayer</c>. The full mod set,
    /// minus RBM. Kept here so a mod-list change is a config edit, not a launcher rebuild.
    /// </summary>
    public string ModuleToken { get; set; } = CurrentModuleToken;

    /// <summary>
    /// Explicit launch exclusions, normally empty for the production compatibility suite.
    /// </summary>
    public string[] BlockedModuleIds { get; set; } = [];

    /// <summary>Optional explanation for an explicitly blocked local module.</summary>
    public string CompatibilityHoldNotice { get; set; } = "";

    internal string? GetBlockedModuleInToken()
    {
        string[] tokenModules = (ModuleToken ?? string.Empty)
            .Split('*', StringSplitOptions.RemoveEmptyEntries)
            .Where(module => !string.Equals(module, "_MODULES_", StringComparison.Ordinal))
            .ToArray();
        return (BlockedModuleIds ?? [])
            .FirstOrDefault(blocked => !string.IsNullOrWhiteSpace(blocked) &&
                tokenModules.Contains(blocked, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Optional explicit Bannerlord install root (the folder containing <c>bin\Win64_Shipping_Client</c>).
    /// Empty = auto-detect from the Steam library.
    /// </summary>
    public string GamePath { get; set; } = "";

    /// <summary>Bannerlord version the host accepts. The client feed may override this value.</summary>
    public string RequiredGameVersion { get; set; } = "1.4.8";

    /// <summary>
    /// Launcher executable update manifest. Empty disables launcher self-update. The stable rolling
    /// release is the default; a private config may opt into the distinct nightly feed.
    /// </summary>
    public string LauncherManifestUrl { get; set; } =
        "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/launcher.json";

    /// <summary>
    /// Update manifest URL (raw JSON — see <see cref="UpdateManifest"/>). Empty disables updates and
    /// the launcher just runs whatever is installed. Defaults to the public stable client feed;
    /// swap <c>client-stable</c> → <c>client-nightly</c> for bleeding edge.
    /// </summary>
    public string UpdateManifestUrl { get; set; } =
        "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/client-stable/launcher.json";

    /// <summary>
    /// Mod-suite manifest URL — the full non-base module set (frameworks + workshop mods) a friend
    /// needs, byte-exact for the join handshake. Large but changes rarely; installed before the
    /// co-op client. Empty disables suite installs (for machines that already have the mods).
    /// </summary>
    public string SuiteManifestUrl { get; set; } =
        "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/suite-stable/suite.json";

    /// <summary>
    /// Curated chronicle (player-facing changelog) feed. Empty = derive from
    /// <see cref="UpdateManifestUrl"/> by swapping the file name to <c>changelog.json</c>, so the
    /// chronicle rides the same release tag as the client feed with zero extra configuration.
    /// </summary>
    public string ChronicleUrl { get; set; } = "";

    /// <summary>Shown as a link in Options; the project this launcher belongs to.</summary>
    public string ProjectUrl { get; set; } =
        "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition";

    /// <summary>HTTPS portal endpoints for authoritative campaign stats and issue submission.</summary>
    public string PortalUrl { get; set; } = "";

    /// <summary>Public discovery document used when the Worker URL is assigned during deployment.</summary>
    public string PortalManifestUrl { get; set; } =
        "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/portal-config/portal.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static LauncherConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                LauncherConfig config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), Options)
                                        ?? new LauncherConfig();
                if (string.Equals(
                        config.ModuleToken,
                        LegacyModuleTokenBeforeGearAndDemographics,
                        StringComparison.Ordinal))
                {
                    config.ModuleToken = CurrentModuleToken;

                    // Persist it. The match above is ordinal and exact, so any hand-edit to this file
                    // — even whitespace — stops the migration firing and the launch silently drops to
                    // the pre-gear 16-module order, which then fails the join handshake. Writing it
                    // back makes the upgrade survive that. Best-effort: a read-only or locked config
                    // must never block a launch, and the in-memory value is already correct.
                    try { File.WriteAllText(path, JsonSerializer.Serialize(config, Options)); }
                    catch { }
                }
                return config;
            }
        }
        catch
        {
            // A corrupt config must never block the launch — fall back to baked defaults.
        }
        return new LauncherConfig();
    }
}

/// <summary>The remote build feed the launcher checks against the installed client on startup.</summary>
public sealed class UpdateManifest
{
    /// <summary>Monotonic build version, e.g. "2026.08.10.1". Newer-than-installed triggers a pull.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// Direct URL to the update ZIP. Mutually exclusive with <see cref="Parts"/>; retained for
    /// existing GitHub/R2 feeds and small client payloads.
    /// </summary>
    [JsonPropertyName("clientZipUrl")] public string ClientZipUrl { get; set; } = "";

    /// <summary>
    /// Ordered sub-2-GiB pieces of one ZIP. The launcher concatenates and verifies them before
    /// handing the reconstructed ZIP to the unchanged transactional installer.
    /// </summary>
    [JsonPropertyName("parts")] public UpdatePart[] Parts { get; set; } = [];

    /// <summary>SHA-256 of the zip, lowercase hex. Verified before anything is extracted.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>Optional one-line changelog surfaced under the update rail.</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>Exact Bannerlord version required by this client build.</summary>
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
}

public sealed class UpdatePart
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>The remote feed used to update the portable launcher executable itself.</summary>
public sealed record LauncherUpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("launcherUrl")] public string LauncherUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}
