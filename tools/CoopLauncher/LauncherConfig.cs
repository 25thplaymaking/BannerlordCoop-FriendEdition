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
        "*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*Coop" +
        "*Europe1100*Europe1100Expanded*SnowballingKingdoms - EOE 1100*_MODULES_";

    // PlayerSettlement is absent on purpose: it generates settlement prefabs from the native
    // culture set, which EoE 1100 replaces, and it took the dedicated host down with a native
    // fault on the first EoE boot. Its catalog entry is held inactive to match.

    // The Empires of Europe 1100 conversion follows the load order its author publishes: Harmony,
    // the TaleWorlds modules, everything else, then EoE 1100 last so its map, cultures, settlements
    // and troop trees win every XML merge.
    //
    // RBM is NOT here, and shipping it data-only does not work either. Its combat-parameter XML is
    // applied by the engine whether or not RBM.SubModule loads, and that is the exact native path
    // it was retired for on 2026-08-11: the dedicated host died with exit 84 immediately after
    // "Combat parameter overriden: 40 -> dagger_right_leftstance". Denying its submodule on the
    // headless allowlist does not protect the host, because the data alone reaches the fault.
    // EoE 1100 carries its own armour/troop rebalance and RF_BattleAI for the 1100 setting.
    //
    // gfrontsEOENamesMod is deliberately excluded: its SubModule.xml declares SPCultures XmlNames
    // under ModuleData/gfront55_names, but the package ships only .xslt transforms and no .xml, so
    // it contributes nothing and only risks an XML-load fault.

    // The production token shipped before the Empires of Europe 1100 conversion was added. Migrate
    // it the same way as the pre-gear token below so an existing install picks up the new order.
    internal const string LegacyModuleTokenBeforeEuropeConversion =
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
    public string GroupName { get; set; } = "Europe 1100 Co-op";

    /// <summary>Co-op host the launcher joins and probes for the online banner.</summary>
    public string ServerHost { get; set; } = "205.209.116.114";
    public int ServerPort { get; set; } = 4200;

    /// <summary>
    /// Group-private join token. The public build deliberately leaves this empty; group admins
    /// distribute a private local config separately from the launcher release.
    /// </summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>
    /// The launch order token passed to <c>Bannerlord.exe /singleplayer</c>. The full mod set.
    /// Kept here so a mod-list change is a config edit, not a launcher rebuild.
    /// </summary>
    public string ModuleToken { get; set; } = CurrentModuleToken;

    /// <summary>
    /// Explicit launch exclusions, normally empty for the production compatibility suite.
    /// </summary>
    public string[] BlockedModuleIds { get; set; } = [];

    /// <summary>Optional explanation for an explicitly blocked local module.</summary>
    public string CompatibilityHoldNotice { get; set; } = "";

    /// <summary>
    /// Move the conversion's precompiled shader cache aside so the base game's complete shader
    /// pipeline is used instead.
    /// </summary>
    /// <remarks>
    /// Europe 1100 ships a 979 MB <c>compressed_shader_cache.sack</c> that is INCOMPLETE for the
    /// deferred render path: its own compile report carries 411 <c>pbr_metallic</c> references and
    /// no <c>pbr_metallic_gbuffer</c>, no <c>pbr_terrain</c> and no <c>pbr_cloth</c> variants. The
    /// engine misses the sack and compiles the variant at runtime, mid-frame, exactly as a garment
    /// comes into view — which is what players see as clothing snapping or tearing at a certain
    /// distance, with a hitch attached.
    /// <para>
    /// The sack is renamed, never deleted, so this is reversible by hand. It is also a config flag
    /// rather than a hard-coded behaviour specifically so it can be switched off through the
    /// published launcher config WITHOUT a launcher rebuild if it turns out to cost more in
    /// first-load shader compilation than it saves in hitching.
    /// </para>
    /// <para>
    /// Safe with respect to the join handshake: the conversion modules are deliberately
    /// uncatalogued, so no receipt content or configuration hash covers their <c>Shaders</c>
    /// directory.
    /// </para>
    /// <para>
    /// DEFAULT CHANGED TO FALSE, 2026-08-19. This shipped to fix models tearing through the ground,
    /// and re-reading the client logs shows it cannot have been the cause: the missing-variant
    /// entries appear hours BEFORE the cache was ever renamed aside, so the artifact predates it.
    /// The one variant family that misses in bulk — <c>pbr_terrain</c>, 2,112 times — never compiles
    /// at runtime either, because the BASE game ships its own <c>compressed_shader_cache.sack</c>
    /// that satisfies it; those misses are the conversion's cache being consulted first and are
    /// harmless. What the conversion's cache genuinely provides is 72,024 precompiled variants for
    /// its own materials, and renaming it aside throws those away in exchange for runtime
    /// compilation — which is a CAUSE of hitching, not a cure.
    /// </para>
    /// <para>
    /// So this is off by default: it modifies a 979 MB game file on a player's disk to fix something
    /// it demonstrably does not fix. The flag stays so it can still be A/B'd from the published
    /// config without a rebuild, and <c>ConversionBootstrap</c> still restores a cache it renamed
    /// previously when the flag is off.
    /// </para>
    /// </remarks>
    public bool NeutralizeConversionShaderCache { get; set; } = false;

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
                        StringComparison.Ordinal) ||
                    string.Equals(
                        config.ModuleToken,
                        LegacyModuleTokenBeforeEuropeConversion,
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
