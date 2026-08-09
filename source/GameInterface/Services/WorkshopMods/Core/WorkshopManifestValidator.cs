using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

public sealed class WorkshopManifestValidationResult
{
    private const int MaximumDiagnostics = 12;

    public WorkshopManifestValidationResult(
        IEnumerable<string> diagnostics,
        IEnumerable<string> warnings = null)
    {
        Diagnostics = (diagnostics ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(MaximumDiagnostics)
            .ToArray();
        Warnings = (warnings ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(MaximumDiagnostics)
            .ToArray();
    }

    public bool Matches => Diagnostics.Count == 0;
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<string> Warnings { get; }

    public string ToNetworkReason(int maximumLength = 2048)
    {
        string reason = string.Join(Environment.NewLine, Diagnostics);
        if (reason.Length <= maximumLength) return reason;
        return reason.Substring(0, Math.Max(0, maximumLength - 3)) + "...";
    }

    public string ToNetworkWarning(int maximumLength = 1024)
    {
        string warning = string.Join(Environment.NewLine, Warnings);
        if (warning.Length <= maximumLength) return warning;
        return warning.Substring(0, Math.Max(0, maximumLength - 3)) + "...";
    }
}

public interface IWorkshopManifestValidator
{
    WorkshopManifestValidationResult Validate(
        WorkshopCompatibilityManifest serverManifest,
        WorkshopCompatibilityManifest clientManifest);
}

public sealed class WorkshopManifestValidator : IWorkshopManifestValidator
{
    private readonly IWorkshopModuleCatalog catalog;

    public WorkshopManifestValidator()
        : this(new FriendEditionWorkshopModuleCatalog())
    {
    }

    public WorkshopManifestValidator(IWorkshopModuleCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public WorkshopManifestValidationResult Validate(
        WorkshopCompatibilityManifest serverManifest,
        WorkshopCompatibilityManifest clientManifest)
    {
        var diagnostics = new List<string>();
        var warnings = new List<string>();
        ValidateShape(serverManifest, WorkshopPeerRole.Server, "Server", diagnostics);
        ValidateShape(clientManifest, WorkshopPeerRole.Client, "Client", diagnostics);
        if (diagnostics.Count > 0) return new WorkshopManifestValidationResult(diagnostics, warnings);

        var serverEntries = serverManifest.Entries.ToDictionary(
            entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase);
        var clientEntries = clientManifest.Entries.ToDictionary(
            entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase);

        foreach (var expected in catalog.Modules)
        {
            bool serverPresent = serverEntries.TryGetValue(expected.ModuleId, out var server);
            bool clientPresent = clientEntries.TryGetValue(expected.ModuleId, out var client);

            // Even a client-presentation component that is intentionally inactive on a headless
            // server must be staged there: its receipt-backed digest is the trusted value clients
            // are compared against.
            if (!serverPresent)
                diagnostics.Add($"Server package is missing '{expected.ModuleId}' {expected.Version}.");
            // The private suite always stages all eleven exact components on both roles. Feature
            // activation is a separate decision: guarded originals may remain inactive without
            // weakening package/content agreement.
            if (!clientPresent)
                diagnostics.Add($"Client package is missing '{expected.ModuleId}' {expected.Version}.");

            if (serverPresent) ValidateIdentity("Server", server, expected, diagnostics);
            if (clientPresent) ValidateIdentity("Client", client, expected, diagnostics);
            if (serverPresent)
                ValidateActivation("Server", server, expected.FeatureActiveExpectedOnServer, diagnostics);
            if (clientPresent)
                ValidateActivation("Client", client, expected.FeatureActiveExpectedOnClient, diagnostics);
            if (!serverPresent || !clientPresent) continue;

            if (!EqualHash(server.ContentSha256, client.ContentSha256))
                diagnostics.Add($"Content mismatch for '{expected.ModuleId}'. Reinstall the Friend Edition package.");

            // Presentation settings are intentionally local; its code/assets remain exact, while
            // cosmetic preferences may differ without affecting campaign or mission authority.
            if (expected.Profile != WorkshopCompatibilityProfile.ClientPresentation &&
                !EqualHash(server.ConfigurationSha256, client.ConfigurationSha256))
            {
                diagnostics.Add($"Configuration mismatch for '{expected.ModuleId}'. Use the server package defaults.");
            }
        }

        var expectedIds = new HashSet<string>(catalog.Modules.Select(module => module.ModuleId),
            StringComparer.OrdinalIgnoreCase);
        foreach (string unexpected in serverEntries.Keys.Concat(clientEntries.Keys)
                     .Where(id => !expectedIds.Contains(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add($"Unexpected managed Workshop component '{unexpected}'.");
        }

        return new WorkshopManifestValidationResult(diagnostics, warnings);
    }

    private static void ValidateShape(
        WorkshopCompatibilityManifest manifest,
        WorkshopPeerRole expectedRole,
        string source,
        ICollection<string> diagnostics)
    {
        if (manifest == null)
        {
            diagnostics.Add($"{source} did not provide the required Friend Edition Workshop manifest.");
            return;
        }

        if (!manifest.TryValidateWireShape(out string error))
        {
            diagnostics.Add($"{source} Workshop manifest is invalid: {error}");
            return;
        }

        if (manifest.PeerRole != expectedRole)
            diagnostics.Add($"{source} Workshop manifest has the wrong peer role.");
    }

    private static void ValidateIdentity(
        string source,
        WorkshopCompatibilityManifestEntry actual,
        WorkshopModuleExpectation expected,
        ICollection<string> diagnostics)
    {
        if (!string.Equals(actual.WorkshopId, expected.WorkshopId, StringComparison.Ordinal))
            diagnostics.Add($"{source} has the wrong Workshop item for '{expected.ModuleId}'.");
        if (!string.Equals(actual.Version, expected.Version, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add($"{source} has '{expected.ModuleId}' {actual.Version}; expected {expected.Version}.");
        if (actual.Role != expected.Role || actual.Profile != expected.Profile)
            diagnostics.Add($"{source} has an invalid compatibility profile for '{expected.ModuleId}'.");
        if (actual.LoadOrder != expected.LoadOrder)
            diagnostics.Add($"{source} has load order {actual.LoadOrder} for '{expected.ModuleId}'; expected {expected.LoadOrder}.");
        if (!actual.ManagedDistributionComponent)
            diagnostics.Add($"{source} loads an unmanaged copy of '{expected.ModuleId}'. Reinstall it from the Friend Edition private suite.");
        if (!actual.ActivationOrderValid)
            diagnostics.Add($"{source} has the Friend Edition Workshop modules in the wrong load order.");
    }

    private static void ValidateActivation(
        string source,
        WorkshopCompatibilityManifestEntry actual,
        bool expectedActive,
        ICollection<string> diagnostics)
    {
        if (actual.Active == expectedActive) return;

        if (expectedActive)
        {
            diagnostics.Add($"{source} must activate '{actual.ModuleId}' before joining the Friend Edition session.");
            return;
        }

        diagnostics.Add(
            $"{source} must keep '{actual.ModuleId}' inactive under the current Friend Edition safe activation policy.");
    }

    private static bool EqualHash(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
