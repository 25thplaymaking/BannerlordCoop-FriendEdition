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
            module => Copy(module, managedDistributionComponent: module.ModuleId != "RBM"));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("unmanaged copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClientPresentation_CodeIsVerified_ButCosmeticConfigMayDiffer()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest cosmeticDifference = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module,
                configurationHash: module.ModuleId == "DismembermentPlus" ? new string('f', 64) : null));

        Assert.True(validator.Validate(server, cosmeticDifference).Matches);

        WorkshopCompatibilityManifest codeDifference = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module,
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
            module => Copy(module,
                contentHash: module.ModuleId == "RBM" ? new string('f', 64) : null));

        WorkshopManifestValidationResult first = validator.Validate(server, mismatchedClient);
        WorkshopManifestValidationResult lateJoin = validator.Validate(
            server,
            ManifestFactory.Create(WorkshopPeerRole.Client));

        Assert.False(first.Matches);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Contains("RBM"));
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
                activationOrderValid: module.ModuleId != "RBM",
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
                loadOrder: module.ModuleId == "RBM" ? module.LoadOrder + 1 : module.LoadOrder,
                active: module.FeatureActiveExpectedOnClient));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("load order", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Contains("RBM", StringComparison.Ordinal));
    }

    [Fact]
    public void SafeStagedInactivePackages_AreAcceptedOnBothRoles()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(WorkshopPeerRole.Client);

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.True(result.Matches, result.ToNetworkReason());
        Assert.Empty(result.Warnings);
        Assert.Single(server.Entries.Where(entry => entry.Active));
        Assert.Single(client.Entries.Where(entry => entry.Active));
        Assert.All(server.Entries.Where(entry => entry.ModuleId != "Bannerlord.Harmony"),
            entry => Assert.False(entry.Active));
        Assert.All(client.Entries.Where(entry => entry.ModuleId != "Bannerlord.Harmony"),
            entry => Assert.False(entry.Active));
    }

    [Fact]
    public void OptionalActiveServerPackage_IsRejected()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(
            WorkshopPeerRole.Server,
            module => Copy(module, active: module.ModuleId is "Bannerlord.Harmony" or "RBM"));
        WorkshopCompatibilityManifest client = ManifestFactory.Create(WorkshopPeerRole.Client);

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("Server must keep 'RBM' inactive", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalActiveClientPackage_IsRejected()
    {
        WorkshopCompatibilityManifest server = ManifestFactory.Create(WorkshopPeerRole.Server);
        WorkshopCompatibilityManifest client = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, active: module.ModuleId is "Bannerlord.Harmony" or "RBM"));

        WorkshopManifestValidationResult result = validator.Validate(server, client);

        Assert.False(result.Matches);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("Client must keep 'RBM' inactive", StringComparison.Ordinal));
    }

    [Fact]
    public void InactiveCanonicalHarmony_IsRejectedOnEitherRole()
    {
        WorkshopCompatibilityManifest inactiveServer = ManifestFactory.Create(
            WorkshopPeerRole.Server,
            module => Copy(module, active: false));
        WorkshopCompatibilityManifest inactiveClient = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => Copy(module, active: false));

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

    private static WorkshopCompatibilityManifestEntry Copy(
        WorkshopModuleExpectation module,
        string contentHash = null,
        string configurationHash = null,
        bool managedDistributionComponent = true,
        bool? active = null) =>
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
            active ?? module.FeatureActiveExpectedOnServer);
}
