using Common;
using GameInterface.Services.WorkshopMods.PlayerSettlement;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.PlayerSettlement;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PlayerSettlementConstructionTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly IPlayerSettlementPatchRuntime previousRuntime = PlayerSettlementPatchRuntime.Current;

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
        PlayerSettlementPatchRuntime.Current = previousRuntime;
    }

    [Fact]
    public void CompleteConstructionSurface_RoutesAllFiveCreatorCommits()
    {
        var commits = PlayerSettlementCompatibilityManifest.Methods
            .Where(spec => spec.Kind == PlayerSettlementPatchKind.ConstructionCommit)
            .ToArray();

        Assert.Equal(5, commits.Length);
        Assert.Contains(commits, spec => spec.MethodName.Contains("BuildTown"));
        Assert.Contains(commits, spec => spec.MethodName.Contains("BuildCastle"));
        Assert.Contains(commits, spec => spec.MethodName.Contains("BuildVillageFor"));
        Assert.Contains(commits, spec => spec.MethodName.Contains("Rebuild"));
        Assert.Contains(commits, spec => spec.MethodName.Contains("Overwrite"));
        Assert.All(commits, spec => Assert.Contains("ApplyPlaced", spec.MethodName));

        var reachableUi = PlayerSettlementCompatibilityManifest.Methods
            .Where(spec => new[]
            {
                "BuildTown", "BuildCastle", "BuildVillage", "BuildVillageFor", "Rebuild",
                "Overwrite", "ExecuteCreatePlayerSettlement",
            }.Contains(spec.MethodName))
            .ToArray();
        Assert.Equal(7, reachableUi.Length);
        Assert.All(reachableUi, spec => Assert.Equal(
            PlayerSettlementPatchKind.ClientPresentation, spec.Kind));
    }

    [Fact]
    public void ConstructionCommit_RunsOriginalOnlyOnHostAndSubmitsOnceOnClient()
    {
        var runtime = new RecordingRuntime();
        PlayerSettlementPatchRuntime.Current = runtime;
        var method = typeof(PlayerSettlementConstructionTests).GetMethod(
            nameof(ConstructionCommit_RunsOriginalOnlyOnHostAndSubmitsOnceOnClient))!;

        ModInformation.IsServer = false;
        Assert.False(PlayerSettlementAuthorityPatches.ConstructionCommitPrefix(
            new object(), method, Array.Empty<object>()));
        Assert.Equal(1, runtime.Submissions);

        ModInformation.IsServer = true;
        Assert.True(PlayerSettlementAuthorityPatches.ConstructionCommitPrefix(
            new object(), method, Array.Empty<object>()));
        Assert.Equal(1, runtime.Submissions);
    }

    [Theory]
    [InlineData(1, "", "", "")]
    [InlineData(2, "", "", "")]
    [InlineData(3, "", "bound", "grain")]
    [InlineData(4, "target", "", "")]
    [InlineData(5, "target", "", "")]
    public void AuthenticatedConstructionProtocol_CoversEveryOperationShape(
        int operationValue,
        string target,
        string bound,
        string villageType)
    {
        var operation = (PlayerSettlementConstructionOperation)operationValue;
        var request = Request(operation, target, bound, villageType, Frame(1f));
        Assert.True(PlayerSettlementConstructionProtocol.TryValidate(request, out var failure), failure);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkRequestPlayerSettlementConstruction>(stream);
        Assert.True(PlayerSettlementConstructionProtocol.TryValidate(copy, out failure), failure);
        Assert.Equal(
            PlayerSettlementConstructionProtocol.CommandKey(request),
            PlayerSettlementConstructionProtocol.CommandKey(copy));
    }

    [Fact]
    public void PlacementIntent_PreservesExactFloatBitsAndRejectsNonFiniteOrWarSailsFields()
    {
        var frame = Frame(-0.0f);
        var request = Request(
            PlayerSettlementConstructionOperation.BuildTown, "", "", "", frame);
        Assert.True(PlayerSettlementConstructionProtocol.TryValidate(request, out var failure), failure);
        Assert.Equal(BitConverter.ToInt32(BitConverter.GetBytes(-0.0f), 0),
            request.SettlementFrame.Bits[0]);

        var invalidBits = Enumerable.Repeat(0, PlayerSettlementBitTransform.FrameBitCount).ToArray();
        invalidBits[4] = BitConverter.ToInt32(BitConverter.GetBytes(float.NaN), 0);
        Assert.False(PlayerSettlementConstructionProtocol.TryValidate(
            Request(PlayerSettlementConstructionOperation.BuildTown, "", "", "",
                new PlayerSettlementBitTransform(invalidBits)), out _));

        Assert.DoesNotContain(
            typeof(NetworkRequestPlayerSettlementConstruction).GetProperties(),
            property => property.Name.Contains("Port", StringComparison.OrdinalIgnoreCase));
    }

    private static NetworkRequestPlayerSettlementConstruction Request(
        PlayerSettlementConstructionOperation operation,
        string target,
        string bound,
        string villageType,
        PlayerSettlementBitTransform frame) =>
        new NetworkRequestPlayerSettlementConstruction(
            requestId: 7,
            expectedRevision: 3,
            operation,
            target,
            bound,
            boundTargetId: string.Empty,
            settlementName: "Coop Settlement",
            cultureId: "empire",
            templateId: "empire_town_a",
            villageType,
            villageNumber: operation == PlayerSettlementConstructionOperation.BuildVillage ? 0 : -1,
            settlementFrame: frame,
            gateFrame: null,
            deepEdits: Array.Empty<PlayerSettlementDeepEditIntent>());

    private static PlayerSettlementBitTransform Frame(float first)
    {
        var bits = Enumerable.Repeat(BitConverter.ToInt32(BitConverter.GetBytes(1f), 0),
            PlayerSettlementBitTransform.FrameBitCount).ToArray();
        bits[0] = BitConverter.ToInt32(BitConverter.GetBytes(first), 0);
        return new PlayerSettlementBitTransform(bits);
    }

    private sealed class RecordingRuntime : IPlayerSettlementPatchRuntime
    {
        public int Submissions { get; private set; }
        public void NotifyFeatureBlocked(string method) { }
        public void AddBehavior(object campaignGameStarter) { }
        public void ValidateObjectRegistration(bool isSavedCampaign) { }
        public bool TrySubmitConstruction(object owner, MethodBase original, object[] arguments)
        {
            Submissions++;
            return true;
        }
    }
}
