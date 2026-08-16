using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoopLauncher;

/// <summary>
/// Per-user launcher preferences, persisted to
/// <c>%LocalAppData%\CalradiaCoop\launcher-settings.json</c> — deliberately NOT
/// <c>launcher-config.json</c>, which ships beside the exe and is replaced wholesale by launcher
/// updates. The shipped config stays the group admin's deployment surface; this file is the
/// member's. Precedence: a non-default value here overrides the shipped config.
/// </summary>
public sealed class LauncherSettings
{
    public const string StableChannel = "stable";
    public const string NightlyChannel = "nightly";

    /// <summary>Explicit Bannerlord install root chosen in Options; empty = use config/auto-detect.</summary>
    public string GamePathOverride { get; set; } = "";

    /// <summary>Per-component feed channel: "stable" (default) or "nightly".</summary>
    public string LauncherChannel { get; set; } = StableChannel;
    public string SuiteChannel { get; set; } = StableChannel;
    public string ClientChannel { get; set; } = StableChannel;

    /// <summary>
    /// Opt-in only. When set, <see cref="ProtectedPassword"/> holds the join password encrypted
    /// with DPAPI for the current Windows user; the default remains never-persisted.
    /// </summary>
    public bool RememberPassword { get; set; }
    public string ProtectedPassword { get; set; } = "";

    /// <summary>Close the launcher once Bannerlord has taken the handoff (the historical behavior).</summary>
    public bool CloseAfterLaunch { get; set; } = true;

    public bool VerboseLogging { get; set; }

    /// <summary>Anonymous installation id used only for report rate limiting; contains no account identity.</summary>
    public string ReportClientId { get; set; } = "";

    /// <summary>Most recently submitted crash bundle directory; prevents repeat prompts for the same crash.</summary>
    public string LastSubmittedCrashReport { get; set; } = "";

    public string GetOrCreateReportClientId()
    {
        if (!Guid.TryParse(ReportClientId, out _))
            ReportClientId = Guid.NewGuid().ToString("D");
        return ReportClientId;
    }

    // Fixed entropy so a copied settings file from another user profile fails closed instead of
    // decrypting to garbage silently. Not a secret; DPAPI's per-user key is the protection.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CalradiaCoop.JoinPassword.v1");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalradiaCoop", "launcher-settings.json");

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Options)
                       ?? new LauncherSettings();
        }
        catch
        {
            // Corrupt settings must never block the launch — fall back to defaults.
        }
        return new LauncherSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Preferences are conveniences; failing to persist them must not surface as a launch error.
        }
    }

    /// <summary>
    /// Produce the effective config: the shipped <paramref name="config"/> with this user's
    /// overrides applied. The shipped object is not mutated — the caller keeps one source of truth
    /// for "what the admin deployed" and one for "what this member chose".
    /// </summary>
    public LauncherConfig ApplyTo(LauncherConfig config)
    {
        var effective = new LauncherConfig
        {
            GroupName = config.GroupName,
            ServerHost = config.ServerHost,
            ServerPort = config.ServerPort,
            ServerPassword = config.ServerPassword,
            ModuleToken = config.ModuleToken,
            BlockedModuleIds = config.BlockedModuleIds,
            CompatibilityHoldNotice = config.CompatibilityHoldNotice,
            GamePath = string.IsNullOrWhiteSpace(GamePathOverride) ? config.GamePath : GamePathOverride,
            RequiredGameVersion = config.RequiredGameVersion,
            ProjectUrl = config.ProjectUrl,
            PortalUrl = config.PortalUrl,
            PortalManifestUrl = config.PortalManifestUrl,
            ChronicleUrl = config.ChronicleUrl,
            LauncherManifestUrl = RewriteChannel(config.LauncherManifestUrl, "launcher-app", "launcher-nightly", LauncherChannel),
            UpdateManifestUrl = RewriteChannel(config.UpdateManifestUrl, "client-stable", "client-nightly", ClientChannel),
            SuiteManifestUrl = RewriteChannel(config.SuiteManifestUrl, "suite-stable", "suite-nightly", SuiteChannel),
        };
        return effective;
    }

    /// <summary>
    /// Swap a feed URL between its stable and nightly release tags. Only the two known tokens are
    /// ever touched: a custom URL a group admin pointed somewhere else comes back unchanged, no
    /// matter what channel the member picked — channel choice must never mangle a bespoke feed.
    /// </summary>
    internal static string RewriteChannel(string url, string stableToken, string nightlyToken, string channel)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        bool wantNightly = string.Equals(channel, NightlyChannel, StringComparison.OrdinalIgnoreCase);
        string from = wantNightly ? stableToken : nightlyToken;
        string to = wantNightly ? nightlyToken : stableToken;
        int index = url.IndexOf($"/{from}/", StringComparison.Ordinal);
        if (index < 0) return url;
        return url.Substring(0, index + 1) + to + url.Substring(index + from.Length + 1);
    }

    /// <summary>Encrypt and store the join password for the current Windows user; clears on failure.</summary>
    public void ProtectPassword(string password)
    {
        try
        {
            ProtectedPassword = string.IsNullOrEmpty(password)
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            ProtectedPassword = "";
        }
    }

    /// <summary>Decrypt the stored join password; empty when unset, corrupted, or from another user.</summary>
    public string UnprotectPassword()
    {
        try
        {
            if (string.IsNullOrEmpty(ProtectedPassword)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
}
