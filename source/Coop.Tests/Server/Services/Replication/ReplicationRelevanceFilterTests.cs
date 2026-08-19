using Common.Messaging;
using System;
using Xunit;
using Coop.Core.Server.Services.Replication;
using GameInterface.Utils.NetworkEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Tests.Server.Services.Replication;

/// <summary>
/// Covers which messages the relevance filter is willing to hold.
/// </summary>
/// <remarks>
/// This is the correctness-critical half of the filter. Holding is only safe for messages where the
/// newest one fully describes the state, because the filter keeps just the latest per object per
/// message type. A message describing a STEP — an add, a remove, a change at an index, a clear, a
/// dictionary upsert — must never be held, or the steps in between are lost. These pin that rule so a
/// future template or message cannot quietly opt into being dropped.
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
    private sealed record TownMarketData_itemDict_UpsertNetworkMessage : ScopedEvent;

    private sealed record PlainMessage : IEvent;

    private static bool IsHoldable(IMessage message) =>
        ReplicationRelevanceFilter.TryGetSubject(message, out _, out _, out _, out _);

    [Fact]
    public void AWholeMemberSetCanBeHeld()
    {
        // The newest set fully describes the member, so keeping only the latest loses nothing.
        Assert.True(IsHoldable(new Settlement_Prosperity_SetNetworkMessage()));
    }

    [Theory]
    [InlineData(typeof(Settlement_Parties_AddNetworkMessage))]
    [InlineData(typeof(Settlement_Parties_RemoveNetworkMessage))]
    [InlineData(typeof(Settlement_Items_ChangeNetworkMessage))]
    [InlineData(typeof(Settlement_Items_ClearNetworkMessage))]
    [InlineData(typeof(TownMarketData_itemDict_UpsertNetworkMessage))]
    public void StepwiseMessagesAreNeverHeld(Type messageType)
    {
        // Each of these describes a change rather than a state. Collapsing them to "the latest" would
        // silently discard the others — an add followed by a remove would arrive as just the remove,
        // and two upserts of different dictionary keys would arrive as only the second.
        var message = (IMessage)Activator.CreateInstance(messageType);

        Assert.False(IsHoldable(message));
    }

    [Fact]
    public void AMessageWithNoInstanceScopeIsNeverHeld()
    {
        // Nothing to locate means nothing to reason about; it goes out.
        Assert.False(IsHoldable(new PlainMessage()));
    }

    [Fact]
    public void ASetWithNoInstanceIdIsNeverHeld()
    {
        Assert.False(IsHoldable(new Settlement_Prosperity_SetNetworkMessage { InstanceId = "" }));
    }

    [Fact]
    public void ASetWithNoInstanceTypeIsNeverHeld()
    {
        Assert.False(IsHoldable(new Settlement_Prosperity_SetNetworkMessage { InstanceType = null }));
    }

    [Fact]
    public void TheSubjectIsReportedFromTheMessage()
    {
        bool holdable = ReplicationRelevanceFilter.TryGetSubject(
            new Settlement_Prosperity_SetNetworkMessage { InstanceId = "party7", InstanceType = typeof(Settlement) },
            out Type instanceType, out string instanceId, out _, out bool positionKnown);

        Assert.True(holdable);
        Assert.Equal(typeof(Settlement), instanceType);
        Assert.Equal("party7", instanceId);

        // A generated set carries no position, so the filter has to resolve the object to place it.
        Assert.False(positionKnown);
    }
}
