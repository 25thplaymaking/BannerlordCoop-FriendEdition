using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Core;

public sealed class WorkshopSuiteReceiptEntry
{
    [JsonProperty("moduleId")]
    public string ModuleId { get; set; }
    [JsonProperty("workshopId")]
    public string WorkshopId { get; set; }
    [JsonProperty("steamManifestId")]
    public string SteamManifestId { get; set; }
    [JsonProperty("version")]
    public string Version { get; set; }
    [JsonProperty("loadOrder")]
    public int LoadOrder { get; set; }
    [JsonProperty("contentSha256")]
    public string ContentSha256 { get; set; }
    [JsonProperty("configurationSha256")]
    public string ConfigurationSha256 { get; set; }
}

public sealed class WorkshopSuiteReceipt
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumReceiptBytes = 256 * 1024;
    public const string SuiteId = "friend-edition-private-workshop-suite";

    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; }
    [JsonProperty("suiteId")]
    public string Id { get; set; }
    [JsonProperty("moduleCount")]
    public int ModuleCount { get; set; }
    [JsonProperty("receiptSha256")]
    public string ReceiptSha256 { get; set; }
    [JsonProperty("modules")]
    public WorkshopSuiteReceiptEntry[] Modules { get; set; }

    public bool TryValidate(IWorkshopModuleCatalog catalog, out string error)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        if (SchemaVersion != CurrentSchemaVersion || !string.Equals(Id, SuiteId, StringComparison.Ordinal))
        {
            error = "The Workshop suite receipt has an unsupported identity or schema.";
            return false;
        }

        if (Modules == null || ModuleCount != Modules.Length || ModuleCount != catalog.Modules.Count ||
            ModuleCount > WorkshopCompatibilityManifest.MaximumEntries)
        {
            error = "The Workshop suite receipt has the wrong module count.";
            return false;
        }

        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in Modules)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.ModuleId) ||
                !moduleIds.Add(module.ModuleId) ||
                !catalog.TryGet(module.ModuleId, out WorkshopModuleExpectation expected) ||
                !string.Equals(module.WorkshopId, expected.WorkshopId, StringComparison.Ordinal) ||
                !string.Equals(module.SteamManifestId, expected.SteamManifestId, StringComparison.Ordinal) ||
                !string.Equals(module.Version, expected.Version, StringComparison.OrdinalIgnoreCase) ||
                module.LoadOrder != expected.LoadOrder ||
                !IsSha256(module.ContentSha256) || !IsSha256(module.ConfigurationSha256))
            {
                error = "The Workshop suite receipt contains an invalid or unpinned module record.";
                return false;
            }
        }

        if (!IsSha256(ReceiptSha256) ||
            !string.Equals(ReceiptSha256, ComputeDigest(Modules), StringComparison.OrdinalIgnoreCase))
        {
            error = "The Workshop suite receipt digest is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    public bool TryGet(string moduleId, out WorkshopSuiteReceiptEntry entry)
    {
        entry = Modules?.FirstOrDefault(module =>
            string.Equals(module.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
        return entry != null;
    }

    public static string ComputeDigest(IEnumerable<WorkshopSuiteReceiptEntry> modules)
    {
        string canonical = string.Join("\n", (modules ?? Enumerable.Empty<WorkshopSuiteReceiptEntry>())
            .OrderBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Select(module => string.Join("|",
                module.ModuleId.ToLowerInvariant(),
                module.WorkshopId,
                module.SteamManifestId,
                module.Version,
                module.LoadOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
                module.ContentSha256.ToLowerInvariant(),
                module.ConfigurationSha256.ToLowerInvariant())));
        using var sha = SHA256.Create();
        return WorkshopCompatibilityManifest.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != WorkshopCompatibilityManifest.Sha256HexLength) return false;
        return value.All(character => character >= '0' && character <= '9' ||
                                      character >= 'a' && character <= 'f' ||
                                      character >= 'A' && character <= 'F');
    }
}

public interface IWorkshopSuiteReceiptProvider
{
    WorkshopSuiteReceipt Load(string coopModuleRoot);
}

public sealed class WorkshopSuiteReceiptProvider : IWorkshopSuiteReceiptProvider
{
    public const string RelativeReceiptPath = "WorkshopSuite/MANIFEST.json";

    private readonly IWorkshopModuleCatalog catalog;

    public WorkshopSuiteReceiptProvider(IWorkshopModuleCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public WorkshopSuiteReceipt Load(string coopModuleRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(coopModuleRoot)) return null;
            string receiptPath = Path.Combine(coopModuleRoot, "WorkshopSuite", "MANIFEST.json");
            if (!File.Exists(receiptPath)) return null;

            var info = new FileInfo(receiptPath);
            if (info.Length <= 0 || info.Length > WorkshopSuiteReceipt.MaximumReceiptBytes) return null;

            WorkshopSuiteReceipt receipt =
                JsonConvert.DeserializeObject<WorkshopSuiteReceipt>(File.ReadAllText(receiptPath));
            return receipt != null && receipt.TryValidate(catalog, out _) ? receipt : null;
        }
        catch (Exception exception) when (
            exception is JsonException ||
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is ArgumentException ||
            exception is NotSupportedException)
        {
            return null;
        }
    }
}
