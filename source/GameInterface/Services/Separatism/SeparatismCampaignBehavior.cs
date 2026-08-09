using Common;
using System;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Separatism;

/// <summary>
/// Thin campaign-event adapter. Decisions are deliberately never evaluated on clients: the
/// server service performs each mutation once and the normal Coop sync services replicate it.
/// </summary>
public sealed class SeparatismCampaignBehavior : CampaignBehaviorBase
{
    public override void RegisterEvents()
    {
        CampaignEvents.OnNewGameCreatedPartialFollowUpEndEvent.AddNonSerializedListener(this, OnNewGameCreated);
        CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, OnGameLoaded);
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        CampaignEvents.DailyTickClanEvent.AddNonSerializedListener(this, OnDailyTickClan);
    }

    public override void SyncData(IDataStore dataStore)
    {
        // Separatism intentionally owns no save data. The resulting kingdoms, clans, wars and
        // settlement ownership are native campaign state and travel in the Coop save/snapshot.
    }

    private static void OnNewGameCreated(CampaignGameStarter _) => Run(service => service.OnNewGameCreated());
    private static void OnGameLoaded(CampaignGameStarter _) => Run(service => service.OnGameLoaded());
    private static void OnDailyTick() => Run(service => service.OnDailyTick());
    private static void OnDailyTickClan(Clan clan) => Run(service => service.OnDailyTickClan(clan));

    private static void Run(Action<ISeparatismCampaignService> action)
    {
        if (!ModInformation.IsServer) return;
        if (ContainerProvider.TryResolve<ISeparatismCampaignService>(out var service))
        {
            action(service);
        }
    }
}
