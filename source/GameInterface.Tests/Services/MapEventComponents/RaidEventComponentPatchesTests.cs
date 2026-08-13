using Common.Util;
using GameInterface.Services.MapEventComponents.Patches;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEventComponents;

public class RaidEventComponentPatchesTests
{
    [Fact]
    public void CaptureItems_SumsModifierStacksForTheSameItem()
    {
        ItemObject item = ObjectHelper.SkipConstructor<ItemObject>();
        var roster = Roster(
            (item, ObjectHelper.SkipConstructor<ItemModifier>(), 2),
            (item, ObjectHelper.SkipConstructor<ItemModifier>(), 3));
        Assert.Equal(2, roster.Count);

        var snapshot = RaidEventComponentPatches.CaptureItems(roster);

        Assert.Equal(5, snapshot[item]);
    }

    [Fact]
    public void GetAddedItems_ReportsOneNetDeltaAcrossModifierStacks()
    {
        ItemObject item = ObjectHelper.SkipConstructor<ItemObject>();
        ItemModifier first = ObjectHelper.SkipConstructor<ItemModifier>();
        ItemModifier second = ObjectHelper.SkipConstructor<ItemModifier>();
        var beforeRoster = Roster(
            (item, first, 2),
            (item, second, 3));
        Assert.Equal(2, beforeRoster.Count);
        var before = RaidEventComponentPatches.CaptureItems(beforeRoster);
        var after = Roster(
            (item, first, 1),
            (item, second, 5));
        Assert.Equal(2, after.Count);

        var added = RaidEventComponentPatches.GetAddedItems(before, after);

        var delta = Assert.Single(added);
        Assert.Same(item, delta.Item);
        Assert.Equal(1, delta.Amount);
    }

    [Fact]
    public void GetAddedItems_DoesNotPublishAStackMoveOrNetDecrease()
    {
        ItemObject item = ObjectHelper.SkipConstructor<ItemObject>();
        ItemModifier first = ObjectHelper.SkipConstructor<ItemModifier>();
        ItemModifier second = ObjectHelper.SkipConstructor<ItemModifier>();
        var beforeRoster = Roster(
            (item, first, 4),
            (item, second, 3));
        Assert.Equal(2, beforeRoster.Count);
        var before = RaidEventComponentPatches.CaptureItems(beforeRoster);
        var after = Roster(
            (item, first, 1),
            (item, second, 6));

        Assert.Empty(RaidEventComponentPatches.GetAddedItems(before, after));

        after = Roster(
            (item, first, 1),
            (item, second, 5));
        Assert.Empty(RaidEventComponentPatches.GetAddedItems(before, after));
    }

    private static ItemRoster Roster(params (ItemObject Item, ItemModifier Modifier, int Amount)[] elements)
    {
        var roster = new ItemRoster();
        foreach (var element in elements)
        {
            roster.Add(new ItemRosterElement(
                new EquipmentElement(element.Item, element.Modifier),
                element.Amount));
        }

        return roster;
    }
}
