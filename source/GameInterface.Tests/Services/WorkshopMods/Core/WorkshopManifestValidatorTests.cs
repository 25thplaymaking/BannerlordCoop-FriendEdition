using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public class WorkshopManifestValidatorTests
{
    private readonly WorkshopManifestValidator validator = new();

    [Fact]
    public void IdenticalPrivateSuite_Matches()
    {
        WorkshopManifestValidationResult result = validator.Validate(
            ManifestFactory.Create(WorkshopPeerRole.Server),
            ManifestFactory.Create(WorkshopPeerRole.Client));

        Assert.True(result.Matches, result.ToNetworkReason());
    }

    [Fact]
    public void UnmanagedCopy_IsRejectedEvenWhenHashesMatch()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true, managedDistributionComponent: module.ModuleId != "Fourberie"));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("unmanaged copy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reproduces the live join refusal: the host's Improved Garrisons drifted (its runtime error
    /// log was hashed), so the server advertised live hashes instead of its receipt pins. The
    /// player then saw two stacked reasons — "reinstall from the private suite" and "use the
    /// server package defaults" — neither of which they could act on, and which contradict each
    /// other. The unmanaged host must be named once, as the host's fault.
    /// </summary>
    [Fact]
    public void UnmanagedServer_ReportsTheHostOnce_WithoutCascadingHashRemedies()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(
            WorkshopPeerRole.Server,
            module => Copy(module,
                configurationHash: module.ModuleId == "ImprovedGarrisons" ? new string('c', 64) : null,
                managedDistributionComponent: module.ModuleId != "ImprovedGarrisons"));
        WorkshopCompatibilityManifest client = ManifestFactory.Create(WorkshopPeerRole.Client);

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Equal(1, result.Diagnostics.Count(diagnostic => diagnostic.Contains("ImprovedGarrisons")));

        string diagnostic = result.Diagnostics.Single(value => value.Contains("ImprovedGarrisons"));
        Assert.Contains("unmanaged copy", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("host must", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Configuration mismatch", result.ToNetworkReason(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The suppression above is scoped to an unmanaged peer. Two receipt-backed peers that
    /// genuinely disagree on package bytes must still be refused with the hash diagnostic.
    /// </summary>
    [Fact]
    public void ManagedPeers_StillReportConfigurationMismatch()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true,
                configurationHash: module.ModuleId == "ImprovedGarrisons" ? new string('c', 64) : null));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("Configuration mismatch for 'ImprovedGarrisons'", StringComparison.Ordinal));
    }

    [Fact]
    public void ClientPresentation_CodeIsVerified_ButCosmeticConfigMayDiffer()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest cosmeticDifference = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true,
                configurationHash: module.ModuleId == "DismembermentPlus" ? new string('f', 64) : null));

        Assert.True(validator.Validate(server, cosmeticDifference).Matches);

        WorkshopCompatibilityManifest codeDifference = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true,
                contentHash: module.ModuleId == "DismembermentPlus" ? new string('f', 64) : null));

        WorkshopManifestValidationResult codeResult = validator.Validate(server, codeDifference);
        Assert.False(codeResult.Matches);
        Assert.Contains(codeResult.Diagnostics, diagnostic => diagnostic.Contains("DismembermentPlus"));
    }

    [Fact]
    public void TwoClients_MismatchDoesNotPoisonCompatibleLateJoin()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest mismatchedClient = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true,
                contentHash: module.ModuleId == "Fourberie" ? new string('f', 64) : null));

        WorkshopManifestValidationResult first = validator.Validate(server, mismatchedClient);
        WorkshopManifestValidationResult lateJoin = validator.Validate(
            server,
            ManifestFactory.Create(WorkshopPeerRole.Client));

        Assert.False(first.Matches);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Contains("Fourberie"));
        Assert.True(lateJoin.Matches, lateJoin.ToNetworkReason());
    }

    [Fact]
    public void MissingCampaignComponent_HasActionableDiagnostic()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest fullClient = ManifestFactory.Create(WorkshopPeerRole.Client);
        var client = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Client,
            fullClient.Entries.Where(entry => entry.ModuleId != "PlayerSettlement"));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("missing 'PlayerSettlement'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WrongActivationOrder_IsRejectedWithActionableDiagnostic()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => new WorkshopCompatibilityManifestEntry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                module.Role,
                module.Profile,
                ManifestFactory.StableHash(module.ModuleId, 'a'),
                ManifestFactory.StableHash(module.ModuleId, 'b'),
                managedDistributionComponent: true,
                activationOrderValid: module.ModuleId != "PlayerSettlement",
                loadOrder: module.LoadOrder,
                active: module.FeatureActiveExpectedOnClient));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("load order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WrongPinnedNumericLoadOrder_IsRejected()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => ManifestFactory.Entry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                ManifestFactory.StableHash(module.ModuleId, 'a'),
                ManifestFactory.StableHash(module.ModuleId, 'b'),
                module.Role,
                module.Profile,
                loadOrder: module.ModuleId == "UnblockableThrust" ? module.LoadOrder + 1 : module.LoadOrder,
                active: module.FeatureActiveExpectedOnClient));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("load order", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Contains("UnblockableThrust", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogActivationPolicy_IsAcceptedOnBothRoles()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(WorkshopPeerRole.Client);

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.True(result.Matches, result.ToNetworkReason());
        Assert.Empty(result.Warnings);

        // Per-role activation: each side's entries match that role's catalog expectation.
        var catalog = new FriendEditionWorkshopModuleCatalog();
        foreach (WorkshopCompatibilityManifestEntry entry in server.Entries)
        {
            Assert.True(catalog.TryGet(entry.ModuleId, out WorkshopModuleExpectation expectation));
            Assert.Equal(expectation.FeatureActiveExpectedOnServer, entry.Active);
        }
        foreach (WorkshopCompatibilityManifestEntry entry in client.Entries)
        {
            Assert.True(catalog.TryGet(entry.ModuleId, out WorkshopModuleExpectation expectation));
            Assert.Equal(expectation.FeatureActiveExpectedOnClient, entry.Active);
        }
        Assert.Equal(14, client.Entries.Length);
        Assert.DoesNotContain(client.Entries, entry => entry.ModuleId == "RBM");
        Assert.All(server.Entries, entry => Assert.True(entry.Active));
        Assert.All(client.Entries, entry => Assert.True(entry.Active));
    }

    /// <summary>
    /// Every retained module is required on both roles; deactivating one side is refused rather
    /// than silently allowing the runtime contract to diverge.
    /// </summary>
    [Fact]
    public void RequiredModuleDeactivatedOnServer_IsRejected()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(
            WorkshopPeerRole.Server,
            module => Copy(module, active: module.ModuleId == "Fourberie"
                ? false
                : module.FeatureActiveExpectedOnServer));
        WorkshopCompatibilityManifest client = ManifestFactory.Create(WorkshopPeerRole.Client);

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("Server must activate 'Fourberie'", StringComparison.Ordinal));
    }

    [Fact]
    public void InactiveRequiredClientPackage_IsRejected()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true, active: module.ModuleId != "UnblockableThrust"));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("Client must activate 'UnblockableThrust'", StringComparison.Ordinal));
    }

    [Fact]
    public void InactiveCanonicalHarmony_IsRejectedOnEitherRole()
    {
        WorkshopCompatibilityManifest inactiveServer = ManifestFactory.Create(
            WorkshopPeerRole.Server,
            module => Copy(module, forClient: true, active: false));
        WorkshopCompatibilityManifest inactiveClient = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, forClient: true, active: false));

        WorkshopManifestValidationResult serverResult = validator.Validate(
            inactiveServer,
            ManifestFactory.Create(WorkshopPeerRole.Client));
        WorkshopManifestValidationResult clientResult = validator.Validate(
            ManifestFactory.Create(WorkshopPeerRole.Server),
            inactiveClient);

        Assert.False(serverResult.Matches);
        Assert.Contains(serverResult.Diagnostics, diagnostic =>
            diagnostic.Contains("Server must activate 'Bannerlord.Harmony'", StringComparison.Ordinal));
        Assert.False(clientResult.Matches);
        Assert.Contains(clientResult.Diagnostics, diagnostic =>
            diagnostic.Contains("Client must activate 'Bannerlord.Harmony'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Bannerlord only resolves ACTIVE modules, so a staged-but-inactive component can never
    /// appear in either manifest. A module absent from BOTH manifests has no bytes in either
    /// runtime, so agreement is a warning, never a refusal — otherwise no session could ever run
    /// a subset (e.g. during a future mod retirement). One-sided absence stays fatal (covered
    /// above).
    /// </summary>
    [Fact]
    public void ModuleAbsentFromBothSides_WarnsButMatches()
    {
        var harmonyOnlyServer = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Server,
            ManifestFactory.Create(WorkshopPeerRole.Server).Entries
                .Where(entry => entry.ModuleId == "Bannerlord.Harmony"));
        var harmonyOnlyClient = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Client,
            ManifestFactory.Create(WorkshopPeerRole.Client).Entries
                .Where(entry => entry.ModuleId == "Bannerlord.Harmony"));

        WorkshopManifestValidationResult result = validator.Validate(harmonyOnlyServer, harmonyOnlyClient);

        Assert.True(result.Matches, result.ToNetworkReason());
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("not active on either side", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ApplicationVersion.ToString() turns a three-part version into four parts, so an active
    /// module legitimately reports "v4.3.4.0" where the catalog pins "v4.3.4". That is the same
    /// version, not a mismatch.
    /// </summary>
    [Fact]
    public void NormalizedFourPartVersion_MatchesThreePartPin()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => new WorkshopCompatibilityManifestEntry(
                module.ModuleId,
                module.WorkshopId,
                module.ModuleId == "Bannerlord.Diplomacy" ? "v1.4.7.0" : module.Version,
                module.Role,
                module.Profile,
                ManifestFactory.StableHash(module.ModuleId, 'a'),
                ManifestFactory.StableHash(module.ModuleId, 'b'),
                managedDistributionComponent: true,
                activationOrderValid: true,
                loadOrder: module.LoadOrder,
                active: module.FeatureActiveExpectedOnClient));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.True(result.Matches, result.ToNetworkReason());
    }

    private static WorkshopCompatibilityManifestEntry Copy(
        WorkshopModuleExpectation module,
        string contentHash = null,
        string configurationHash = null,
        bool managedDistributionComponent = true,
        bool? active = null,
        bool forClient = false) =>
        ManifestFactory.Entry(
            module.ModuleId,
            module.WorkshopId,
            module.Version,
            contentHash ?? ManifestFactory.StableHash(module.ModuleId, 'a'),
            configurationHash ?? ManifestFactory.StableHash(module.ModuleId, 'b'),
            module.Role,
            module.Profile,
            managedDistributionComponent,
            module.LoadOrder,
            active ?? (forClient
                ? module.FeatureActiveExpectedOnClient
                : module.FeatureActiveExpectedOnServer));
}
