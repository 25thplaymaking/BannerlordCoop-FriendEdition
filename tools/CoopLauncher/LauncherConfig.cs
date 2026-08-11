using System.IO;
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
    public string ModuleToken { get; set; } =
        "_MODULES_*Bannerlord.Harmony*Bannerlord.ButterLib*Bannerlord.UIExtenderEx*Bannerlord.MBOptionScreen" +
        "*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*PlayerSettlement*Coop*ImprovedGarrisons" +
        "*DismembermentPlus*Fourberie*Bannerlord.Diplomacy*UnblockableThrust*_MODULES_";

    /// <summary>
    /// Optional explicit Bannerlord install root (the folder containing <c>bin\Win64_Shipping_Client</c>).
    /// Empty = auto-detect from the Steam library.
    /// </summary>
    public string GamePath { get; set; } = "";

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
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), Options)
                       ?? new LauncherConfig();
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

    /// <summary>Direct URL to the client mod zip (the <c>Modules\Coop</c> payload).</summary>
    [JsonPropertyName("clientZipUrl")] public string ClientZipUrl { get; set; } = "";

    /// <summary>SHA-256 of the zip, lowercase hex. Verified before anything is extracted.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>Optional one-line changelog surfaced under the update rail.</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}
