using Common.Util;
using Helpers;
using GameInterface.Services.WorkshopMods.Fourberie;
using HarmonyLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieSafehouseItemTransferTests
{
    private const string SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Protocol_AcceptsBoundedSignedSafehouseItemDeltas()
    {
        var request = Request(
            new FourberieItemSelection("grain", string.Empty, 4),
            new FourberieItemSelection("sword", "modifier_a", -2));

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
    }

    [Fact]
    public void Protocol_RejectsZeroDuplicateOversizedAndCrossOperationItemDeltas()
    {
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(
            new FourberieItemSelection("grain", string.Empty, 0))));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(
            new FourberieItemSelection("grain", string.Empty, 1),
            new FourberieItemSelection("grain", string.Empty, -1))));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(
            new FourberieItemSelection("grain", string.Empty, FourberieOperationProtocol.MaxSelectedItems + 1))));

        var crossOperation = new NetworkRequestFourberieOperation(
            SessionId,
            1,
            0,
            FourberieOperation.EnslavePrisoners,
            "hideout_a",
            string.Empty,
            string.Empty,
            0,
            new[] { new FourberieTroopSelection("looter", 1) },
            new[] { new FourberieItemSelection("grain", string.Empty, 1) });
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(crossOperation));
    }

    [Fact]
    public void CommandKey_CoversModifierAndSignedDelta()
    {
        string baseline = FourberieOperationProtocol.CommandKey(Request(
            new FourberieItemSelection("sword", "modifier_a", 2)));
        string differentModifier = FourberieOperationProtocol.CommandKey(Request(
            new FourberieItemSelection("sword", "modifier_b", 2)));
        string differentDirection = FourberieOperationProtocol.CommandKey(Request(
            new FourberieItemSelection("sword", "modifier_a", -2)));

        Assert.NotEqual(baseline, differentModifier);
        Assert.NotEqual(baseline, differentDirection);
    }

    [Fact]
    public void Protocol_ProtobufRoundTripPreservesItemSelections()
    {
        NetworkRequestFourberieOperation request = Request(
            new FourberieItemSelection("sword", "modifier_a", -2));
        using var stream = new MemoryStream();

        Serializer.Serialize(stream, request);
        stream.Position = 0;
        NetworkRequestFourberieOperation restored =
            Serializer.Deserialize<NetworkRequestFourberieOperation>(stream);

        FourberieItemSelection item = Assert.Single(restored.Items);
        Assert.Equal("sword", item.ItemId);
        Assert.Equal("modifier_a", item.ItemModifierId);
        Assert.Equal(-2, item.DeltaToSafehouse);
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(restored));
    }

    [Fact]
    public void LocalSelectionBuilder_NetsDepositsAgainstWithdrawalsByEquipmentElement()
    {
        var grain = new EquipmentElement(new ItemObject());
        var sword = new EquipmentElement(new ItemObject());
        var bought = new List<(ItemRosterElement, int)>
        {
            (new ItemRosterElement(grain, 2), 0),
            (new ItemRosterElement(sword, 1), 0),
        };
        var sold = new List<(ItemRosterElement, int)>
        {
            (new ItemRosterElement(grain, 5), 0),
        };

        FourberieLocalItemSelection[] selections =
            FourberieSafehouseTransferContext.BuildSelections(bought, sold);

        Assert.Collection(
            selections,
            selection =>
            {
                Assert.Equal(grain, selection.EquipmentElement);
                Assert.Equal(3, selection.DeltaToSafehouse);
            },
            selection =>
            {
                Assert.Equal(sword, selection.EquipmentElement);
                Assert.Equal(-1, selection.DeltaToSafehouse);
            });
    }

    [Fact]
    public void AuthorityAvailability_RejectsEitherSourceOverdrawBeforeMutation()
    {
        var grain = new EquipmentElement(new ItemObject());
        var sword = new EquipmentElement(new ItemObject());
        var selections = new[] { (grain, 4), (sword, -2) };

        Assert.True(FourberieSafehouseItemTransferAuthority.CanApply(
            selections,
            equipment => equipment.Equals(grain) ? 4 : 0,
            equipment => equipment.Equals(sword) ? 2 : 0,
            out var validFailure), validFailure);
        Assert.False(FourberieSafehouseItemTransferAuthority.CanApply(
            selections,
            _ => 3,
            _ => 2,
            out var playerFailure));
        Assert.Contains("player", playerFailure, StringComparison.OrdinalIgnoreCase);
        Assert.False(FourberieSafehouseItemTransferAuthority.CanApply(
            selections,
            _ => 4,
            _ => 1,
            out var safehouseFailure));
        Assert.Contains("safehouse", safehouseFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplicationGuard_FailsClosedWhenAnAllowedThreadWouldSilenceRosterUpdates()
    {
        FourberieReplicationGuard.EnsureEnabled();

        using (new AllowedThread())
            Assert.Throws<InvalidOperationException>(FourberieReplicationGuard.EnsureEnabled);
    }

    [Fact]
    public void HarmonyTargets_IncludeThisBannerlordBuildsStashOpenAndCloseMethods()
    {
        Assert.NotNull(AccessTools.Method(
            typeof(InventoryScreenHelper),
            nameof(InventoryScreenHelper.OpenScreenAsStash),
            new[] { typeof(ItemRoster) }));
        Assert.NotNull(AccessTools.Method(
            typeof(InventoryScreenHelper),
            nameof(InventoryScreenHelper.CloseScreen),
            new[] { typeof(bool) }));
    }

    private static NetworkRequestFourberieOperation Request(params FourberieItemSelection[] items) =>
        new(
            SessionId,
            1,
            0,
            FourberieOperation.TransferSafehouseItems,
            "hideout_a",
            string.Empty,
            string.Empty,
            0,
            Array.Empty<FourberieTroopSelection>(),
            items);
}
