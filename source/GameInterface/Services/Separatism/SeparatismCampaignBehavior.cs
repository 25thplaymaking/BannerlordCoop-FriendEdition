using Common;
using GameInterface.Services.WorkshopMods.Core;
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
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
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

    private static void OnSessionLaunched(CampaignGameStarter starter)
    {
        if (!ModInformation.IsClient) return;

        // Separatism 1.3.8 targeted an old vanilla persuasion node that no longer exists in the
        // current game. Keep its original option, source node, wording and priority, but close the
        // conversation after dispatching the complete server-owned replacement flow.
        starter.AddPlayerLine(
            "player_is_requesting_fallen_to_join",
            "lord_talk_speak_diplomacy_2",
            "close_window",
            "{=Separatism_Clan_Recruit}{FIRST_NAME}, I have heard you are in search of a new sovereign...",
            CanOfferFallenClanRecruitment,
            RequestFallenClanRecruitment,
            100);
    }

    internal static bool CanOfferFallenClanRecruitment()
    {
        if (!ContainerProvider.TryResolve<IWorkshopCapabilityRegistry>(out var capabilities))
            return false;

        Hero actor = Hero.MainHero;
        Hero target = Hero.OneToOneConversationHero;
        Kingdom actorKingdom = actor?.Clan?.Kingdom;
        Clan targetClan = target?.Clan;
        bool canOffer = SeparatismConversationPolicy.CanOfferFallenClanRecruitment(
            capabilities.IsEnabled(
                SeparatismRecruitmentHandler.ModuleId,
                SeparatismRecruitmentHandler.Operation),
            actorKingdom != null,
            actorKingdom?.Leader == actor,
            target != null && targetClan != null,
            targetClan?.Kingdom == null,
            targetClan?.IsMinorFaction == false,
            target?.MapFaction?.Leader == target,
            target != null && actor != null &&
                !FactionManager.IsAtWarAgainstFaction(target.MapFaction, actor.MapFaction));
        if (canOffer) target.SetTextVariables();
        return canOffer;
    }

    internal static void RequestFallenClanRecruitment()
    {
        if (ContainerProvider.TryResolve<SeparatismRecruitmentHandler>(out var handler))
            handler.TryRequest(Hero.MainHero, Hero.OneToOneConversationHero);
    }

    private static void Run(Action<ISeparatismCampaignService> action)
    {
        if (!ModInformation.IsServer) return;
        if (ContainerProvider.TryResolve<ISeparatismCampaignService>(out var service))
        {
            action(service);
        }
    }
}
