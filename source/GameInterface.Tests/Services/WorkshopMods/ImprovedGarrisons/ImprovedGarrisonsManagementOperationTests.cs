using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
using ProtoBuf;
using System.IO;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.ImprovedGarrisons;

public sealed class ImprovedGarrisonsManagementOperationTests
{
    [Fact]
    public void CompleteManagementSurface_HasNoDeniedOperations()
    {
        Assert.DoesNotContain(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.Kind.ToString() == "DeniedClientUi");

        Assert.Contains(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.MethodName == "PromptTemplateManager" &&
                      method.Kind == ImprovedGarrisonsPatchKind.ClientPresentation);
        Assert.Contains(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.MethodName == "PromptCreateMobileGarrison" &&
                      method.Kind == ImprovedGarrisonsPatchKind.RoutedOperation);
        Assert.Contains(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.MethodName == "ToggleAutoGuards" &&
                      method.Kind == ImprovedGarrisonsPatchKind.RoutedSetting);
        Assert.Contains(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.MethodName == "OrderMobileGarrisonAttackOrDefend" &&
                      method.Kind == ImprovedGarrisonsPatchKind.RoutedOperation);
    }

    [Fact]
    public void ManagementOperation_RoundTripsAuthenticatedConcurrencyAndStableSelections()
    {
        var request = new NetworkRequestImprovedGarrisonsOperation(
            sessionId: "9d80f6d4ae2a4f7db912829fd7a3a284",
            requestId: 71,
            expectedRevision: 9,
            operation: ImprovedGarrisonsOperation.CreateGlobalTemplate,
            townId: "town-source",
            targetIds: null,
            value: "all",
            troops: new[] { new ImprovedGarrisonsTroopSelection("aserai_recruit", 25) });

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkRequestImprovedGarrisonsOperation>(stream);

        Assert.Equal(request.SessionId, copy.SessionId);
        Assert.Equal(request.RequestId, copy.RequestId);
        Assert.Equal(request.ExpectedRevision, copy.ExpectedRevision);
        Assert.Equal(request.Operation, copy.Operation);
        Assert.Equal(request.TownId, copy.TownId);
        Assert.Empty(copy.TargetIds);
        Assert.Equal("aserai_recruit", Assert.Single(copy.Troops).TroopId);
        Assert.True(ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(copy));
    }

    [Fact]
    public void ManagementOperation_RejectsOversizedDuplicateAndInvalidSelections()
    {
        var duplicateTargets = Request(
            ImprovedGarrisonsOperation.CopySettings,
            new[] { "town-a", "town-a" },
            new[] { new ImprovedGarrisonsTroopSelection("troop-a", 1) });
        var duplicateTroops = Request(
            ImprovedGarrisonsOperation.ReplaceTownTemplate,
            new[] { "town-a" },
            new[]
            {
                new ImprovedGarrisonsTroopSelection("troop-a", 1),
                new ImprovedGarrisonsTroopSelection("troop-a", 2),
            });
        var invalidCount = Request(
            ImprovedGarrisonsOperation.ReplaceTownTemplate,
            new[] { "town-a" },
            new[] { new ImprovedGarrisonsTroopSelection("troop-a", -1) });

        Assert.False(ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(duplicateTargets));
        Assert.False(ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(duplicateTroops));
        Assert.False(ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(invalidCount));
    }

    [Fact]
    public void CanonicalCopy_ReplacesOnlySelectedOwnedTownScopes()
    {
        var values = new[]
        {
            Value("town", "source", "EnableTraining", "true"),
            Value("town", "source", "Template.Name", "source-template"),
            Value("town", "target", "EnableTraining", "false"),
            Value("town", "target", "Template.Name", "target-template"),
            Value("config", "", "DailyEXPAmount", "20"),
        };
        var request = new NetworkRequestImprovedGarrisonsOperation(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            1,
            0,
            ImprovedGarrisonsOperation.CopySettings,
            "source",
            new[] { "target" },
            string.Empty,
            null);

        Assert.True(ImprovedGarrisonsCanonicalOperations.TryTransform(
            values,
            request,
            new[] { "source", "target" },
            out var transformed,
            out var failure), failure);
        Assert.Equal("true", transformed.Single(value =>
            value.Scope == "town" && value.TargetId == "target" &&
            value.Property == "EnableTraining").Value);
        Assert.Equal("source-template", transformed.Single(value =>
            value.Scope == "town" && value.TargetId == "target" &&
            value.Property == "Template.Name").Value);
        Assert.Equal("20", transformed.Single(value => value.Scope == "config").Value);
    }

    [Fact]
    public void CanonicalTemplateReplacement_UsesStableTroopIdsAndCounts()
    {
        var values = new[]
        {
            Value("town", "town-a", "EnableTraining", "true"),
            Value("town", "town-a", "Template.Name", "old"),
            Value("town", "town-a", "Template.Troop:b2xk", "4"),
        };
        var request = new NetworkRequestImprovedGarrisonsOperation(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            2,
            0,
            ImprovedGarrisonsOperation.ReplaceTownTemplate,
            "town-a",
            null,
            "new-template",
            new[]
            {
                new ImprovedGarrisonsTroopSelection("troop-a", 12),
                new ImprovedGarrisonsTroopSelection("troop-b", 3),
            });

        Assert.True(ImprovedGarrisonsCanonicalOperations.TryTransform(
            values,
            request,
            new[] { "town-a" },
            out var transformed,
            out var failure), failure);
        Assert.Equal("new-template", transformed.Single(value =>
            value.Scope == "town" && value.Property == "Template.Name").Value);
        Assert.Equal(2, transformed.Count(value =>
            value.Scope == "town" && value.Property.StartsWith("Template.Troop:")));
    }

    [Fact]
    public void CanonicalRecruiterReturn_DisablesAutoSpawnBeforeOrderingThePartyHome()
    {
        var values = new[]
        {
            Value("town", "town-a", "RecruiterAutoSpawn", "True"),
            Value("town", "town-a", "EnableRecruitment", "True"),
        };
        var request = new NetworkRequestImprovedGarrisonsOperation(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            3,
            0,
            ImprovedGarrisonsOperation.ReturnRecruiter,
            "town-a",
            null,
            string.Empty,
            null);

        Assert.True(ImprovedGarrisonsCanonicalOperations.TryTransform(
            values,
            request,
            new[] { "town-a" },
            out var transformed,
            out var failure), failure);
        Assert.Equal("False", transformed.Single(value =>
            value.Scope == "town" && value.Property == "RecruiterAutoSpawn").Value);
        Assert.Equal("True", transformed.Single(value =>
            value.Scope == "town" && value.Property == "EnableRecruitment").Value);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Capability_RequiresHostOptionAndCompatibleRoute(
        bool optionEnabled,
        bool routeReady,
        bool expected)
    {
        Assert.Equal(expected, ImprovedGarrisonsCapabilityPolicy.IsEnabled(optionEnabled, routeReady));
    }

    private static NetworkRequestImprovedGarrisonsOperation Request(
        ImprovedGarrisonsOperation operation,
        string[] targets,
        ImprovedGarrisonsTroopSelection[] troops) =>
        new(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            1,
            0,
            operation,
            "town-source",
            targets,
            string.Empty,
            troops);

    private static ImprovedGarrisonsStateValue Value(
        string scope,
        string target,
        string property,
        string value) =>
        new(scope, target, property, value);
}
