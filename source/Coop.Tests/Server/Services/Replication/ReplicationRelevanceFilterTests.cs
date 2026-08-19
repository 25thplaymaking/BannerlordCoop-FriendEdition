using Common.Messaging;
using System;
using Xunit;
using Coop.Core.Server.Services.Replication;
using GameInterface.Utils.NetworkEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Tests.Server.Services.Replication;

/// <summary>
/// Covers which messages the relevance filter is willing to hold, and how it slots them.
/// </summary>
/// <remarks>
/// This is the correctness-critical half of the filter. Holding keeps only the newest message per
/// slot, so a shape may only be held when the newest one genuinely supersedes the previous one for
/// that slot. A message describing a STEP — an add, a remove, a change at an index, a clear — must
/// never be held, or the steps in between are lost. These pin that rule so a future template cannot
/// quietly opt into being dropped.
/// </remarks>
public class ReplicationRelevanceFilterTests
{
    private abstract record ScopedEvent : IEvent, IInstanceScopedNetworkEvent
    {
        public string InstanceId { get; init; } = "abc";
        public Type InstanceType { get; init; } = typeof(MobileParty);
    }

    // Names matter: the filter classifies generated messages by the suffix their template produces.
    private sealed record Settlement_Prosperity_SetNetworkMessage : ScopedEvent;
    private sealed record Settlement_Parties_AddNetworkMessage : ScopedEvent;
    private sealed record Settlement_Parties_RemoveNetworkMessage : ScopedEvent;
    private sealed record Settlement_Items_ChangeNetworkMessage : ScopedEvent;
    private sealed record Settlement_Items_ClearNetworkMessage : ScopedEvent;

    private sealed record TownMarketData_itemDict_UpsertNetworkMessage : ScopedEvent
    {
        public string Key { get; init; } = "grain";
    }

    private sealed record PlainMessage : IEvent;

    private static bool IsHoldable(IMessage message) =>
        ReplicationRelevanceFilter.TryGetHoldSlot(message) != null;

    [Fact]
    public void AWholeMemberSetCanBeHeld()
    {
        // The newest set fully describes the member, so keeping only the latest loses nothing.
        Assert.NotNull(ReplicationRelevanceFilter.TryGetHoldSlot(new Settlement_Prosperity_SetNetworkMessage()));
    }

    [Theory]
    [InlineData(typeof(Settlement_Parties_AddNetworkMessage))]
    [InlineData(typeof(Settlement_Parties_RemoveNetworkMessage))]
    [InlineData(typeof(Settlement_Items_ChangeNetworkMessage))]
    [InlineData(typeof(Settlement_Items_ClearNetworkMessage))]
    public void StepwiseMessagesAreNeverHeld(Type messageType)
    {
        // Each of these describes a change rather than a state. Collapsing them to "the latest" would
        // silently discard the others — an add followed by a remove would arrive as just the remove.
        var message = (IMessage)Activator.CreateInstance(messageType);

        Assert.False(IsHoldable(message));
    }

    [Fact]
    public void AMessageWithNoInstanceScopeIsNeverHeld()
    {
        Assert.False(IsHoldable(new PlainMessage()));
    }

    [Fact]
    public void UpsertsForDifferentKeysGetDifferentSlots()
    {
        // The whole point of keying by the dictionary key: two upserts for different items must both
        // survive. Sharing a slot would silently drop one of them.
        string grain = ReplicationRelevanceFilter.TryGetHoldSlot(
            new TownMarketData_itemDict_UpsertNetworkMessage { Key = "grain" });
        string iron = ReplicationRelevanceFilter.TryGetHoldSlot(
            new TownMarketData_itemDict_UpsertNetworkMessage { Key = "iron" });

        Assert.NotNull(grain);
        Assert.NotNull(iron);
        Assert.NotEqual(grain, iron);
    }

    [Fact]
    public void UpsertsForTheSameKeyShareASlot()
    {
        // And this is where the saving comes from: a market repricing the same item repeatedly
        // collapses to one message, which is exactly what an upsert means.
        string first = ReplicationRelevanceFilter.TryGetHoldSlot(
            new TownMarketData_itemDict_UpsertNetworkMessage { Key = "grain" });
        string second = ReplicationRelevanceFilter.TryGetHoldSlot(
            new TownMarketData_itemDict_UpsertNetworkMessage { Key = "grain" });

        Assert.Equal(first, second);
    }

    [Fact]
    public void ASetAndAnUpsertNeverShareASlot()
    {
        string set = ReplicationRelevanceFilter.TryGetHoldSlot(new Settlement_Prosperity_SetNetworkMessage());
        string upsert = ReplicationRelevanceFilter.TryGetHoldSlot(new TownMarketData_itemDict_UpsertNetworkMessage());

        Assert.NotEqual(set, upsert);
    }

    [Fact]
    public void TheSubjectIsReportedForHeldAndUnheldMessagesAlike()
    {
        // The subject is needed even for messages that are never held: the filter releases whatever it
        // is holding for that object before letting an unheld message past, so a held update can never
        // land after a later one.
        bool addHasSubject = ReplicationRelevanceFilter.TryGetSubject(
            new Settlement_Parties_AddNetworkMessage { InstanceId = "s1", InstanceType = typeof(Settlement) },
            out Type addType, out string addId, out _, out _);

        Assert.True(addHasSubject);
        Assert.Equal(typeof(Settlement), addType);
        Assert.Equal("s1", addId);
        Assert.False(IsHoldable(new Settlement_Parties_AddNetworkMessage()));
    }

    [Fact]
    public void ASetWithNoInstanceIdHasNoSubject()
    {
        Assert.False(ReplicationRelevanceFilter.TryGetSubject(
            new Settlement_Prosperity_SetNetworkMessage { InstanceId = "" }, out _, out _, out _, out _));
    }

    [Fact]
    public void ASetWithNoInstanceTypeHasNoSubject()
    {
        Assert.False(ReplicationRelevanceFilter.TryGetSubject(
            new Settlement_Prosperity_SetNetworkMessage { InstanceType = null }, out _, out _, out _, out _));
    }

    [Fact]
    public void AGeneratedSetCarriesNoPositionOfItsOwn()
    {
        ReplicationRelevanceFilter.TryGetSubject(
            new Settlement_Prosperity_SetNetworkMessage { InstanceType = typeof(Settlement) },
            out _, out _, out _, out bool positionKnown);

        // So the filter has to resolve the object to place it; only the party-behaviour snapshot
        // carries its own position.
        Assert.False(positionKnown);
    }
}
