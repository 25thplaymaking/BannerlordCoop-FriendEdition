using GameInterface.Services.WorkshopMods.PlayerSettlement;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.PlayerSettlement;

public sealed class PlayerSettlementRebuildTests
{
    [Fact]
    public void RebuildAndOverwrite_RequireStableTargetsAndRejectCrossOperationFields()
    {
        foreach (var operation in new[]
                 {
                     PlayerSettlementConstructionOperation.Rebuild,
                     PlayerSettlementConstructionOperation.Overwrite,
                 })
        {
            Assert.True(PlayerSettlementConstructionProtocol.TryValidate(
                Request(operation, "settlement_a", "", ""), out var failure), failure);
            Assert.False(PlayerSettlementConstructionProtocol.TryValidate(
                Request(operation, "", "", ""), out _));
            Assert.False(PlayerSettlementConstructionProtocol.TryValidate(
                Request(operation, "settlement_a", "bound", "grain"), out _));
        }
    }

    [Fact]
    public void DeepEditTransaction_IsBoundedAndRequiresCompleteFiniteTransforms()
    {
        var complete = new PlayerSettlementDeepEditIntent(
            index: 4,
            name: "wall",
            isDeleted: false,
            transform: Transform(PlayerSettlementBitTransform.DeepEditBitCount));
        Assert.True(PlayerSettlementConstructionProtocol.TryValidate(
            Request(PlayerSettlementConstructionOperation.Rebuild, "settlement_a", "", "", complete),
            out var failure), failure);

        var incomplete = new PlayerSettlementDeepEditIntent(
            4, "wall", false, Transform(PlayerSettlementBitTransform.DeepEditBitCount - 1));
        Assert.False(PlayerSettlementConstructionProtocol.TryValidate(
            Request(PlayerSettlementConstructionOperation.Rebuild, "settlement_a", "", "", incomplete),
            out _));

        var deleted = new PlayerSettlementDeepEditIntent(4, "wall", true, null);
        Assert.True(PlayerSettlementConstructionProtocol.TryValidate(
            Request(PlayerSettlementConstructionOperation.Rebuild, "settlement_a", "", "", deleted),
            out failure), failure);
    }

    private static NetworkRequestPlayerSettlementConstruction Request(
        PlayerSettlementConstructionOperation operation,
        string target,
        string bound,
        string villageType,
        params PlayerSettlementDeepEditIntent[] edits) =>
        new NetworkRequestPlayerSettlementConstruction(
            1, 0, operation, target, bound, string.Empty, "Settlement", "empire", "template",
            villageType, -1, Transform(PlayerSettlementBitTransform.FrameBitCount), null, edits);

    private static PlayerSettlementBitTransform Transform(int count) =>
        new PlayerSettlementBitTransform(Enumerable.Repeat(
            BitConverter.ToInt32(BitConverter.GetBytes(1f), 0), count));
}

