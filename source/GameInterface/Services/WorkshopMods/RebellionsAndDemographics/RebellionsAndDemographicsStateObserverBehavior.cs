using System;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>Server-only observer registered after the original behavior allowlist.</summary>
internal sealed class RebellionsAndDemographicsStateObserverBehavior : CampaignBehaviorBase
{
    private readonly RebellionsAndDemographicsCompatibilityHandler handler;

    internal RebellionsAndDemographicsStateObserverBehavior(RebellionsAndDemographicsCompatibilityHandler handler) =>
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public override void RegisterEvents()
    {
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, handler.ObserveAuthoritativeState);
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => handler.ObserveAuthoritativeState());
    }

    public override void SyncData(IDataStore dataStore) { }
}
