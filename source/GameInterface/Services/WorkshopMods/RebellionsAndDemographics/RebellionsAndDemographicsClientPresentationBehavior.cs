using Common;
using GameInterface.Services.WorkshopMods.Core;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>
/// Rendered-peer replacement for the upstream funding dialog.  It contains no costs, save data,
/// or campaign mutation: each option is a typed request and the host recalculates the exact
/// upstream cost/pool predicates immediately before committing.
/// </summary>
internal sealed class RebellionsAndDemographicsClientPresentationBehavior : CampaignBehaviorBase
{
    public override void RegisterEvents() =>
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, AddFundingDialog);

    public override void SyncData(IDataStore dataStore) { }

    private static void AddFundingDialog(CampaignGameStarter starter)
    {
        starter.AddPlayerLine("coop_rd_fund_start", "hero_main_options", "coop_rd_fund_options",
            "{=str_turmoil_intro}I hear whispers of discontent. Your King is weak.", CanFundTarget, null, 100);
        starter.AddDialogLine("coop_rd_fund_reply", "coop_rd_fund_options", "coop_rd_fund_options",
            "{=str_turmoil_reply}Careful... But go on. What do you propose?", null, null, 100, null);
        AddFundingOption(starter, 3, "Gather a Council (3 Clans)");
        AddFundingOption(starter, 4, "Form an Alliance (4 Clans)");
        AddFundingOption(starter, 5, "Civil War (5 Clans)");
        starter.AddPlayerLine("coop_rd_fund_cancel", "coop_rd_fund_options", "close_window",
            "{=str_turmoil_dialog_cancel}Never mind.", null, null, 100);
        starter.AddPlayerLine("coop_rd_demographics_report", "hero_main_options", "hero_main_options",
            "Show the demographic report.", CanShowReport, ShowReport, 99);
    }

    private static void AddFundingOption(CampaignGameStarter starter, int allyCount, string text) =>
        starter.AddPlayerLine("coop_rd_fund_" + allyCount, "coop_rd_fund_options", "close_window", text,
            CanFundTarget, () => Request(allyCount), 100);

    private static bool CanFundTarget()
    {
        if (!ContainerProvider.TryResolve<IWorkshopCapabilityRegistry>(out var capabilities) ||
            !capabilities.IsEnabled(RebellionsAndDemographicsCapabilitySource.ModuleId,
                RebellionsAndDemographicsCapabilitySource.Operation)) return false;
        Hero actor = Hero.MainHero;
        Hero target = Hero.OneToOneConversationHero;
        return actor?.Clan?.Kingdom?.Leader == actor && target?.Clan?.Kingdom != null &&
            target.Clan.Kingdom != actor.Clan.Kingdom && target.Clan.Kingdom.RulingClan != target.Clan &&
            target.Clan.Leader == target;
    }

    private static void Request(int allyCount)
    {
        if (ContainerProvider.TryResolve<RebellionsAndDemographicsCompatibilityHandler>(out var handler))
            handler.TryRequestPlayerIntervention(Hero.OneToOneConversationHero, allyCount);
    }

    private static bool CanShowReport() =>
        ContainerProvider.TryResolve<RebellionsAndDemographicsCompatibilityHandler>(out var handler) &&
        handler.CurrentState != null && handler.SnapshotReadiness == WorkshopSnapshotReadiness.Ready;

    private static void ShowReport()
    {
        if (!ContainerProvider.TryResolve<RebellionsAndDemographicsCompatibilityHandler>(out var handler) ||
            handler.CurrentState == null) return;
        int population = handler.CurrentState.Settlements.Sum(settlement => settlement.TotalPopulation);
        string plague = string.IsNullOrEmpty(handler.CurrentState.Plague?.SettlementId)
            ? "no active plague" : "plague at " + handler.CurrentState.Plague.SettlementId +
                " (" + handler.CurrentState.Plague.DaysLeft + " days)";
        InformationManager.DisplayMessage(new InformationMessage("Demographics: " + handler.CurrentState.Settlements.Length +
            " settlements, population " + population + ", " + plague + "."));
    }
}
