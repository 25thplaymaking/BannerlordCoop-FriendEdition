using Common.Messaging;
using System;
using TaleWorlds.CampaignSystem.Siege;
using static TaleWorlds.CampaignSystem.Siege.SiegeEvent;

namespace GameInterface.Services.SiegeEngines.Messages;

/// <summary>Local-only finalizer signal for an incomplete native container mutation.</summary>
public readonly struct SiegeEngineContainerMutationFailed : IEvent
{
    public readonly SiegeEnginesContainer Container;
    public readonly int Index;
    public readonly bool IsRanged;
    public readonly Exception Exception;

    public SiegeEngineContainerMutationFailed(
        SiegeEnginesContainer container,
        int index,
        bool isRanged,
        Exception exception)
    {
        Container = container;
        Index = index;
        IsRanged = isRanged;
        Exception = exception;
    }
}
