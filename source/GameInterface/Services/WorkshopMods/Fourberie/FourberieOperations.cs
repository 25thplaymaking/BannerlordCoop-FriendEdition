using Common;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Barters;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieReplicationGuard
{
    public static void EnsureEnabled()
    {
        if (AllowedThread.IsThisThreadAllowed())
            throw new InvalidOperationException(
                "authoritative Fourberie roster mutation was attempted while Coop replication was suppressed");
    }
}

internal static class FourberieSafehouseItemTransferAuthority
{
    public static bool CanApply(
        IEnumerable<(EquipmentElement Equipment, int Delta)> selections,
        Func<EquipmentElement, int> playerCount,
        Func<EquipmentElement, int> safehouseCount,
        out string failure)
    {
        failure = null;
        if (selections == null || playerCount == null || safehouseCount == null)
        {
            failure = "safehouse transfer context is unavailable";
            return false;
        }

        bool any = false;
        foreach ((EquipmentElement equipment, int delta) in selections)
        {
            any = true;
            if (equipment.Item == null || delta == 0)
            {
                failure = "safehouse transfer contains an invalid item delta";
                return false;
            }
            int required = (int)Math.Min(int.MaxValue, Math.Abs((long)delta));
            if (delta > 0 && playerCount(equipment) < required)
            {
                failure = "player item roster changed before the safehouse transfer";
                return false;
            }
            if (delta < 0 && safehouseCount(equipment) < required)
            {
                failure = "safehouse item roster changed before the transfer";
                return false;
            }
        }

        if (!any)
        {
            failure = "no safehouse items were selected";
            return false;
        }
        return true;
    }
}

internal sealed class FourberieOperationExecutor
{
    private sealed class GrudgeQuote
    {
        public GrudgeQuote(string clanId, int grudge, int amount)
        {
            ClanId = clanId;
            Grudge = grudge;
            Amount = amount;
        }

        public string ClanId { get; }
        public int Grudge { get; }
        public int Amount { get; }
    }

    private const string BehaviorTypeName = "Fourberie.FourberieBehavior";
    private readonly Assembly assembly;
    private readonly IObjectManager objectManager;
    private readonly Dictionary<string, GrudgeQuote> grudgeQuotes = new Dictionary<string, GrudgeQuote>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> stealthVictims =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public FourberieOperationExecutor(Assembly assembly, IObjectManager objectManager)
    {
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.objectManager = objectManager ?? throw new ArgumentNullException(nameof(objectManager));
    }

    public bool TryExecute(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request,
        out string failure,
        out int resultValue)
    {
        failure = null;
        resultValue = 0;
        if (actor == null || actorParty == null || request == null ||
            actor.PartyBelongedTo != actorParty || actorParty.LeaderHero != actor)
        {
            failure = "authenticated controller has no matching active party";
            return false;
        }

        if (!FourberieCanonicalState.TryCapture(
                assembly, objectManager, out var rollbackState, out _, out failure))
            return false;

        MobileParty sourceParty = null;
        Dictionary<CharacterObject, int> sourceCounts = null;
        Dictionary<CharacterObject, int> actorCounts = null;
        Dictionary<CharacterObject, int> actorPrisonCounts = null;
        ItemRosterElement[] actorItems = null;
        ItemRosterElement[] safehouseItems = null;
        MobileParty previousCaravan = GetStaticField("_insucaraF") as MobileParty;
        MobileParty previousBandits = GetStaticField("_insubandF") as MobileParty;
        MobileParty previousAgents = GetStaticField("_agentsParty") as MobileParty;
        bool previousAgentsWasActive = previousAgents?.IsActive == true;
        Dictionary<CharacterObject, int> previousAgentCounts = CaptureAllCounts(previousAgents?.MemberRoster);
        MobileParty previousCrimeBase = GetStaticField("_crimeBaseParty") as MobileParty;
        bool previousCrimeBaseWasActive = previousCrimeBase?.IsActive == true;
        int previousActorGold = actor.Gold;
        int previousActorHitPoints = actor.HitPoints;
        Hero previousTransferTarget = null;
        int previousTransferTargetGold = 0;
        MobileParty banditTargetParty = null;
        Dictionary<CharacterObject, int> banditTargetCounts = null;
        Dictionary<CharacterObject, int> banditTargetPrisonCounts = null;
        ItemRosterElement[] banditStashItems = null;

        try
        {
            switch (request.Operation)
            {
                case FourberieOperation.EnlistAgentsFromParty:
                    sourceParty = actorParty;
                    sourceCounts = CaptureCounts(sourceParty.MemberRoster, request.Troops);
                    ApplyEnlistment(actorParty, sourceParty, request, requireCurrentSettlement: false);
                    break;
                case FourberieOperation.EnlistAgentsFromLads:
                    sourceParty = GetStaticField("_crimeBaseParty") as MobileParty;
                    if (sourceParty == null || !sourceParty.IsActive)
                        throw new InvalidOperationException("the Fourberie base party is unavailable");
                    sourceCounts = CaptureCounts(sourceParty.MemberRoster, request.Troops);
                    ApplyEnlistment(actorParty, sourceParty, request, requireCurrentSettlement: true);
                    break;
                case FourberieOperation.RecruitBandits:
                    actorCounts = CaptureCounts(actorParty.MemberRoster, request.Troops);
                    ApplyBanditRecruitment(actor, actorParty, request);
                    break;
                case FourberieOperation.EnslavePrisoners:
                    actorPrisonCounts = CaptureCounts(actorParty.PrisonRoster, request.Troops);
                    actorItems = CaptureAllItems(actorParty.ItemRoster);
                    ApplyPrisonerEnslavement(actor, actorParty, request);
                    break;
                case FourberieOperation.RecruitFightClubStable:
                    actorCounts = CaptureCounts(actorParty.MemberRoster, request.Troops);
                    actorPrisonCounts = CaptureCounts(actorParty.PrisonRoster, request.Troops);
                    ApplyFightClubStableRecruitment(actorParty, request);
                    break;
                case FourberieOperation.RefreshFightClubMenu:
                    ApplyFightClubMenuRefresh(actor, actorParty, request.SettlementId);
                    break;
                case FourberieOperation.TransferSafehouseItems:
                    actorItems = CaptureAllItems(actorParty.ItemRoster);
                    safehouseItems = CaptureAllItems(previousCrimeBase?.ItemRoster);
                    ApplySafehouseItemTransfer(actorParty, request);
                    break;
                case FourberieOperation.StartInsuranceScam:
                    ApplyInsuranceScam(actor, actorParty, request);
                    break;
                case FourberieOperation.StartCriminalBusiness:
                    ApplyBusinessStart(actor, request.IntValue);
                    break;
                case FourberieOperation.UpgradeCriminalBusiness:
                    ApplyBusinessUpgrade(request.IntValue);
                    break;
                case FourberieOperation.DowngradeCriminalBusiness:
                    ApplyBusinessDowngrade(request.IntValue);
                    break;
                case FourberieOperation.UpgradeSchemeBonus:
                case FourberieOperation.DowngradeSchemeBonus:
                case FourberieOperation.ResetSchemeBonus:
                    ApplySchemeBonus(actor, actorParty, request.Operation, request.IntValue);
                    break;
                case FourberieOperation.CreateAgentParty:
                case FourberieOperation.DisbandAgentParty:
                case FourberieOperation.RefillAgentParty:
                    ApplyAgentParty(actor, actorParty, request.Operation);
                    break;
                case FourberieOperation.ResetCrimeBaseParty:
                    ApplyCrimeBaseReset(actor, actorParty);
                    break;
                case FourberieOperation.AssignCriminalRole:
                case FourberieOperation.RemoveCriminalRole:
                    ApplyCriminalRole(actor, actorParty, request);
                    break;
                case FourberieOperation.SelectSchemeVictim:
                case FourberieOperation.SelectSchemeType:
                case FourberieOperation.StartScheme:
                case FourberieOperation.AbortScheme:
                case FourberieOperation.ClearCompletedScheme:
                case FourberieOperation.ChangeSchemeStance:
                    ApplySchemeOperation(actor, actorParty, request);
                    break;
                case FourberieOperation.SetCorruptionLevel:
                case FourberieOperation.SetAutoInvestment:
                case FourberieOperation.SetLadsDuty:
                case FourberieOperation.SetSlavesDuty:
                    ApplyCrimeRoomSetting(request.Operation, request.IntValue);
                    break;
                case FourberieOperation.EnsureSchemeRoomDefaults:
                case FourberieOperation.ClearDominanceConversation:
                    ApplyPresentationState(request.Operation);
                    break;
                case FourberieOperation.CommitStealthEvent:
                    ApplyStealthEvent(actor, actorParty, request);
                    break;
                case FourberieOperation.CommitBanditEvent:
                    actorCounts = CaptureAllCounts(actorParty.MemberRoster);
                    actorPrisonCounts = CaptureAllCounts(actorParty.PrisonRoster);
                    actorItems = CaptureAllItems(actorParty.ItemRoster);
                    banditStashItems = CaptureAllItems(GetStaticField("_stash") as ItemRoster);
                    if (!string.IsNullOrEmpty(request.TargetId) &&
                        objectManager.TryGetObject(request.TargetId, out MobileParty resolvedBanditTarget))
                    {
                        banditTargetParty = resolvedBanditTarget;
                        banditTargetCounts = CaptureAllCounts(banditTargetParty.MemberRoster);
                        banditTargetPrisonCounts = CaptureAllCounts(banditTargetParty.PrisonRoster);
                    }
                    ApplyBanditEvent(actor, actorParty, request);
                    break;
                case FourberieOperation.CommitLegacyCallback:
                    actorCounts = CaptureAllCounts(actorParty.MemberRoster);
                    actorPrisonCounts = CaptureAllCounts(actorParty.PrisonRoster);
                    actorItems = CaptureAllItems(actorParty.ItemRoster);
                    ExecuteLegacyCallback(actor, actorParty, request);
                    break;
                case FourberieOperation.CommitConversationEvent:
                    ApplyConversationEvent(actor, actorParty, request);
                    break;
                case FourberieOperation.CommitCampaignConsequence:
                    ApplyCampaignConsequence(actor, actorParty, request);
                    break;
                case FourberieOperation.RecruitMinorTroops:
                    actorCounts = CaptureAllCounts(actorParty.MemberRoster);
                    ApplyMinorRecruitment(actor, actorParty, request);
                    break;
                case FourberieOperation.LeaveKingdom:
                    ApplyKingdomLeave(actor, request.SecondaryTargetId);
                    break;
                case FourberieOperation.CommitGuardKills:
                    ApplyGuardKills(actor, actorParty, request);
                    break;
                case FourberieOperation.CommitSafehouseEncounter:
                    ApplySafehouseEncounter(actorParty, request);
                    break;
                case FourberieOperation.EnableContractOffers:
                case FourberieOperation.DisableContractOffers:
                case FourberieOperation.AbortContract:
                case FourberieOperation.AcceptContractProposal:
                case FourberieOperation.DeclineContractProposal:
                    ApplyContractOperation(actor, actorParty, request.Operation);
                    break;
                case FourberieOperation.SetMainCrimeBase:
                    ApplyMainCrimeBase(request.SettlementId);
                    break;
                case FourberieOperation.RemoveTerritory:
                case FourberieOperation.AbandonTownCrimeBase:
                    ApplyTerritoryRemoval(request.SettlementId, request.Operation);
                    break;
                case FourberieOperation.AbandonSafehouse:
                    ApplySafehouseAbandonment(actor, actorParty, request.SettlementId);
                    break;
                case FourberieOperation.RequestGrudgeQuote:
                    resultValue = PrepareGrudgeQuote(actor, request.TargetId);
                    break;
                case FourberieOperation.SettleClanGrudge:
                    previousTransferTarget = ResolveGrudgeRecipient(request.TargetId);
                    previousTransferTargetGold = previousTransferTarget.Gold;
                    ApplyGrudgeSettlement(actor, request.TargetId, request.IntValue, previousTransferTarget);
                    break;
                case FourberieOperation.SellQuarterSlaves:
                case FourberieOperation.SellHalfSlaves:
                case FourberieOperation.DeclineCrookedTrader:
                case FourberieOperation.RobCrookedTrader:
                    ApplySafehouseTrader(actor, actorParty, request);
                    break;
                case FourberieOperation.EstablishSafehouse:
                    ApplySafehouseEstablishment(actor, actorParty, request.SettlementId);
                    break;
                case FourberieOperation.StartSafehouseWait:
                case FourberieOperation.StopSafehouseWait:
                    ApplySafehouseWait(actorParty, request.SettlementId, request.Operation);
                    break;
                case FourberieOperation.CompleteSafehouseReturn:
                    ApplySafehouseReturn(actorParty, request.SettlementId);
                    break;
                case FourberieOperation.CompleteGrabAndRun:
                case FourberieOperation.CompleteGangLeaderBashing:
                case FourberieOperation.CompleteIsolatedRobbery:
                case FourberieOperation.CompletePickpocketFight:
                case FourberieOperation.CompleteGrudgeAssassination:
                case FourberieOperation.CompleteTavernBrawl:
                case FourberieOperation.CompleteLarcenyFight:
                case FourberieOperation.CompleteAlleyFight:
                    ApplyInsideMissionOutcome(actor, actorParty, request);
                    break;
                case FourberieOperation.CompleteFightClubMatch:
                    ApplyFightClubOutcome(actor, actorParty, request);
                    break;
                case FourberieOperation.StartFightClubMatch:
                case FourberieOperation.EnrollFightClub:
                case FourberieOperation.RefuteFightClubPatron:
                case FourberieOperation.OwnFightClubStable:
                    ApplyFightClubLifecycle(actor, actorParty, request);
                    break;
                default:
                    throw new InvalidOperationException("unknown Fourberie operation");
            }

            return true;
        }
        catch (Exception exception)
        {
            Exception reported = exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
            var rollbackErrors = new List<string>();
            try { RestoreCounts(sourceParty?.MemberRoster, sourceCounts); }
            catch (Exception rollback) { rollbackErrors.Add("source roster: " + rollback.Message); }
            try { RestoreCounts(actorParty.MemberRoster, actorCounts); }
            catch (Exception rollback) { rollbackErrors.Add("actor roster: " + rollback.Message); }
            try { RestoreCounts(actorParty.PrisonRoster, actorPrisonCounts); }
            catch (Exception rollback) { rollbackErrors.Add("actor prisoner roster: " + rollback.Message); }
            try { RestoreItems(actorParty.ItemRoster, actorItems); }
            catch (Exception rollback) { rollbackErrors.Add("actor item roster: " + rollback.Message); }
            try { RestoreItems(previousCrimeBase?.ItemRoster, safehouseItems); }
            catch (Exception rollback) { rollbackErrors.Add("safehouse item roster: " + rollback.Message); }
            try { RestoreItems(GetStaticField("_stash") as ItemRoster, banditStashItems); }
            catch (Exception rollback) { rollbackErrors.Add("bandit stash roster: " + rollback.Message); }
            try { RestoreCounts(banditTargetParty?.MemberRoster, banditTargetCounts); }
            catch (Exception rollback) { rollbackErrors.Add("bandit member roster: " + rollback.Message); }
            try { RestoreCounts(banditTargetParty?.PrisonRoster, banditTargetPrisonCounts); }
            catch (Exception rollback) { rollbackErrors.Add("bandit prisoner roster: " + rollback.Message); }
            try
            {
                if (previousAgents?.IsActive == true)
                    RestoreCounts(previousAgents.MemberRoster, previousAgentCounts);
            }
            catch (Exception rollback) { rollbackErrors.Add("agent-party roster: " + rollback.Message); }

            MobileParty createdBandits = GetStaticField("_insubandF") as MobileParty;
            MobileParty createdCaravan = GetStaticField("_insucaraF") as MobileParty;
            TryDestroyCreated(createdBandits, previousBandits, rollbackErrors);
            TryDestroyCreated(createdCaravan, previousCaravan, rollbackErrors);
            MobileParty createdAgents = GetStaticField("_agentsParty") as MobileParty;
            TryDestroyCreated(createdAgents, previousAgents, rollbackErrors);
            try { SetStaticField("_agentsParty", previousAgents?.IsActive == true ? previousAgents : null); }
            catch (Exception rollback) { rollbackErrors.Add("agent-party reference: " + rollback.Message); }
            if (previousAgentsWasActive && previousAgents?.IsActive != true)
                rollbackErrors.Add("agent-party destruction cannot be reversed");
            MobileParty createdCrimeBase = GetStaticField("_crimeBaseParty") as MobileParty;
            TryDestroyCreated(createdCrimeBase, previousCrimeBase, rollbackErrors);
            try { SetStaticField("_crimeBaseParty", previousCrimeBase?.IsActive == true ? previousCrimeBase : null); }
            catch (Exception rollback) { rollbackErrors.Add("crime-base-party reference: " + rollback.Message); }
            if (previousCrimeBaseWasActive && previousCrimeBase?.IsActive != true)
                rollbackErrors.Add("crime-base-party destruction cannot be reversed");

            if (!FourberieCanonicalState.TryApply(assembly, objectManager, rollbackState, out var stateFailure))
                rollbackErrors.Add("canonical state: " + stateFailure);
            try { RestoreGold(actor, previousActorGold); }
            catch (Exception rollback) { rollbackErrors.Add("actor gold: " + rollback.Message); }
            try { actor.HitPoints = previousActorHitPoints; }
            catch (Exception rollback) { rollbackErrors.Add("actor hit points: " + rollback.Message); }
            if (previousTransferTarget != null)
            {
                try { RestoreGold(previousTransferTarget, previousTransferTargetGold); }
                catch (Exception rollback) { rollbackErrors.Add("grudge recipient gold: " + rollback.Message); }
            }

            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    "Fourberie operation rollback failed after " + reported.Message + ": " +
                    string.Join("; ", rollbackErrors), reported);

            failure = reported.GetType().Name + ": " + reported.Message;
            return false;
        }
    }

    public void Reset()
    {
        grudgeQuotes.Clear();
        stealthVictims.Clear();
    }

    private void ApplyPresentationState(FourberieOperation operation)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        if (operation == FourberieOperation.EnsureSchemeRoomDefaults)
        {
            if (!crime.Contains(500)) crime[500] = 2;
            return;
        }

        if (operation == FourberieOperation.ClearDominanceConversation)
        {
            crime.Remove(92);
            return;
        }

        throw new InvalidOperationException("operation is not a presentation-state transaction");
    }

    private void ApplyStealthEvent(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement))
            throw new InvalidOperationException("the stealth event is not in the controller's current settlement");

        var stealthEvent = (FourberieStealthEvent)request.IntValue;
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        string actorId = actor.StringId;

        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            switch (stealthEvent)
            {
                case FourberieStealthEvent.AlertRaised:
                    SetStealthAlert(settlement);
                    return;
                case FourberieStealthEvent.MilitiaFullPayment:
                case FourberieStealthEvent.MilitiaHalfPayment:
                    if (settlement.Village == null)
                        throw new InvalidOperationException("militia payment requires the current village");
                    int payment = 3900 + (int)settlement.Village.Hearth;
                    if (stealthEvent == FourberieStealthEvent.MilitiaHalfPayment) payment /= 2;
                    GiveGoldAction.ApplyBetweenCharacters(actor, null, payment, false);
                    return;
                case FourberieStealthEvent.AbortContractForRansom:
                    ApplyStealthContractAbort(actor, settlement, crime, heroes);
                    return;
                case FourberieStealthEvent.LordWounded:
                    ApplyStealthLordWounded(actor, settlement, request.TargetId, crime, actorId);
                    return;
                case FourberieStealthEvent.FinishMission:
                    ResolveStealthVictims(actorId);
                    return;
                case FourberieStealthEvent.FinishMissionAlerted:
                    SetStealthAlert(settlement);
                    ResolveStealthVictims(actorId);
                    return;
                case FourberieStealthEvent.ScandalRecovered:
                    SetStealthAlert(settlement);
                    crime[116] = 0;
                    return;
                case FourberieStealthEvent.PrisonBreakCompleted:
                    SetStealthAlert(settlement);
                    crime[113] = 0;
                    crime[98] = 1;
                    actor.AddSkillXp(DefaultSkills.Roguery, 1000f);
                    actor.AddSkillXp(DefaultSkills.Athletics, 1000f);
                    return;
                case FourberieStealthEvent.GreedyMilitiaAccepted:
                    RequiredMethod(BehaviorTypeName, "AftermathGreedy", 1).Invoke(null, new object[] { 2 });
                    return;
                case FourberieStealthEvent.GreedyMilitiaRefused:
                    RequiredMethod(BehaviorTypeName, "AftermathGreedy", 1).Invoke(null, new object[] { 3 });
                    return;
                case FourberieStealthEvent.GreedyMilitiaImmediate:
                    RequiredMethod(BehaviorTypeName, "AftermathGreedy", 1).Invoke(null, new object[] { 1 });
                    return;
                case FourberieStealthEvent.FailedLordHall:
                case FourberieStealthEvent.FailedPrison:
                case FourberieStealthEvent.FailedTownCenter:
                case FourberieStealthEvent.FailedVillage:
                    ApplyStealthFailure(actor, settlement, stealthEvent, crime, actorId);
                    return;
                default:
                    throw new InvalidOperationException("unknown stealth event");
            }
        }
    }

    private void ApplyStealthContractAbort(Hero actor, Settlement settlement, IDictionary crime, IDictionary heroes)
    {
        if (!crime.Contains(201) || heroes?["contractTarget"] is not string targetId ||
            !objectManager.TryGetObject(targetId, out Hero target) || target == null ||
            heroes["contractGiver"] is not string giverId ||
            !objectManager.TryGetObject(giverId, out Hero giver) || giver == null || !giver.IsAlive)
            throw new InvalidOperationException("the negotiated stealth contract is no longer active");

        float reward = Convert.ToInt32(crime[201]);
        reward *= target.GetTraitLevel(DefaultTraits.Generosity) switch
        {
            < 0 => 0.9f,
            0 => 1.1f,
            1 => 1.2f,
            2 => 1.3f,
            _ => 1f,
        };
        GiveGoldAction.ApplyBetweenCharacters(null, actor, Math.Max(0, (int)reward), false);
        RequiredMethod("Fourberie.FourbContractBehavior", "ContractAborted", 2)
            .Invoke(null, new object[] { true, 10 });
        SetStealthAlert(settlement);
    }

    private void ApplyStealthLordWounded(
        Hero actor,
        Settlement settlement,
        string targetId,
        IDictionary crime,
        string actorId)
    {
        if (!objectManager.TryGetObject(targetId, out Hero target) || target == null || target.Clan == null ||
            target.CurrentSettlement != settlement ||
            !target.CanDie((KillCharacterAction.KillCharacterActionDetail)1))
            throw new InvalidOperationException("the reported stealth target is not an eligible lord in this settlement");

        if (!stealthVictims.TryGetValue(actorId, out List<string> victims))
        {
            victims = new List<string>();
            stealthVictims.Add(actorId, victims);
        }
        if (victims.Contains(targetId, StringComparer.Ordinal)) return;

        RequiredMethod("Fourberie.FourbContractBehavior", "ContractComplete", 2)
            .Invoke(null, new object[] { target.Clan, 0 });
        actor.AddSkillXp(DefaultSkills.Roguery, 2000f);
        crime[1150] = 1;
        SetStealthAlert(settlement);
        victims.Add(targetId);
    }

    private void ResolveStealthVictims(string actorId)
    {
        if (!stealthVictims.TryGetValue(actorId, out List<string> victims)) return;
        for (int index = 0; index < victims.Count; index++)
        {
            if (!objectManager.TryGetObject(victims[index], out Hero target) || target == null || !target.IsAlive)
                continue;
            if (index > 0 && MBRandom.RandomInt(6) == 0) continue;
            target.AddDeathMark(null, (KillCharacterAction.KillCharacterActionDetail)1);
            KillCharacterAction.ApplyByMurder(target, null, true);
        }
        stealthVictims.Remove(actorId);
    }

    private void ApplyStealthFailure(
        Hero actor,
        Settlement settlement,
        FourberieStealthEvent stealthEvent,
        IDictionary crime,
        string actorId)
    {
        if (stealthEvent != FourberieStealthEvent.FailedVillage)
        {
            float severity = 100f;
            float crimeRating = 50f;
            int relation = -20;
            int grudge = 10;
            if (stealthEvent == FourberieStealthEvent.FailedLordHall)
            {
                severity *= 2f;
                relation *= 3;
                crimeRating *= 3f;
                grudge = 50;
            }
            else if (stealthEvent == FourberieStealthEvent.FailedPrison)
            {
                relation *= 2;
                crimeRating *= 2f;
                grudge = 25;
            }

            if (settlement.OwnerClan != null)
                InvokeClanGrudge(settlement.OwnerClan, grudge);

            if (stealthVictims.TryGetValue(actorId, out List<string> victims))
            {
                severity += 25f * victims.Count;
                relation -= 5 * victims.Count;
                crimeRating += 20f * victims.Count;
                foreach (string victimId in victims)
                {
                    if (!objectManager.TryGetObject(victimId, out Hero victim) || victim == null) continue;
                    if (victim.Clan != null) InvokeClanGrudge(victim.Clan, 120);
                    InvokePlayerConsequences(victim, true, 200f, -50, 70f);
                }
            }

            if (settlement.Owner != null)
                InvokePlayerConsequences(settlement.Owner, false, severity, relation, crimeRating);
        }

        SetStealthAlert(settlement);
        crime[98] = 2;
    }

    private void InvokeClanGrudge(Clan clan, int impact) =>
        RequiredMethod(BehaviorTypeName, "ClanGrudgeChange", 3).Invoke(
            null,
            new object[] { clan.StringId, impact, clan.EncyclopediaLinkWithName.ToString() });

    private void InvokePlayerConsequences(
        Hero target,
        bool murder,
        float severity,
        int relation,
        float crimeRating) =>
        RequiredMethod(BehaviorTypeName, "PlayerActionsConsequences", 6).Invoke(
            null,
            new object[] { target, "Stealth", murder, severity, relation, crimeRating });

    private void SetStealthAlert(Settlement settlement) =>
        GetDictionary("_InfiltrationAlertTiming")[settlement.StringId] = CampaignTime.Now;

    private void ApplyBanditEvent(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        var banditEvent = (FourberieBanditEvent)request.IntValue;
        Settlement settlement = null;
        if (!string.IsNullOrEmpty(request.SettlementId) &&
            !TryResolveCurrentSettlement(actorParty, request.SettlementId, out settlement))
            throw new InvalidOperationException("the bandit action is not in the controller's current settlement");

        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            switch (banditEvent)
            {
                case FourberieBanditEvent.RepairShips:
                    RequireBanditSettlement(settlement);
                    RepairActorShips(actor, actorParty);
                    return;
                case FourberieBanditEvent.HealWounds:
                    RequireBanditSettlement(settlement);
                    HealActor(actor, actorParty);
                    return;
                case FourberieBanditEvent.ReleaseAllFollowers:
                    RequireBanditSettlement(settlement);
                    ReleaseAllBanditFollowers(actorParty);
                    return;
                case FourberieBanditEvent.RefuseBanditJoin:
                    ApplyRefuseBanditJoin(actor, actorParty, ResolveBanditParty(request.TargetId));
                    return;
                case FourberieBanditEvent.FollowParties:
                    ApplyBanditFollowers(actorParty, request.ObjectIds);
                    return;
                case FourberieBanditEvent.StopFollower:
                    ReleaseBanditFollower(actorParty, ResolveBanditParty(request.TargetId));
                    return;
                case FourberieBanditEvent.AcceptTruce:
                    ApplyBanditTruce(actor, settlement, accept: true, relationPenalty: 0);
                    return;
                case FourberieBanditEvent.BreakTruce:
                    ApplyBanditTruce(actor, settlement, accept: false, relationPenalty: 0);
                    return;
                case FourberieBanditEvent.BetrayBandits:
                    ApplyBanditTruce(actor, settlement, accept: false, relationPenalty: -20);
                    return;
                case FourberieBanditEvent.SelectWarDogKingdom:
                    ApplyWarDog(actor, actorParty, settlement, request.TargetId);
                    return;
                case FourberieBanditEvent.AcquireCoveShip:
                    ApplyCoveShip(actorParty, settlement, request.TargetId);
                    return;
                case FourberieBanditEvent.TransferFollowerShip:
                    TransferFollowerShip(actorParty, ResolveBanditParty(request.TargetId), request.SecondaryTargetId);
                    return;
                case FourberieBanditEvent.DonatePrisoners:
                    DonatePrisoners(actor, actorParty, settlement, request.Troops);
                    return;
                case FourberieBanditEvent.CommitBanditRoster:
                    CommitBanditRoster(
                        actor,
                        actorParty,
                        ResolveBanditParty(request.TargetId),
                        request.Roster,
                        request.SecondaryTargetId == "recruit.all");
                    return;
                case FourberieBanditEvent.PrepareRecruitment:
                    PrepareBanditRecruitment(settlement);
                    return;
                case FourberieBanditEvent.OpenBanditStash:
                    OpenBanditStash(settlement);
                    return;
                case FourberieBanditEvent.RefreshBlackMarket:
                    RefreshBlackMarket(settlement);
                    return;
                case FourberieBanditEvent.StartHideoutWait:
                    SetHideoutWait(actorParty, settlement, waiting: true);
                    return;
                case FourberieBanditEvent.StopHideoutWait:
                    SetHideoutWait(actorParty, settlement, waiting: false);
                    return;
                case FourberieBanditEvent.DonateLoot:
                    TransferBanditLoot(actorParty, settlement, request.Items);
                    return;
                default:
                    throw new InvalidOperationException("unknown bandit event");
            }
        }
    }

    private void ExecuteLegacyCallback(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!FourberieOperationProtocol.IsLegacyCallbackToken(request.IntValue) ||
            !TryResolveCurrentSettlement(actorParty, request.SettlementId, out _))
            throw new InvalidOperationException("the legacy Fourberie callback is not valid in the controller's current settlement");
        MethodBase method = assembly.ManifestModule.ResolveMethod(request.IntValue);
        if (method == null || method.DeclaringType == null || !method.DeclaringType.Name.Contains("<>c") ||
            method.DeclaringType.Name.Contains("DisplayClass"))
            throw new InvalidOperationException("the legacy Fourberie callback owner is not an approved stateless closure");
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length > 1 || parameters.Length == 1 && parameters[0].ParameterType.FullName !=
            "TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs")
            throw new InvalidOperationException("the legacy Fourberie callback signature changed");

        object instance = method.IsStatic ? null :
            method.DeclaringType.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ??
            Activator.CreateInstance(method.DeclaringType, nonPublic: true);
        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        using (new FourberieLegacyExecutionContext())
            method.Invoke(instance, parameters.Length == 0 ? Array.Empty<object>() : new object[] { null });
    }

    private void ApplyConversationEvent(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement))
            throw new InvalidOperationException("the Fourberie conversation is not in the controller's current settlement");
        var conversationEvent = (FourberieConversationEvent)request.IntValue;
        Hero target = string.IsNullOrEmpty(request.TargetId) ? null : ResolveHero(request.TargetId);
        if (conversationEvent != FourberieConversationEvent.ResolveGangLeaderBashing &&
            (target == null || !target.IsAlive || target.CurrentSettlement != settlement))
            throw new InvalidOperationException("the Fourberie conversation target is stale or outside the current settlement");

        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            switch (conversationEvent)
            {
                case FourberieConversationEvent.PromoteGangLeader:
                    PromoteGangLeader(actor, settlement, target);
                    return;
                case FourberieConversationEvent.EstablishPartnership:
                    EstablishPartnership(actor, settlement, target);
                    return;
                case FourberieConversationEvent.AcceptRecommendation:
                    AcceptRecommendation(actor, target);
                    return;
                case FourberieConversationEvent.RejectRivalry:
                case FourberieConversationEvent.RejectBashing:
                    RejectGangLeader(actor, target);
                    return;
                case FourberieConversationEvent.ResolveGangLeaderBashing:
                    ResolveGangLeaderBashing();
                    return;
                default:
                    throw new InvalidOperationException("unknown Fourberie conversation event");
            }
        }
    }

    private void ApplyCampaignConsequence(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        var consequence = (FourberieCampaignConsequence)request.IntValue;
        Hero target = string.IsNullOrEmpty(request.TargetId) ? null : ResolveHero(request.TargetId);
        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            switch (consequence)
            {
                case FourberieCampaignConsequence.StartAssassination:
                    GetDictionary("_crimeValue")[104] = 30;
                    return;
                case FourberieCampaignConsequence.RanAway:
                    GetDictionary("_crimeValue").Remove(104);
                    GetDictionary("_crimeValue").Remove(512);
                    AddTraitXp(actor, DefaultTraits.Honor, -5);
                    AddTraitXp(actor, DefaultTraits.Valor, -10);
                    return;
                case FourberieCampaignConsequence.HealWound:
                    ApplyHealWound(actor, actorParty);
                    return;
                case FourberieCampaignConsequence.SafehouseCompanionRelation:
                    if (target == null || !target.IsPlayerCompanion || target.Clan != actor.Clan)
                        throw new InvalidOperationException("the safehouse companion is no longer eligible");
                    if (target.GetRelation(actor) <= 55f && MBRandom.RandomInt(0, 11) > 5)
                        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, target, 1, true);
                    return;
                case FourberieCampaignConsequence.BribeGuard:
                    if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
                        settlement.Town == null)
                        throw new InvalidOperationException("the bribed settlement is no longer current");
                    int price = (int)settlement.Town.Prosperity;
                    if (actor.Gold < price) throw new InvalidOperationException("not enough gold to bribe the guard");
                    GiveGoldAction.ApplyBetweenCharacters(actor, null, price, false);
                    Campaign.Current.IsMainHeroDisguised = true;
                    return;
                default:
                    throw new InvalidOperationException("unknown Fourberie campaign consequence");
            }
        }
    }

    private void ApplyMinorRecruitment(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Town == null || settlement.Culture == null)
            throw new InvalidOperationException("the minor recruitment town is no longer current");
        var allowedIds = new HashSet<string>(StringComparer.Ordinal);
        Type helper = assembly.GetType("Fourberie.ListHelper", true, false);
        if (AccessTools.Field(helper, "_troopList")?.GetValue(null) is IEnumerable list)
        {
            foreach (object entry in list)
            {
                PropertyInfo first = entry.GetType().GetProperty("Item1");
                PropertyInfo second = entry.GetType().GetProperty("Item2");
                if (string.Equals(first?.GetValue(entry) as string, settlement.Culture.StringId, StringComparison.Ordinal) &&
                    second?.GetValue(entry) is string id)
                    allowedIds.Add(id);
            }
        }
        int count = 0;
        var selected = new List<(CharacterObject Troop, int Count)>();
        foreach (FourberieTroopSelection selection in request.Troops)
        {
            if (!allowedIds.Contains(selection.TroopId) ||
                !objectManager.TryGetObject(selection.TroopId, out CharacterObject troop) || troop == null)
                throw new InvalidOperationException("the selected minor-faction troop is not eligible here");
            count = checked(count + selection.Count);
            selected.Add((troop, selection.Count));
        }
        if (count > 30 || actorParty.MemberRoster.TotalManCount + count > actorParty.Party.PartySizeLimit)
            throw new InvalidOperationException("the selected recruits exceed the party limit");
        CharacterObject baseline = CharacterObject.All.FirstOrDefault(value =>
            value.Occupation == Occupation.Villager && value.Level == 21);
        if (baseline == null) throw new InvalidOperationException("the recruitment cost baseline is unavailable");
        int unitCost = (int)(Campaign.Current.Models.PartyWageModel
            .GetTroopRecruitmentCost(baseline, actor, false).ResultNumber + settlement.Town.Prosperity / 111f);
        int price = checked(count * unitCost);
        if (actor.Gold < price) throw new InvalidOperationException("not enough gold for the selected recruits");
        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            GiveGoldAction.ApplyBetweenCharacters(actor, null, price, false);
            foreach ((CharacterObject troop, int amount) in selected)
                actorParty.MemberRoster.AddToCounts(troop, amount, false, 0, 0, true, -1);
            Type recruitable = assembly.GetType("Fourberie.FourbRecruitableBehavior", true, false);
            if (AccessTools.Field(recruitable, "_recruitTiming")?.GetValue(null) is IDictionary timing)
                timing[settlement] = CampaignTime.Now;
            else throw new InvalidOperationException("the minor recruitment cooldown is unavailable");
        }
    }

    private static void ApplyKingdomLeave(Hero actor, string choice)
    {
        Clan clan = actor.Clan;
        if (clan?.Kingdom == null || clan.Leader != actor)
            throw new InvalidOperationException("the controller no longer leads a kingdom clan");
        using (new AllowedThread())
        {
            if (choice == "keep")
                ChangeKingdomAction.ApplyByLeaveWithRebellionAgainstKingdom(clan, true);
            else if (choice == "dontkeep")
                ChangeKingdomAction.ApplyByLeaveKingdom(clan, true);
            else throw new InvalidOperationException("the kingdom leave choice is invalid");
        }
    }

    private void ApplyGuardKills(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement))
            throw new InvalidOperationException("the guard-kill settlement is no longer current");
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        Hero giver = ResolveMappedHero("jobKillGuard");
        if (giver == null || !giver.IsAlive)
        {
            heroes.Remove("jobKillGuard");
            crime.Remove(101);
            return;
        }
        if (giver.CurrentSettlement != settlement)
            throw new InvalidOperationException("the guard-kill job giver is no longer in this settlement");
        int required = ReadInt(crime, 101);
        if (request.IntValue < required) return;
        using (new AllowedThread())
        using (new BarterPlayerContext(actor, actorParty))
        {
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, giver, required, true);
            GiveGoldAction.ApplyBetweenCharacters(null, actor, MBRandom.RandomInt(required * 100, required * 100 + 200), false);
            actor.AddSkillXp(DefaultSkills.Roguery, required * 100f);
            RequiredMethod(BehaviorTypeName, "XpFornoMercyNoHonorinParty", 3)
                .Invoke(null, new object[] { 0f, actorParty.MemberRoster, true });
            giver.AddPower(5f);
            heroes.Remove("jobKillGuard");
            crime.Remove(101);
        }
    }

    private void ApplySafehouseEncounter(
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!objectManager.TryGetObject(request.SettlementId, out Settlement crimeBase) ||
            crimeBase == null || crimeBase != GetStaticField("_crimeBase"))
            throw new InvalidOperationException("the safehouse encounter base is stale");
        int encounterType = request.IntValue / 2;
        bool playerWon = request.IntValue % 2 == 1;
        using (new AllowedThread())
        {
            if (GetStaticField("_FourbParty") is MobileParty encounterParty)
            {
                if (encounterParty.IsActive) DestroyPartyAction.Apply(null, encounterParty);
                SetStaticField("_FourbParty", null);
            }
            if (!playerWon && encounterType == 1)
            {
                int escaped = ReadInt(GetDictionary("_crimeValue"), 1500) / 2;
                RequiredMethod(BehaviorTypeName, "PrisonersVirtualLogic", 2)
                    .Invoke(null, new object[] { 0, escaped });
            }
            else if (!playerWon && encounterType == 2)
                RequiredMethod(BehaviorTypeName, "HideoutDeactivated", 1)
                    .Invoke(null, new object[] { crimeBase });
        }
    }

    private void ApplyHealWound(Hero actor, MobileParty actorParty)
    {
        if (actor.IsHealthFull()) return;
        Town nearest = SettlementHelper.FindNearestTownToMobileParty(
            actorParty, MobileParty.NavigationType.Default, null);
        if (nearest == null) throw new InvalidOperationException("no town is available for Fourberie healing");
        int price = 1000 + (int)nearest.Prosperity / 100;
        Settlement crimeBase = GetStaticField("_crimeBase") as Settlement;
        IDictionary crime = GetDictionary("_crimeValue");
        if (crimeBase?.IsTown == true && crime.Contains(3) &&
            Convert.ToBoolean(RequiredMethod(BehaviorTypeName, "PaymasterCond", 0).Invoke(null, null)) ||
            crimeBase?.IsHideout == true && ReadInt(crime, 560) > 2)
            price /= 2;
        if (actor.Gold < price) throw new InvalidOperationException("not enough gold for Fourberie healing");
        GiveGoldAction.ApplyBetweenCharacters(actor, null, price, false);
        int healed = Math.Min(
            (int)actorParty.Party.HealingRateForMemberHeroes,
            actor.CharacterObject.MaxHitPoints() - actor.HitPoints);
        actor.HitPoints += healed;
        SkillLevelingManager.OnHeroHealedWhileWaiting(actor, healed);
    }

    private void PromoteGangLeader(Hero actor, Settlement settlement, Hero target)
    {
        if (!target.IsGangLeader || target.GetRelation(actor) <= 45f || target.CurrentSettlement == null)
            throw new InvalidOperationException("the selected gang leader is not eligible for promotion");
        if (GetStaticField("_territoryList") is not IList territories ||
            !territories.Contains(target.CurrentSettlement.StringId) ||
            GetDictionary("_assignedGl").Contains(target.CurrentSettlement.StringId))
            throw new InvalidOperationException("the selected gang leader's territory is not eligible");
        IDictionary assigned = GetDictionary("_assignedGl");
        assigned[settlement.StringId] = target.StringId;
        target.AddPower(400f);
        target.SupporterOf = actor.Clan;
        if (target.GetRelation(actor) < 40f)
            CharacterRelationManager.SetHeroRelation(target, actor, 50);
        foreach (Alley alley in settlement.Alleys) alley.SetOwner(target);
    }

    private void EstablishPartnership(Hero actor, Settlement settlement, Hero target)
    {
        if (!target.IsGangLeader || settlement.Town == null)
            throw new InvalidOperationException("the partnership target is not a gang leader");
        AddTraitXp(actor, DefaultTraits.Honor, -20);
        AddTraitXp(actor, DefaultTraits.Mercy, -20);
        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, target, 40, true);
        settlement.Town.Loyalty -= 20f;
        settlement.Town.Security -= 20f;
        foreach (Hero notable in settlement.Notables.Where(value => !value.IsGangLeader))
        {
            int current = (int)notable.GetRelation(actor);
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, notable,
                current > 0 ? -(current + 5) : -5, true);
        }
        ReplaceListMembership("_territoryList", settlement.StringId, present: false);
        ReplaceListMembership("_partnershipList", settlement.StringId, present: true);
        ReplaceListMembership("_partnerRecomList", settlement.StringId, present: true);
    }

    private void AcceptRecommendation(Hero actor, Hero target)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        crime.Remove(90);
        GetDictionary("_stringHeroIdDico").Remove("recomGl");
        int relation = (int)Math.Floor(10d / (1d + actor.GetSkillValue(DefaultSkills.Charm) * 0.005d));
        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, target, relation, true);
    }

    private static void RejectGangLeader(Hero actor, Hero target)
    {
        int current = (int)target.GetRelation(actor);
        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(actor, target,
            -(30 + Math.Max(0, current)), true);
    }

    private void ResolveGangLeaderBashing()
    {
        Hero low = ResolveMappedHero("lowPowerGl");
        Hero high = ResolveMappedHero("highPowerGl");
        if (low == null || high == null || !low.IsAlive || !high.IsAlive)
            throw new InvalidOperationException("the gang-leader rivalry context is stale");
        RequiredMethod("Fourberie.FourberieBehavior", "AftermathGlBash", 2)
            .Invoke(null, new object[] { low, high });
    }

    private void AddTraitXp(Hero actor, TraitObject trait, int amount)
    {
        MethodInfo traitXp = RequiredMethod("Fourberie.VanillaHelperFourb", "AddPlayerTraitXPAndLogEntry", 4);
        object note = Enum.ToObject(traitXp.GetParameters()[2].ParameterType, 0);
        traitXp.Invoke(null, new object[] { trait, amount, note, actor });
    }

    private void ReplaceListMembership(string fieldName, string value, bool present)
    {
        if (GetStaticField(fieldName) is not IList list)
            throw new InvalidOperationException("Fourberie field " + fieldName + " is not a list");
        while (list.Contains(value)) list.Remove(value);
        if (present) list.Add(value);
    }

    private static void RequireBanditSettlement(Settlement settlement)
    {
        if (settlement == null || !settlement.IsHideout || settlement.Culture == null ||
            settlement.MapFaction == null || !settlement.MapFaction.IsBanditFaction)
            throw new InvalidOperationException("the action requires the controller's current bandit hideout");
    }

    private void PrepareBanditRecruitment(Settlement settlement)
    {
        RequireBanditSettlement(settlement);
        IDictionary values = GetDictionary("_stringIntDico");
        if (values.Contains(settlement.StringId)) return;
        Settlement crimeBase = GetStaticField("_crimeBase") as Settlement;
        int bonus = crimeBase == settlement ? ReadInt(GetDictionary("_crimeValue"), 560) * 20 : 0;
        values[settlement.StringId] = 30 + bonus;
    }

    private void OpenBanditStash(Settlement settlement)
    {
        RequireBanditSettlement(settlement);
        if (GetStaticField("_stash") is not ItemRoster) SetStaticField("_stash", new ItemRoster());
        GetDictionary("_crimeValue")[554] = 0;
    }

    private void RefreshBlackMarket(Settlement settlement)
    {
        RequireBanditSettlement(settlement);
        MobileParty trader = settlement.Parties.FirstOrDefault(value => value.IsPartyTradeActive);
        if (trader == null) throw new InvalidOperationException("the bandit black market is unavailable");

        IDictionary timings = GetDictionary("_townScamTiming");
        if (timings.Contains(settlement.StringId) && timings[settlement.StringId] is CampaignTime previous &&
            previous.ElapsedHoursUntilNow <= 24f)
            return;

        Town nearest = SettlementHelper.FindNearestTownToSettlement(settlement, MobileParty.NavigationType.Default, null);
        ItemRosterElement[] eligible = nearest?.Settlement?.ItemRoster == null
            ? Array.Empty<ItemRosterElement>()
            : Enumerable.Range(0, nearest.Settlement.ItemRoster.Count)
                .Select(nearest.Settlement.ItemRoster.GetElementCopyAtIndex)
                .Where(value => value.Amount > 0 && value.EquipmentElement.ItemValue > 100)
                .ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("no eligible black-market stock is available");

        int strength = BanditStrength(settlement.Culture.StringId);
        int stockCount = 6 + Math.Min(15, strength / 500);
        int maximumValue = Math.Max(10000, strength * 10);
        ItemRosterElement[] affordable = eligible.Where(value => value.EquipmentElement.ItemValue < maximumValue).ToArray();
        if (affordable.Length == 0) throw new InvalidOperationException("no affordable black-market stock is available");
        trader.ItemRoster.Clear();
        for (int index = 0; index < stockCount; index++)
        {
            ItemRosterElement selected = affordable[MBRandom.RandomInt(affordable.Length)];
            trader.ItemRoster.AddToCounts(selected.EquipmentElement, 1);
        }

        int crimeBaseMaximum = Math.Max(2500 + strength / 10, strength * 75 / 100);
        int ordinaryMaximum = Math.Max(1500 + strength / 10, strength / 2);
        trader.PartyTradeGold = GetStaticField("_crimeBase") as Settlement == settlement
            ? crimeBaseMaximum
            : Math.Min(trader.PartyTradeGold, ordinaryMaximum);
        timings[settlement.StringId] = CampaignTime.Now;
        GetDictionary("_crimeValue").Remove(95);
    }

    private void SetHideoutWait(MobileParty actorParty, Settlement settlement, bool waiting)
    {
        RequireBanditSettlement(settlement);
        IDictionary crime = GetDictionary("_crimeValue");
        if (!waiting)
        {
            crime.Remove(9);
            actorParty.IsVisible = true;
            return;
        }

        foreach (MobileParty follower in BanditFollowers().Cast<object>().OfType<MobileParty>())
        {
            follower.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(3f));
            follower.Ai.SetDoNotMakeNewDecisions(true);
            follower.SetMoveModeHold();
            follower.SetMovePatrolAroundSettlement(settlement, follower.NavigationCapability, false);
        }
        crime[9] = 1;
        actorParty.IsVisible = false;
        actorParty.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(3f));
    }

    private void TransferBanditLoot(
        MobileParty actorParty,
        Settlement settlement,
        IEnumerable<FourberieItemSelection> selections)
    {
        RequireBanditSettlement(settlement);
        ItemRoster stash = GetStaticField("_stash") as ItemRoster ??
            throw new InvalidOperationException("the bandit donation stash is unavailable");
        foreach ((EquipmentElement equipment, int delta) in ResolveItems(selections))
        {
            if (delta <= 0 || ExactItemCount(actorParty.ItemRoster, equipment) < delta)
                throw new InvalidOperationException("the donated loot changed before the transfer");
            actorParty.ItemRoster.AddToCounts(equipment, -delta);
            stash.AddToCounts(equipment, delta);
        }
        GetDictionary("_crimeValue")[554] = 0;
    }

    private static void RepairActorShips(Hero actor, MobileParty actorParty)
    {
        int price = 0;
        foreach (Ship ship in actorParty.Ships)
        {
            if (ship == null || ship.HitPoints >= ship.MaxHitPoints) continue;
            price = checked(price + (int)Campaign.Current.Models.ShipCostModel.GetShipRepairCost(ship, actorParty.Party));
        }
        if (price <= 0) throw new InvalidOperationException("the controller has no damaged ships");
        if (actor.Gold < price) throw new InvalidOperationException("the controller cannot afford the ship repairs");

        foreach (Ship ship in actorParty.Ships)
        {
            if (ship == null || ship.HitPoints >= ship.MaxHitPoints) continue;
            float repaired = ship.MaxHitPoints - ship.HitPoints;
            SkillLevelingManager.OnShipRepaired(ship, repaired);
            ship.HitPoints = ship.MaxHitPoints;
        }
        GiveGoldAction.ApplyBetweenCharacters(actor, null, price, false);
    }

    private void HealActor(Hero actor, MobileParty actorParty)
    {
        if (actor.IsHealthFull()) throw new InvalidOperationException("the controller is already at full health");
        Town nearest = SettlementHelper.FindNearestTownToMobileParty(actorParty, MobileParty.NavigationType.Default, null);
        if (nearest == null) throw new InvalidOperationException("no town is available to price the treatment");
        int price = 1000 + (int)nearest.Prosperity / 100;
        Settlement crimeBase = GetStaticField("_crimeBase") as Settlement;
        IDictionary crime = GetDictionary("_crimeValue");
        if (crimeBase?.IsTown == true && crime.Contains(3) &&
            Convert.ToBoolean(RequiredMethod(BehaviorTypeName, "PaymasterCond", 0).Invoke(null, null)))
            price /= 2;
        else if (crimeBase?.IsHideout == true && ReadInt(crime, 560) > 2)
            price /= 2;
        if (actor.Gold < price) throw new InvalidOperationException("the controller cannot afford treatment");

        int healed = Math.Min((int)actorParty.Party.HealingRateForMemberHeroes,
            actor.CharacterObject.MaxHitPoints() - actor.HitPoints);
        if (healed <= 0) throw new InvalidOperationException("the controller cannot recover health right now");
        GiveGoldAction.ApplyBetweenCharacters(actor, null, price, false);
        actor.HitPoints += healed;
        SkillLevelingManager.OnHeroHealedWhileWaiting(actor, healed);
    }

    private IList BanditFollowers() => GetStaticField("_banditsFollowers") as IList ??
        throw new InvalidOperationException("Fourberie bandit follower state is unavailable");

    private MobileParty ResolveBanditParty(string partyId)
    {
        if (string.IsNullOrEmpty(partyId) || !objectManager.TryGetObject(partyId, out MobileParty party) ||
            party == null || !party.IsActive || !party.IsBandit || party.MapFaction?.Culture == null)
            throw new InvalidOperationException("the selected bandit party is unavailable");
        return party;
    }

    private void ReleaseAllBanditFollowers(MobileParty actorParty)
    {
        IList followers = BanditFollowers();
        foreach (MobileParty follower in followers.Cast<object>().OfType<MobileParty>().ToArray())
            ReleaseBanditFollower(actorParty, follower);
        followers.Clear();
    }

    private void ApplyBanditFollowers(MobileParty actorParty, IEnumerable<string> partyIds)
    {
        IList followers = BanditFollowers();
        foreach (string partyId in partyIds)
        {
            MobileParty party = ResolveBanditParty(partyId);
            if (followers.Contains(party)) continue;
            CampaignVec2 position = party.Position;
            if (position.Distance(actorParty.Position) > 25f || party.MapEvent != null ||
                party.CurrentSettlement != null || party.IsBanditBossParty || party.LeaderHero != null)
                throw new InvalidOperationException("the selected bandit party is not eligible to follow the controller");
            int strengthCost = checked(party.MemberRoster.TotalManCount * 10);
            if (BanditStrength(party.MapFaction.Culture.StringId) < strengthCost)
                throw new InvalidOperationException("the selected bandit faction is too weak to provide this follower");
            followers.Add(party);
            party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(3f));
            party.Ai.SetDoNotMakeNewDecisions(true);
            if (actorParty.CurrentSettlement != null) party.SetMoveModeHold();
            else
            {
                if (!party.HasLandNavigationCapability) party.SetLandNavigationAccess(true);
                party.SetMoveEscortParty(actorParty, actorParty.NavigationCapability, false);
            }
            ApplyBanditDiplomacy(party.MapFaction.Culture.StringId, -strengthCost, false, 0, false, party.MapFaction);
            actorParty.RecentEventsMorale += party.MemberRoster.TotalManCount / 5f;
        }
    }

    private void ReleaseBanditFollower(MobileParty actorParty, MobileParty party)
    {
        IList followers = BanditFollowers();
        if (!followers.Contains(party)) throw new InvalidOperationException("the selected party is not following the controller");
        followers.Remove(party);
        party.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(1f));
        party.Ai.SetDoNotMakeNewDecisions(false);
        party.RecalculateShortTermBehavior();
        ApplyBanditDiplomacy(
            party.MapFaction.Culture.StringId,
            checked(party.MemberRoster.TotalManCount * 5),
            false,
            0,
            false,
            party.MapFaction);
    }

    private void ApplyRefuseBanditJoin(Hero actor, MobileParty actorParty, MobileParty party)
    {
        if (party.ActualClan?.Culture == null)
            throw new InvalidOperationException("the encountered bandit faction is unavailable");
        CampaignVec2 position = party.Position;
        if (position.Distance(actorParty.Position) > 5f)
            throw new InvalidOperationException("the bandit party is no longer in encounter range");
        Settlement nearest = SettlementHelper.FindNearestSettlementToMobileParty(actorParty, MobileParty.NavigationType.Default,
            value => !value.IsHideout && value.OwnerClan != null && value.OwnerClan != actor.Clan &&
                     !value.OwnerClan.IsRebelClan && value.OwnerClan.MapFaction?.IsKingdomFaction == true);
        if (nearest?.OwnerClan?.MapFaction != null)
            ChangeCrimeRatingAction.Apply(nearest.OwnerClan.MapFaction, 100f, true);
        ApplyBanditDiplomacy(party.ActualClan.Culture.StringId, 100, true, 10, true, party.MapFaction);
    }

    private void ApplyBanditTruce(Hero actor, Settlement settlement, bool accept, int relationPenalty)
    {
        RequireBanditSettlement(settlement);
        string cultureId = settlement.Culture.StringId;
        IDictionary values = GetDictionary("_stringIntDico");
        string key = "FoTruce" + cultureId;
        if (accept)
        {
            if (!actor.IsKingdomLeader) throw new InvalidOperationException("only a kingdom leader may negotiate this truce");
            InvokeBanditStance(cultureId, true, true);
            values[key] = 0;
            return;
        }
        if (relationPenalty != 0) ApplyBanditDiplomacy(cultureId, 0, false, relationPenalty, true, settlement.MapFaction);
        values.Remove(key);
        InvokeBanditStance(cultureId, false, relationPenalty == 0);
    }

    private void ApplyWarDog(Hero actor, MobileParty actorParty, Settlement settlement, string kingdomId)
    {
        RequireBanditSettlement(settlement);
        if (actor.MapFaction?.IsKingdomFaction == true)
            throw new InvalidOperationException("war-dog service is available only while independent");
        if (!objectManager.TryGetObject(kingdomId, out Kingdom kingdom) || kingdom == null || kingdom.IsEliminated ||
            kingdom == actorParty.MapFaction)
            throw new InvalidOperationException("the selected war-dog kingdom is unavailable");
        IDictionary values = GetDictionary("_stringIntDico");
        RemoveWarDog(values, GetDictionary("_crimeValue"), GetDictionary("_campaignTimeDictio"));
        int award = Campaign.Current.Models.MinorFactionsModel.GetMercenaryAwardFactorToJoinKingdom(actor.Clan, kingdom, false);
        values["FWarDog" + kingdom.StringId] = award;
        foreach (IFaction enemy in kingdom.FactionsAtWarWith.Where(value => value?.IsKingdomFaction == true))
            values["UnleaOn" + enemy.StringId] = 0;
        GetDictionary("_crimeValue")[100] = 0;
        GetDictionary("_campaignTimeDictio")[100] = CampaignTime.Now;
    }

    private static void RemoveWarDog(IDictionary values, IDictionary crime, IDictionary times)
    {
        foreach (object key in values.Keys.Cast<object>().Where(value => value?.ToString()?.Contains("FWarDog") == true ||
                     value?.ToString()?.Contains("UnleaOn") == true).ToArray())
            values.Remove(key);
        crime.Remove(100);
        times.Remove(100);
    }

    private void ApplyCoveShip(MobileParty actorParty, Settlement settlement, string hullId)
    {
        RequireBanditSettlement(settlement);
        if (!objectManager.TryGetObject(hullId, out ShipHull hull) || hull == null || (int)hull.Type == 2)
            throw new InvalidOperationException("the selected cove ship is unavailable");
        IDictionary values = GetDictionary("_stringIntDico");
        string stockKey = settlement.StringId + "CovShip";
        int stock = values.Contains(stockKey) ? Convert.ToInt32(values[stockKey]) : 3;
        int strengthCost = (int)((float)hull.Value / 13f);
        if (stock <= 0 || BanditStrength(settlement.Culture.StringId) <= strengthCost)
            throw new InvalidOperationException("the cove cannot provide this ship");
        ApplyBanditDiplomacy(settlement.Culture.StringId, -strengthCost, false, 0, false, settlement.MapFaction);
        ChangeShipOwnerAction.ApplyByTransferring(actorParty.Party, new Ship(hull));
        values[stockKey] = stock - 1;
    }

    private static void TransferFollowerShip(MobileParty actorParty, MobileParty follower, string selection)
    {
        string[] parts = selection.Split('.');
        bool fromActor = parts[0] == "actor";
        int index = int.Parse(parts[1], CultureInfo.InvariantCulture);
        MobileParty source = fromActor ? actorParty : follower;
        MobileParty destination = fromActor ? follower : actorParty;
        if (index < 0 || index >= source.Ships.Count) throw new InvalidOperationException("the selected ship changed");
        Ship ship = source.Ships[index];
        if (fromActor && actorParty.Ships.Count <= 1 || !fromActor && follower.IsCurrentlyAtSea && follower.Ships.Count <= 1)
            throw new InvalidOperationException("the source party must retain a navigable ship");
        if (fromActor && (int)ship.ShipHull.Type == 2)
            throw new InvalidOperationException("bandit followers cannot use heavy ships");
        ship.Owner = destination.Party;
    }

    private void DonatePrisoners(
        Hero actor,
        MobileParty actorParty,
        Settlement settlement,
        IEnumerable<FourberieTroopSelection> selections)
    {
        RequireBanditSettlement(settlement);
        var resolved = ResolveTroops(selections).ToArray();
        if (resolved.Sum(value => value.Count) < 5 || resolved.Any(value => value.Troop.IsHero))
            throw new InvalidOperationException("bandit donations require at least five non-hero prisoners");
        int strength = 0;
        foreach ((CharacterObject troop, int count) in resolved)
        {
            if (actorParty.PrisonRoster.GetTroopCount(troop) < count)
                throw new InvalidOperationException("the donated prisoner roster changed");
            strength = checked(strength + count * Math.Min(60, Math.Max(1, troop.Tier) * 4));
        }
        foreach ((CharacterObject troop, int count) in resolved)
            actorParty.PrisonRoster.AddToCounts(troop, -count, false, 0, 0, true, -1);
        actor.AddSkillXp(DefaultSkills.Roguery, 100f);
        ApplyBanditDiplomacy(settlement.Culture.StringId, strength, true, 0, true, settlement.MapFaction);
        Settlement nearest = SettlementHelper.FindNearestSettlementToSettlement(settlement, MobileParty.NavigationType.Default,
            value => !value.IsHideout);
        if (nearest?.OwnerClan?.MapFaction != null && !nearest.OwnerClan.IsRebelClan &&
            nearest.OwnerClan.MapFaction.Leader != actor)
            ChangeCrimeRatingAction.Apply(nearest.MapFaction, Math.Min(strength / 20f, 100f), true);
    }

    private void CommitBanditRoster(
        Hero actor,
        MobileParty actorParty,
        MobileParty banditParty,
        IEnumerable<FourberieRosterSelection> selections,
        bool recruitWholeParty)
    {
        CampaignVec2 position = banditParty.Position;
        if (position.Distance(actorParty.Position) > 5f)
            throw new InvalidOperationException("the bandit roster party is no longer in encounter range");
        var resolved = selections.Select(value =>
        {
            if (!objectManager.TryGetObject(value.TroopId, out CharacterObject troop) || troop == null)
                throw new InvalidOperationException("a bandit roster troop is unavailable");
            return (Troop: troop, value.MemberDeltaToActor, value.PrisonerDeltaToActor);
        }).ToArray();
        int recruitmentStrengthCost = recruitWholeParty
            ? checked(banditParty.MemberRoster.TotalManCount * 10)
            : 0;
        if (recruitWholeParty)
        {
            if (BanditStrength(banditParty.MapFaction.Culture.StringId) < recruitmentStrengthCost)
                throw new InvalidOperationException("the bandit faction is too weak for this recruitment");
        }
        foreach (var value in resolved)
        {
            if (value.MemberDeltaToActor > 0 && banditParty.MemberRoster.GetTroopCount(value.Troop) < value.MemberDeltaToActor ||
                value.MemberDeltaToActor < 0 && actorParty.MemberRoster.GetTroopCount(value.Troop) < -value.MemberDeltaToActor ||
                value.PrisonerDeltaToActor > 0 && banditParty.PrisonRoster.GetTroopCount(value.Troop) < value.PrisonerDeltaToActor ||
                value.PrisonerDeltaToActor < 0 && actorParty.PrisonRoster.GetTroopCount(value.Troop) < -value.PrisonerDeltaToActor)
                throw new InvalidOperationException("the bandit roster changed before the transfer");
        }
        foreach (var value in resolved)
        {
            TransferRosterCount(banditParty.MemberRoster, actorParty.MemberRoster, value.Troop, value.MemberDeltaToActor);
            TransferRosterCount(banditParty.PrisonRoster, actorParty.PrisonRoster, value.Troop, value.PrisonerDeltaToActor);
        }
        foreach (TroopRosterElement prisoner in banditParty.PrisonRoster.GetTroopRoster().Where(value => value.Character.IsHero).ToArray())
            RequiredMethod("Fourberie.FourbBanditBehavior", "DoHeroPrisoAfterMath", 2)
                .Invoke(null, new object[] { banditParty, prisoner.Character });
        if (recruitWholeParty)
        {
            ApplyBanditDiplomacy(
                banditParty.MapFaction.Culture.StringId,
                -recruitmentStrengthCost,
                false,
                0,
                false,
                banditParty.MapFaction);
        }
        if (recruitWholeParty || banditParty.MemberRoster.TotalManCount == 0)
            DestroyPartyAction.Apply(null, banditParty);
    }

    private static void TransferRosterCount(TroopRoster source, TroopRoster destination, CharacterObject troop, int delta)
    {
        if (delta == 0) return;
        source.AddToCounts(troop, -delta, false, 0, 0, true, -1);
        destination.AddToCounts(troop, delta, false, 0, 0, true, -1);
    }

    private int BanditStrength(string cultureId)
    {
        IDictionary supported = GetDictionary("_supportedBandits");
        return supported.Contains(cultureId) ? Convert.ToInt32(supported[cultureId]) : 0;
    }

    private void ApplyBanditDiplomacy(
        string cultureId,
        int strength,
        bool giveAway,
        int relation,
        bool affectRelation,
        IFaction faction) =>
        RequiredMethod("Fourberie.FourbBanditBehavior", "BanditsDiploLogic", 8).Invoke(
            null,
            new object[] { cultureId, cultureId, strength, giveAway, relation, affectRelation, false, faction });

    private void InvokeBanditStance(string cultureId, bool neutral, bool bypass) =>
        RequiredMethod("Fourberie.FourbBanditBehavior", "BanditMakeStanceWith", 3).Invoke(
            null,
            new object[] { cultureId, neutral, bypass });

    private void ApplyInsideMissionOutcome(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement))
            throw new InvalidOperationException("the mission settlement is no longer current");

        FourberieInsideMissionResult result = FourberieInsideMissionResultCodec.Decode(request.IntValue);
        if (result.Outcome == FourberieInsideMissionOutcome.None)
            throw new InvalidOperationException("the mission result is invalid");

        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
            FourberieInsideMissionAuthority.Commit(
                request.Operation,
                result,
                actor,
                settlement,
                crime,
                heroes,
                ResolveHero,
                (minimum, maximum) => MBRandom.RandomInt(minimum, maximum));
    }

    private Hero ResolveHero(string stableId)
    {
        if (string.IsNullOrEmpty(stableId)) return null;
        return objectManager.TryGetObject(stableId, out Hero hero) ? hero : Hero.Find(stableId);
    }

    private void ApplyFightClubOutcome(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Town == null)
            throw new InvalidOperationException("the fight-club town is no longer current");
        FourberieFightClubResult result = FourberieFightClubResultCodec.Decode(request.IntValue);
        Hero patron = string.IsNullOrEmpty(request.TargetId) ? null : ResolveHero(request.TargetId);
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        if (ReadInt(crime, 900) != result.FightType ||
            result.PatronTrial && ReadInt(crime, 951) != result.TrialFightType)
            throw new InvalidOperationException("the fight selection changed before the result arrived");
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
            FourberieFightClubAuthority.Commit(
                result,
                actor,
                settlement,
                patron,
                crime,
                heroes,
                assembly);
    }

    private void ApplyFightClubLifecycle(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Town == null)
            throw new InvalidOperationException("the fight-club town is no longer current");
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        IDictionary times = GetDictionary("_campaignTimeDictio");
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            switch (request.Operation)
            {
                case FourberieOperation.EnrollFightClub:
                    if (!crime.Contains(950))
                        throw new InvalidOperationException("the player has not qualified for the fight club");
                    Hero newProtector = ResolveHero(request.TargetId);
                    if (newProtector == null || newProtector.IsHumanPlayerCharacter)
                        throw new InvalidOperationException("the selected stable protector is invalid");
                    if (heroes.Contains("pitProtector") &&
                        ResolveHero(heroes["pitProtector"] as string) is Hero oldProtector &&
                        oldProtector != newProtector && !oldProtector.IsHumanPlayerCharacter)
                        ChangeRelationAction.ApplyPlayerRelation(oldProtector, -(5 + FightClubTier(ReadInt(crime, 950))), true, true);
                    heroes["pitProtector"] = newProtector.StringId;
                    break;
                case FourberieOperation.OwnFightClubStable:
                    if (!crime.Contains(950)) crime[950] = 500;
                    if (heroes.Contains("pitProtector") &&
                        ResolveHero(heroes["pitProtector"] as string) is Hero previousProtector &&
                        !previousProtector.IsHumanPlayerCharacter)
                    {
                        if (!string.Equals(request.TargetId, previousProtector.StringId, StringComparison.Ordinal))
                            throw new InvalidOperationException("the stable protector changed before ownership transfer");
                        ChangeRelationAction.ApplyPlayerRelation(previousProtector, -(5 + FightClubTier(ReadInt(crime, 950))), true, true);
                    }
                    else if (!string.IsNullOrEmpty(request.TargetId))
                        throw new InvalidOperationException("the stable ownership target is no longer canonical");
                    heroes["pitProtector"] = actor.StringId;
                    break;
                case FourberieOperation.RefuteFightClubPatron:
                    if (!heroes.Contains("pitPatron") ||
                        !string.Equals(heroes["pitPatron"] as string, request.TargetId, StringComparison.Ordinal) ||
                        ResolveHero(request.TargetId) is not Hero patron)
                        throw new InvalidOperationException("the selected patron is no longer canonical");
                    ChangeRelationAction.ApplyPlayerRelation(patron, -10, true, true);
                    heroes.Remove("pitPatron");
                    break;
                case FourberieOperation.StartFightClubMatch:
                    if (!crime.Contains(950))
                        throw new InvalidOperationException("the player is not enrolled in the fight club");
                    FourberieFightClubResult result = FourberieFightClubResultCodec.Decode(request.IntValue);
                    crime[900] = result.FightType;
                    if (!result.Training && !result.GangTrial && !result.PatronTrial && crime.Contains(901))
                    {
                        string remaining = Convert.ToString(crime[901], CultureInfo.InvariantCulture)
                            .Replace(result.FightType.ToString(CultureInfo.InvariantCulture), string.Empty);
                        crime[901] = string.IsNullOrEmpty(remaining)
                            ? 0
                            : int.Parse(remaining, CultureInfo.InvariantCulture);
                    }
                    if (result.GangTrial && result.FameDelta == 1)
                    {
                        MethodInfo traitXp = RequiredMethod(
                            "Fourberie.VanillaHelperFourb",
                            "AddPlayerTraitXPAndLogEntry",
                            4);
                        object note = Enum.ToObject(traitXp.GetParameters()[2].ParameterType, 0);
                        traitXp.Invoke(null, new object[] { DefaultTraits.Valor, 10, note, actor });
                    }
                    if (result.PatronTrial)
                    {
                        if (settlement.Owner == null ||
                            !string.Equals(request.TargetId, settlement.Owner.StringId, StringComparison.Ordinal))
                            throw new InvalidOperationException("the patron trial owner changed before admission");
                        crime[951] = result.TrialFightType;
                        times[951] = CampaignTime.Now;
                    }
                    else crime.Remove(951);
                    break;
            }
        }
    }

    private void ApplyFightClubStableRecruitment(
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Town == null)
            throw new InvalidOperationException("the fight-club town is no longer current");
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        if (!heroes.Contains("pitProtector") ||
            ResolveHero(heroes["pitProtector"] as string) is not Hero protector ||
            !protector.IsHumanPlayerCharacter)
            throw new InvalidOperationException("the player does not own the fight-club stable");

        var selected = ResolveTroops(request.Troops).ToArray();
        int total = selected.Sum(value => value.Count);
        int current = ReadInt(crime, 920);
        if (total <= 0 || current + total > 50)
            throw new InvalidOperationException("the stable fighter cap changed before recruitment");
        foreach ((CharacterObject troop, int count) in selected)
        {
            if (troop.IsHero || troop.IsNotTransferableInHideouts)
                throw new InvalidOperationException("a selected stable fighter is not transferable");
            if (actorParty.MemberRoster.GetTroopCount(troop) + actorParty.PrisonRoster.GetTroopCount(troop) < count)
                throw new InvalidOperationException("the selected fighter roster changed before recruitment");
        }

        FourberieReplicationGuard.EnsureEnabled();
        foreach ((CharacterObject troop, int count) in selected)
        {
            int members = Math.Min(count, actorParty.MemberRoster.GetTroopCount(troop));
            if (members > 0)
                actorParty.MemberRoster.AddToCounts(troop, -members, false, 0, 0, true, -1);
            int prisoners = count - members;
            if (prisoners > 0)
                actorParty.PrisonRoster.AddToCounts(troop, -prisoners, false, 0, 0, true, -1);
        }
        crime[920] = current + total;
    }

    private void ApplyFightClubMenuRefresh(Hero actor, MobileParty actorParty, string settlementId)
    {
        if (!TryResolveCurrentSettlement(actorParty, settlementId, out Settlement settlement) || settlement.Town == null)
            throw new InvalidOperationException("the fight-club town is no longer current");
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        IDictionary times = GetDictionary("_campaignTimeDictio");
        MethodInfo fightDay = RequiredMethod("Fourberie.FourbFightClubBehavior", "GetPitFightDay", 1);
        object[] dayArguments = { false };
        fightDay.Invoke(null, dayArguments);
        if (dayArguments[0] is not bool isFightNow || !isFightNow) return;

        Hero protector = heroes.Contains("pitProtector") ? ResolveHero(heroes["pitProtector"] as string) : null;
        Hero guest = heroes.Contains("pitGuest") ? ResolveHero(heroes["pitGuest"] as string) : null;
        if (ReadInt(crime, 901) == 0 && crime.Contains(903))
        {
            int performance = ReadInt(crime, 903);
            if (performance > 0)
            {
                GainRenownAction.Apply(actor, performance, false);
                if (protector != null && !protector.IsHumanPlayerCharacter)
                {
                    ChangeRelationAction.ApplyPlayerRelation(protector, performance, true, true);
                    protector.AddPower(performance);
                }
                if (performance > 1)
                {
                    Hero pitOwner = AccessTools.Method(assembly.GetType(BehaviorTypeName, true, false), "GangLeaderPowerInSettlement")?
                        .Invoke(null, new object[] { settlement }) as Hero;
                    if (pitOwner != null && pitOwner != protector && !IsFourberieEnemy(pitOwner))
                        ChangeRelationAction.ApplyPlayerRelation(pitOwner, 2, true, true);
                    if (guest != null && guest != protector && !IsFourberieEnemy(guest))
                        ChangeRelationAction.ApplyPlayerRelation(guest, 2, true, true);
                }
                if (performance > 3)
                    RequiredMethod(BehaviorTypeName, "XpFornoMercyNoHonorinParty", 3)
                        .Invoke(null, new object[] { 0f, actorParty.MemberRoster, false });
            }
            crime.Remove(903);
        }

        float elapsedHours = times.Contains(902) && times[902] is CampaignTime lastFight
            ? lastFight.ElapsedHoursUntilNow
            : float.PositiveInfinity;
        if (elapsedHours <= 8f) return;
        if (protector == null || protector.HomeSettlement?.Culture == settlement.Culture)
        {
            Settlement[] candidates = Campaign.Current.Settlements
                .Where(candidate => candidate.IsTown && candidate != settlement && candidate.Culture != settlement.Culture)
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("no eligible guest fight-club town exists");
            Settlement candidate = candidates[MBRandom.RandomInt(candidates.Length)];
            guest = AccessTools.Method(assembly.GetType(BehaviorTypeName, true, false), "GangLeaderPowerInSettlement")?
                .Invoke(null, new object[] { candidate }) as Hero;
        }
        else guest = protector;
        if (guest == null)
            throw new InvalidOperationException("the nightly guest stable could not be resolved");
        crime[901] = 1435;
        crime[903] = 0;
        heroes["pitGuest"] = guest.StringId;
        times[902] = CampaignTime.Now;
    }

    private bool IsFourberieEnemy(Hero hero) =>
        Convert.ToBoolean(RequiredMethod("Fourberie.FourbValueHelper", "FourbIsEnemy", 1)
            .Invoke(null, new object[] { hero }));

    private static int FightClubTier(int fame) => fame >= 4000 ? 4 : fame >= 2500 ? 3 : fame >= 1000 ? 2 : 1;

    private void ApplyPrisonerEnslavement(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            GetStaticField("_crimeBase") is not Settlement currentBase ||
            currentBase != settlement ||
            GetStaticField("_crimeBaseParty") is not MobileParty baseParty ||
            baseParty.IsActive != true)
            throw new InvalidOperationException("the controller is no longer at the active Fourberie safehouse");

        var selected = ResolveTroops(request.Troops).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("no prisoners were selected");

        foreach ((CharacterObject troop, int count) in selected)
        {
            if ((int)troop.Occupation == 3)
                throw new InvalidOperationException("selected prisoner is not eligible for enslavement");
            if (actorParty.PrisonRoster.GetTroopCount(troop) < count)
                throw new InvalidOperationException("selected prisoner roster changed before enslavement");
        }

        var casualties = selected
            .Select(selection => new TroopRosterElement(selection.Troop) { Number = selection.Count })
            .ToArray();
        var lootFactor = new ExplainedNumber(1f, false, null);
        CharacterObject leader = SkillHelper.GetEffectivePartyLeaderForSkill(actorParty.Party);
        if (leader != null)
            SkillHelper.AddSkillBonusForCharacter(
                DefaultSkillEffects.RogueryLootBonus,
                leader,
                ref lootFactor);

        var lootMethod = RequiredMethod(BehaviorTypeName, "LootCasualties", parameterCount: 2);
        var loot = (lootMethod.Invoke(null, new object[] { casualties, lootFactor.ResultNumber })
                    as IEnumerable<ItemRosterElement>)?.ToArray()
                   ?? Array.Empty<ItemRosterElement>();
        int total = selected.Sum(selection => selection.Count);

        FourberieReplicationGuard.EnsureEnabled();
        using (new BarterPlayerContext(actor, actorParty))
        {
            foreach ((CharacterObject troop, int count) in selected)
                actorParty.PrisonRoster.AddToCounts(troop, -count, false, 0, 0, true, -1);
            foreach (ItemRosterElement item in loot)
                actorParty.ItemRoster.AddToCounts(item.EquipmentElement, item.Amount);
            Increment(GetDictionary("_crimeValue"), 1500, total);
            actor.AddSkillXp(DefaultSkills.Roguery, 100f);
        }
    }

    private void ApplySafehouseItemTransfer(
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            GetStaticField("_crimeBase") is not Settlement currentBase || currentBase != settlement ||
            GetStaticField("_crimeBaseParty") is not MobileParty baseParty || baseParty.IsActive != true ||
            baseParty.ItemRoster == null || ReferenceEquals(baseParty.ItemRoster, actorParty.ItemRoster))
            throw new InvalidOperationException("the controller is no longer at the active Fourberie safehouse");

        var selections = ResolveItems(request.Items).ToArray();
        if (!FourberieSafehouseItemTransferAuthority.CanApply(
                selections,
                equipment => ExactItemCount(actorParty.ItemRoster, equipment),
                equipment => ExactItemCount(baseParty.ItemRoster, equipment),
                out string failure))
            throw new InvalidOperationException(failure);

        // Keep Coop's roster patches live: these are authoritative server mutations and their
        // deltas must reach the player and the safehouse roster on every rendered client.
        FourberieReplicationGuard.EnsureEnabled();
        foreach ((EquipmentElement equipment, int delta) in selections)
        {
            actorParty.ItemRoster.AddToCounts(equipment, -delta);
            baseParty.ItemRoster.AddToCounts(equipment, delta);
        }
    }

    private void ApplySafehouseReturn(MobileParty actorParty, string settlementId)
    {
        if (!TryResolveCurrentSettlement(actorParty, settlementId, out Settlement settlement))
            throw new InvalidOperationException("the controller is no longer at the selected safehouse");
        Settlement currentBase = GetStaticField("_crimeBase") as Settlement;
        IDictionary crime = GetDictionary("_crimeValue");
        if (!FourberieSafehouseReturnAuthority.CanComplete(
                settlementId,
                currentBase?.StringId,
                settlement.StringId,
                settlement.IsTown,
                crime,
                out string failure))
            throw new InvalidOperationException(failure);

        FourberieSafehouseReturnAuthority.Commit(crime);
    }

    private void ApplySafehouseWait(
        MobileParty actorParty,
        string settlementId,
        FourberieOperation operation)
    {
        if (!TryResolveCurrentSettlement(actorParty, settlementId, out Settlement settlement))
            throw new InvalidOperationException("the controller is no longer at the selected safehouse");
        Settlement currentBase = GetStaticField("_crimeBase") as Settlement;
        IDictionary crime = GetDictionary("_crimeValue");
        if (!FourberieSafehouseWaitAuthority.CanChangeWaitState(
                settlementId,
                currentBase?.StringId,
                settlement.StringId,
                settlement.IsTown,
                crime,
                out string failure))
            throw new InvalidOperationException(failure);

        var followers = (GetStaticField("_banditsFollowers") as IEnumerable)?
            .Cast<object>()
            .OfType<MobileParty>()
            .Where(party => party.IsActive)
            .ToArray() ?? Array.Empty<MobileParty>();
        bool waiting = operation == FourberieOperation.StartSafehouseWait;
        using (new AllowedThread())
        {
            foreach (MobileParty follower in followers)
            {
                if (follower.Ai == null) continue;
                if (waiting)
                {
                    follower.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(3f));
                    follower.Ai.SetDoNotMakeNewDecisions(true);
                    follower.SetMoveModeHold();
                    follower.SetMovePatrolAroundSettlement(
                        settlement, follower.NavigationCapability, false);
                }
                else
                {
                    // The original never released this flag, permanently freezing retained followers.
                    follower.Ai.SetDoNotMakeNewDecisions(false);
                    follower.Ai.RethinkAtNextHourlyTick = true;
                }
            }

            FourberieSafehouseWaitAuthority.Commit(crime, waiting);
            actorParty.IsVisible = !waiting;
            if (waiting) actorParty.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(3f));
        }
    }

    private void ApplySafehouseEstablishment(
        Hero actor,
        MobileParty actorParty,
        string settlementId)
    {
        if (!TryResolveCurrentSettlement(actorParty, settlementId, out Settlement settlement) ||
            settlement.Culture == null)
            throw new InvalidOperationException("the controller is no longer at the selected safehouse site");

        Settlement previousBase = GetStaticField("_crimeBase") as Settlement;
        MobileParty baseParty = GetStaticField("_crimeBaseParty") as MobileParty;
        bool firstBase = previousBase == null;
        if (!FourberieSafehouseEstablishmentAuthority.CanEstablish(
                settlementId,
                settlement.StringId,
                settlement.IsHideout,
                ReadInt(GetDictionary("_stringIntDico"), settlement.Culture.StringId),
                previousBase?.StringId,
                previousBase?.IsHideout == true,
                out string failure))
            throw new InvalidOperationException(failure);
        if (!firstBase && previousBase?.IsTown != true)
            throw new InvalidOperationException("the existing Fourberie base cannot be migrated to a safehouse");
        if (!firstBase && baseParty?.IsActive != true)
            throw new InvalidOperationException("the Fourberie base-party state is inconsistent");

        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            SetStaticField("_crimeBase", settlement);
            // Full base abandonment intentionally retains the virtual party. Reuse that canonical
            // roster when present; only create a party for a genuinely fresh or inactive base.
            if (firstBase && baseParty?.IsActive != true)
            {
                baseParty = RequiredMethod(BehaviorTypeName, "CreateVirtualParty", parameterCount: 2)
                    .Invoke(null, new object[]
                    {
                        "fb_crimebase_party",
                        new TextObject("{=FoSafHou23}Your lads"),
                    }) as MobileParty;
                if (baseParty == null)
                    throw new InvalidOperationException("Fourberie did not create the safehouse party");
                SetStaticField("_crimeBaseParty", baseParty);
            }

            FourberieSafehouseEstablishmentAuthority.Commit(
                GetDictionary("_crimeValue"),
                GetDictionary("_stringHeroIdDico"),
                firstBase,
                MBRandom.RandomInt(1, 5));
        }
    }

    private void ApplySafehouseTrader(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement))
            throw new InvalidOperationException("the controller is no longer at the selected safehouse");

        Settlement currentBase = GetStaticField("_crimeBase") as Settlement;
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary times = GetDictionary("_campaignTimeDictio");
        float elapsedDays = times.Contains(556) && times[556] is CampaignTime lastVisit
            ? lastVisit.ElapsedDaysUntilNow
            : float.PositiveInfinity;
        if (!FourberieSafehouseTraderAuthority.CanExecute(
                request.SettlementId,
                currentBase?.StringId,
                settlement.StringId,
                settlement.IsTown,
                crime,
                elapsedDays,
                out string accessFailure))
            throw new InvalidOperationException(accessFailure);

        MethodInfo ransom = RequiredMethod("Fourberie.FourbSafeHouseBehavior", "GetRansomValueOfSlaves", 1);
        MethodInfo regionWealth = RequiredMethod(BehaviorTypeName, "RegionWealth", 0);
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            if (!FourberieSafehouseTraderAuthority.TryCreatePlan(
                    crime,
                    request.Operation,
                    quantity => Convert.ToInt32(ransom.Invoke(null, new object[] { quantity })),
                    () => Convert.ToSingle(regionWealth.Invoke(null, null)),
                    out FourberieSafehouseTraderPlan plan,
                    out string planFailure))
                throw new InvalidOperationException(planFailure);

            FourberieSafehouseTraderAuthority.Commit(crime, times, plan, CampaignTime.Now);
            if (plan.GoldReward > 0)
                GiveGoldAction.ApplyBetweenCharacters(null, actor, plan.GoldReward, false);
        }
    }

    private void ApplyEnlistment(
        MobileParty actorParty,
        MobileParty sourceParty,
        NetworkRequestFourberieOperation request,
        bool requireCurrentSettlement)
    {
        if (request.Troops.Length == 0)
            throw new InvalidOperationException("no troops were selected");
        if (requireCurrentSettlement && !TryResolveCurrentSettlement(actorParty, request.SettlementId, out _))
            throw new InvalidOperationException("the controller is no longer in the selected settlement");

        int low = 0;
        int middle = 0;
        int high = 0;
        foreach ((CharacterObject troop, int count) in ResolveTroops(request.Troops))
        {
            if ((int)troop.Occupation == 3 || (int)troop.Occupation == 16 || troop.Tier < 1 || troop.Tier > 6)
                throw new InvalidOperationException("selected troop is not eligible for agent training");
            if (sourceParty.MemberRoster.GetTroopCount(troop) < count)
                throw new InvalidOperationException("selected source roster changed before enlistment");
            if (troop.Tier < 3) low += count;
            else if (troop.Tier < 5) middle += count;
            else high += count;
        }

        using (new AllowedThread())
        {
            foreach ((CharacterObject troop, int count) in ResolveTroops(request.Troops))
                sourceParty.MemberRoster.AddToCounts(troop, -count, false, 0, 0, true, -1);
            IDictionary crime = GetDictionary("_crimeValue");
            Increment(crime, 301, low);
            Increment(crime, 311, middle);
            Increment(crime, 321, high);
        }
    }

    private void ApplyBanditRecruitment(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Culture == null)
            throw new InvalidOperationException("the selected bandit settlement is no longer current");
        int total = request.Troops.Sum(selection => selection.Count);
        if (request.IntValue <= 0 || total <= 0 || total > request.IntValue)
            throw new InvalidOperationException("bandit selection exceeds the confirmed maximum");
        if (actorParty.MemberRoster.TotalManCount + total > actorParty.Party.PartySizeLimit)
            throw new InvalidOperationException("the controller party has no room for the selected recruits");

        string settlementId = settlement.StringId;
        string cultureId = settlement.Culture.StringId;
        IDictionary availability = GetDictionary("_stringIntDico");
        IDictionary support = GetDictionary("_supportedBandits");
        if (ReadInt(availability, settlementId) < total)
            throw new InvalidOperationException("the settlement no longer has enough recruits");
        if (ReadInt(support, cultureId) < total * 5)
            throw new InvalidOperationException("the bandit faction no longer has enough strength");

        var resolved = ResolveTroops(request.Troops).ToArray();
        foreach ((CharacterObject troop, _) in resolved)
            if (!IsBanditRecruitEligible(troop, cultureId))
                throw new InvalidOperationException("selected troop is not valid for this bandit culture");

        MethodInfo diplomacy = RequiredMethod(
            "Fourberie.FourbBanditBehavior",
            "BanditsDiploLogic",
            parameterCount: 8);
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            foreach ((CharacterObject troop, int count) in resolved)
                actorParty.MemberRoster.AddToCounts(troop, count, false, 0, 0, true, -1);
            availability[settlementId] = ReadInt(availability, settlementId) - total;
            diplomacy.Invoke(null, new object[]
            {
                cultureId,
                settlement.Culture.Name?.ToString() ?? cultureId,
                -(total * 5),
                false,
                0,
                false,
                false,
                null,
            });
            actor.AddSkillXp(DefaultSkills.Roguery, total);
            actor.AddSkillXp(DefaultSkills.Tactics, total);
        }
    }

    private void ApplyInsuranceScam(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (GetStaticField("_insucaraF") is MobileParty activeCaravan && activeCaravan.IsActive ||
            GetStaticField("_insubandF") is MobileParty activeBandits && activeBandits.IsActive)
            throw new InvalidOperationException("an insurance scam is already active");
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            !objectManager.TryGetObject(request.TargetId, out Hero merchant) ||
            !objectManager.TryGetObject(request.SecondaryTargetId, out Settlement destination) ||
            merchant == null || destination == null || merchant.CurrentSettlement != settlement ||
            destination == settlement || !destination.IsTown)
            throw new InvalidOperationException("the insurance-scam context is stale or invalid");

        MethodInfo spawnCaravan = RequiredMethod("Fourberie.HelperSubInsuScam", "SpawnCaravan", 2);
        MethodInfo spawnBandits = RequiredMethod("Fourberie.HelperSubInsuScam", "SpawnBandits", 1);
        MethodInfo traitXp = RequiredMethod("Fourberie.VanillaHelperFourb", "AddPlayerTraitXPAndLogEntry", 4);
        int partyCount = Math.Min(actorParty.MemberRoster.TotalHealthyCount, MBRandom.RandomInt(73, 83));
        if (partyCount <= 0) throw new InvalidOperationException("the controller party has no healthy escort force");

        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            Type noteType = traitXp.GetParameters()[2].ParameterType;
            object note = Enum.ToObject(noteType, 0);
            traitXp.Invoke(null, new object[] { DefaultTraits.Honor, -5, note, actor });
            traitXp.Invoke(null, new object[] { DefaultTraits.Mercy, -5, note, actor });
            spawnCaravan.Invoke(null, new object[] { merchant, destination });
            spawnBandits.Invoke(null, new object[] { partyCount });

            IDictionary crime = GetDictionary("_crimeValue");
            crime[91] = actor.MapFaction != null && settlement.MapFaction != null &&
                        actor.MapFaction.IsAtWarWith(settlement.MapFaction) ? 1 : 2;
            GetDictionary("_stringHeroIdDico")["insuScamMerchant"] = merchant.StringId;
            GetDictionary("_townInsuScamTiming")[settlement.StringId] = CampaignTime.Now;
        }
    }

    private void ApplyBusinessStart(Hero actor, int businessKey)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        using (new AllowedThread())
        {
            if (!FourberieEnterpriseAuthority.TryStart(
                    crime, businessKey, actor.Gold, out int goldCost, out string failure))
                throw new InvalidOperationException(failure);
            if (goldCost > 0)
                GiveGoldAction.ApplyBetweenCharacters(actor, null, goldCost, false);
        }
    }

    private void ApplyBusinessUpgrade(int businessKey)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        int partnerships = CountStaticCollection("_partnershipList");
        int territories = CountStaticCollection("_territoryList");
        using (new AllowedThread())
            if (!FourberieEnterpriseAuthority.TryUpgrade(
                    crime, businessKey, partnerships, territories, out string failure))
                throw new InvalidOperationException(failure);
    }

    private void ApplyBusinessDowngrade(int businessKey)
    {
        using (new AllowedThread())
            if (!FourberieEnterpriseAuthority.TryDowngrade(
                    GetDictionary("_crimeValue"), businessKey, out string failure))
                throw new InvalidOperationException(failure);
    }

    private void ApplyCrimeRoomSetting(FourberieOperation operation, int value)
    {
        using (new AllowedThread())
            if (!FourberieCrimeRoomAuthority.TrySet(
                    GetDictionary("_crimeValue"), operation, value, out string failure))
                throw new InvalidOperationException(failure);
    }

    private void ApplyContractOperation(
        Hero actor,
        MobileParty actorParty,
        FourberieOperation operation)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        using (new AllowedThread())
        {
            if (operation == FourberieOperation.EnableContractOffers ||
                operation == FourberieOperation.DisableContractOffers)
            {
                if (!FourberieContractAuthority.TrySetOffers(
                        crime,
                        heroes,
                        operation == FourberieOperation.EnableContractOffers,
                        out string failure))
                    throw new InvalidOperationException(failure);
                return;
            }

            if (operation == FourberieOperation.AcceptContractProposal ||
                operation == FourberieOperation.DeclineContractProposal)
            {
                EnsureContractController(actor, actorParty, heroes);
                if (!FourberieContractAuthority.CanRespondToProposal(crime, heroes, out string responseFailure))
                    throw new InvalidOperationException(responseFailure);
                int responseCooldown = MBRandom.RandomInt(7, 13);
                if (operation == FourberieOperation.AcceptContractProposal)
                    FourberieContractAuthority.CommitAccept(crime, responseCooldown);
                else
                    FourberieContractAuthority.CommitDecline(crime, heroes, responseCooldown);
                return;
            }

            if (!FourberieContractAuthority.TryPlanAbort(
                    crime, heroes, out string giverId, out string abortFailure) ||
                !objectManager.TryGetObject(giverId, out Hero giver) || giver == null || !giver.IsAlive)
                throw new InvalidOperationException(abortFailure ?? "the Fourberie contract giver is unavailable");

            int cooldown = MBRandom.RandomInt(7, 13);
            using (new BarterPlayerContext(actor, actorParty))
                ChangeRelationAction.ApplyPlayerRelation(giver, -5, true, true);
            FourberieContractAuthority.CommitAbort(crime, heroes, cooldown);
        }
    }

    private void EnsureContractController(Hero actor, MobileParty actorParty, IDictionary heroes)
    {
        string enforcerId = heroes?.Contains("enforcer") == true ? heroes["enforcer"] as string : null;
        if (string.IsNullOrEmpty(enforcerId) ||
            !objectManager.TryGetObject(enforcerId, out Hero enforcer) || enforcer == null ||
            enforcer.Clan != actor.Clan || enforcer.PartyBelongedTo != actorParty ||
            actorParty.MemberRoster.GetTroopCount(enforcer.CharacterObject) <= 0)
            throw new InvalidOperationException("the authenticated controller no longer owns the Fourberie enforcer");
    }

    private void ApplyMainCrimeBase(string settlementId)
    {
        string failure = null;
        if (!objectManager.TryGetObject(settlementId, out Settlement settlement) || settlement == null ||
            !FourberieTerritoryAuthority.CanMakeMainBase(
                GetStaticField("_territoryList") as IEnumerable,
                settlementId,
                settlement.IsTown,
                out failure))
            throw new InvalidOperationException(failure ?? "the selected Fourberie territory is unavailable");

        using (new AllowedThread())
        {
            SetStaticField("_crimeBase", settlement);
            FourberieTerritoryAuthority.CommitMainBase(
                GetDictionary("_campaignTimeDictio"),
                GetDictionary("_stringHeroIdDico"),
                CampaignTime.Now);
        }
    }

    private void ApplyTerritoryRemoval(string settlementId, FourberieOperation operation)
    {
        if (!objectManager.TryGetObject(settlementId, out Settlement settlement) || settlement == null ||
            GetStaticField("_territoryList") is not IList territories)
            throw new InvalidOperationException("the selected Fourberie territory is unavailable");
        Settlement currentBase = GetStaticField("_crimeBase") as Settlement;
        string currentBaseId = currentBase?.StringId;

        using (new AllowedThread())
        {
            if (operation == FourberieOperation.RemoveTerritory)
            {
                if (!FourberieTerritoryAuthority.CanRemoveNonBase(
                        territories, settlementId, currentBaseId, out string failure))
                    throw new InvalidOperationException(failure);
                FourberieTerritoryAuthority.CommitRemove(territories, settlementId);
                return;
            }

            if (!FourberieTerritoryAuthority.CanAbandonBase(
                    territories, settlementId, currentBaseId, out string abandonFailure))
                throw new InvalidOperationException(abandonFailure);
            FourberieTerritoryAuthority.CommitAbandonBase(
                territories,
                settlementId,
                GetDictionary("_crimeValue"),
                GetDictionary("_stringHeroIdDico"),
                GetDictionary("_campaignTimeDictio"));
            SetStaticField("_crimeBase", null);
        }
    }

    private void ApplySafehouseAbandonment(Hero actor, MobileParty actorParty, string settlementId)
    {
        if (!TryResolveCurrentSettlement(actorParty, settlementId, out Settlement settlement) ||
            settlement.Culture == null)
            throw new InvalidOperationException("the selected Fourberie safehouse is no longer current");

        Settlement currentBase = GetStaticField("_crimeBase") as Settlement;
        if (!FourberieTerritoryAuthority.CanAbandonSafehouse(
                settlementId,
                currentBase?.StringId,
                actorParty.CurrentSettlement?.StringId,
                settlement.IsTown,
                out string failure))
            throw new InvalidOperationException(failure);

        IDictionary crime = GetDictionary("_crimeValue");
        int slaveStrength = FourberieTerritoryAuthority.SafehouseSlaveStrength(crime);
        MethodInfo diplomacy = RequiredMethod(
            "Fourberie.FourbBanditBehavior",
            "BanditsDiploLogic",
            parameterCount: 8);

        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            diplomacy.Invoke(null, new object[]
            {
                settlement.Culture.StringId,
                settlement.Culture.Name?.ToString() ?? settlement.Culture.StringId,
                slaveStrength,
                true,
                0,
                true,
                false,
                null,
            });
            FourberieTerritoryAuthority.CommitAbandonSafehouse(
                crime,
                GetDictionary("_stringHeroIdDico"),
                GetDictionary("_campaignTimeDictio"));
            SetStaticField("_crimeBase", null);
        }
    }

    private int PrepareGrudgeQuote(Hero actor, string clanId)
    {
        if (actor == null || string.IsNullOrEmpty(actor.StringId) ||
            !objectManager.TryGetObject(clanId, out Clan clan) || clan == null || clan.IsEliminated ||
            clan == actor.Clan || clan.Leader == null)
            throw new InvalidOperationException("the selected Fourberie grudge target is unavailable");

        IDictionary grudges = GetDictionary("_stringClanDico");
        int grudge = ReadInt(grudges, clanId);
        if (!FourberieGrudgeAuthority.TryQuote(
                actor.Gold,
                clan.Gold,
                grudge,
                MBRandom.RandomInt(
                    FourberieGrudgeAuthority.MinimumRandomSurcharge,
                    FourberieGrudgeAuthority.MaximumRandomSurcharge + 1),
                out int amount,
                out string failure))
            throw new InvalidOperationException(failure);

        grudgeQuotes[actor.StringId] = new GrudgeQuote(clanId, grudge, amount);
        return amount;
    }

    private Hero ResolveGrudgeRecipient(string clanId)
    {
        if (!objectManager.TryGetObject(clanId, out Clan clan) || clan == null || clan.IsEliminated ||
            clan.Leader == null)
            throw new InvalidOperationException("the Fourberie grudge recipient is unavailable");
        return clan.Leader;
    }

    private void ApplyGrudgeSettlement(Hero actor, string clanId, int amount, Hero recipient)
    {
        if (actor == null || string.IsNullOrEmpty(actor.StringId) ||
            !grudgeQuotes.TryGetValue(actor.StringId, out GrudgeQuote quote) ||
            !string.Equals(quote.ClanId, clanId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject(clanId, out Clan clan) || clan == null || clan.IsEliminated ||
            clan == actor.Clan || clan.Leader != recipient)
            throw new InvalidOperationException("the Fourberie grudge quote is missing or stale");

        IDictionary grudges = GetDictionary("_stringClanDico");
        IDictionary crime = GetDictionary("_crimeValue");
        if (!FourberieGrudgeAuthority.CanSettle(
                grudges,
                crime,
                clanId,
                quote.Grudge,
                actor.Gold,
                amount,
                quote.Amount,
                out string failure))
            throw new InvalidOperationException(failure);

        using (new AllowedThread())
        {
            FourberieGrudgeAuthority.Commit(grudges, crime, clanId);
            GiveGoldAction.ApplyBetweenCharacters(actor, recipient, amount, false);
            grudgeQuotes.Remove(actor.StringId);
        }
    }

    private void ApplySchemeBonus(
        Hero actor,
        MobileParty actorParty,
        FourberieOperation operation,
        int slot)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        using (new AllowedThread())
        {
            string failure;
            bool applied;
            if (operation == FourberieOperation.UpgradeSchemeBonus)
            {
                Hero victim = ResolveMappedHero("victim" + slot) ??
                              throw new InvalidOperationException("scheme victim is unavailable");
                Kingdom kingdom = victim.Clan?.Kingdom ??
                                  throw new InvalidOperationException("scheme victim has no kingdom");
                int network;
                using (new BarterPlayerContext(actor, actorParty))
                    network = Convert.ToInt32(RequiredMethod(
                        BehaviorTypeName, "SchemeNet", parameterCount: 1).Invoke(null, new object[] { kingdom }));
                applied = FourberieSchemeBonusAuthority.TryUpgrade(
                    crime,
                    GetDictionary("_stringClanDico"),
                    slot,
                    kingdom.StringId,
                    ComputeSchemeBase(),
                    network,
                    out failure);
            }
            else if (operation == FourberieOperation.DowngradeSchemeBonus)
            {
                applied = FourberieSchemeBonusAuthority.TryDowngrade(crime, slot, out failure);
            }
            else
            {
                applied = FourberieSchemeBonusAuthority.TryReset(crime, slot, out failure);
            }

            if (!applied) throw new InvalidOperationException(failure);
        }
    }

    private Hero ResolveMappedHero(string tag)
    {
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        string heroId = heroes.Contains(tag) ? heroes[tag] as string : null;
        return !string.IsNullOrEmpty(heroId) && objectManager.TryGetObject(heroId, out Hero hero)
            ? hero
            : null;
    }

    private int ComputeSchemeBase()
    {
        Hero enforcer = ResolveMappedHero("enforcer");
        if (enforcer == null) return 0;
        return FourberieSchemeBonusAuthority.ComputeBase(
            enforcer.GetSkillValue(DefaultSkills.Roguery),
            enforcer.GetSkillValue(DefaultSkills.Tactics),
            enforcer.GetTraitLevel(DefaultTraits.Valor),
            enforcer.GetTraitLevel(DefaultTraits.Mercy),
            enforcer.GetTraitLevel(DefaultTraits.Honor),
            enforcer.GetTraitLevel(DefaultTraits.Calculating));
    }

    private void ApplyAgentParty(Hero actor, MobileParty actorParty, FourberieOperation operation)
    {
        if (!CanManageAgentParty(actor, actorParty))
            throw new InvalidOperationException("controller is not at a valid agent-management location");

        IDictionary crime = GetDictionary("_crimeValue");
        CharacterObject saboteur = objectManager.TryGetObject("fb_saboteur_tier_1", out CharacterObject resolved)
            ? resolved
            : throw new InvalidOperationException("Fourberie saboteur troop is unavailable");

        using (new AllowedThread())
        {
            if (operation == FourberieOperation.CreateAgentParty)
            {
                if (GetStaticField("_agentsParty") != null)
                    throw new InvalidOperationException("saboteur party already exists");
                if (!FourberieAgentPartyAuthority.TryTakeForCreate(crime, out int count, out string failure))
                    throw new InvalidOperationException(failure);

                MobileParty party;
                using (new BarterPlayerContext(actor, actorParty))
                    party = RequiredMethod(BehaviorTypeName, "CreateVirtualParty", parameterCount: 2)
                        .Invoke(null, new object[]
                        {
                            "fb_saboteurs_party",
                            new TextObject("{=FoAgeOp17}Saboteurs"),
                        }) as MobileParty;
                if (party == null) throw new InvalidOperationException("Fourberie did not create the saboteur party");
                SetStaticField("_agentsParty", party);
                party.MemberRoster.Clear();
                party.MemberRoster.AddToCounts(saboteur, count, false, 0, 0, true, -1);
                return;
            }

            if (GetStaticField("_agentsParty") is not MobileParty existing || !existing.IsActive)
                throw new InvalidOperationException("saboteur party is unavailable");
            if (operation == FourberieOperation.RefillAgentParty)
            {
                if (!FourberieAgentPartyAuthority.TryTakeForRefill(
                        crime, existing.MemberRoster.TotalManCount, out int count, out string failure))
                    throw new InvalidOperationException(failure);
                existing.MemberRoster.AddToCounts(saboteur, count, false, 0, 0, true, -1);
                return;
            }

            int saboteurs = 0;
            int others = 0;
            foreach (TroopRosterElement element in existing.MemberRoster.GetTroopRoster())
            {
                if (element.Character == saboteur) saboteurs += element.Number;
                else others += element.Number;
            }
            if (!FourberieAgentPartyAuthority.TryReturnDisbanded(
                    crime, saboteurs, others, out string disbandFailure))
                throw new InvalidOperationException(disbandFailure);
            DestroyPartyAction.Apply(null, existing);
            SetStaticField("_agentsParty", null);
        }
    }

    private bool CanManageAgentParty(Hero actor, MobileParty actorParty)
    {
        Settlement current = actorParty.CurrentSettlement;
        if (current == null) return false;
        IDictionary crime = GetDictionary("_crimeValue");
        if (crime.Contains(550)) return true;
        if (GetStaticField("_crimeBase") is Settlement crimeBase && current == crimeBase) return true;
        if (current.IsTown)
        {
            bool territory = (GetStaticField("_territoryList") as IEnumerable)?
                .Cast<object>()
                .Any(value => string.Equals(value as string, current.StringId, StringComparison.Ordinal)) == true;
            return territory || actor.Clan?.Fiefs.Contains(current.Town) == true;
        }
        if (current.IsCastle) return actor.Clan?.Settlements.Contains(current) == true;
        return true;
    }

    private void ApplyCrimeBaseReset(Hero actor, MobileParty actorParty)
    {
        if (GetStaticField("_crimeBase") is not Settlement crimeBase ||
            actorParty.CurrentSettlement != crimeBase)
            throw new InvalidOperationException("controller is not at the Fourberie crime base");
        if (GetStaticField("_crimeBaseParty") is not MobileParty party)
            return;
        if (!party.IsActive) throw new InvalidOperationException("crime-base party is inactive");

        using (new AllowedThread())
        {
            DestroyPartyAction.Apply(null, party);
            MobileParty replacement;
            using (new BarterPlayerContext(actor, actorParty))
                replacement = RequiredMethod(BehaviorTypeName, "CreateVirtualParty", parameterCount: 2)
                    .Invoke(null, new object[]
                    {
                        "fb_crimebase_party",
                        new TextObject("{=FoSafHou23}Your lads"),
                    }) as MobileParty;
            if (replacement == null)
                throw new InvalidOperationException("Fourberie did not recreate the crime-base party");
            SetStaticField("_crimeBaseParty", replacement);
        }
    }

    private void ApplyCriminalRole(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        IDictionary roles = GetDictionary("_stringHeroIdDico");
        if (request.Operation == FourberieOperation.RemoveCriminalRole)
        {
            using (new AllowedThread())
                if (!FourberieRoleAuthority.TryRemove(roles, request.IntValue, out string failure))
                    throw new InvalidOperationException(failure);
            return;
        }

        if (!objectManager.TryGetObject(request.TargetId, out Hero target) || target == null ||
            target == actor || target.IsHumanPlayerCharacter || target.Clan != actor.Clan ||
            target.PartyBelongedTo != actorParty ||
            actorParty.MemberRoster.GetTroopCount(target.CharacterObject) <= 0 ||
            !target.CanMoveToSettlement())
            throw new InvalidOperationException("selected criminal-role hero is no longer eligible");

        string role = FourberieRoleAuthority.RoleName(request.IntValue);
        string otherRole = role == "paymaster" ? "enforcer" : "paymaster";
        if (roles.Contains(role) && string.Equals(roles[role] as string, target.StringId, StringComparison.Ordinal) ||
            roles.Contains(otherRole) && string.Equals(roles[otherRole] as string, target.StringId, StringComparison.Ordinal))
            throw new InvalidOperationException("selected hero already holds a Fourberie role");

        using (new AllowedThread())
            if (!FourberieRoleAuthority.TryAssign(
                    roles, request.IntValue, target.StringId, out string failure))
                throw new InvalidOperationException(failure);
    }

    private void ApplySchemeOperation(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        IDictionary crime = GetDictionary("_crimeValue");
        IDictionary heroes = GetDictionary("_stringHeroIdDico");
        IDictionary times = GetDictionary("_campaignTimeDictio");

        using (new AllowedThread())
        {
            if (request.Operation == FourberieOperation.ChangeSchemeStance)
            {
                if (!FourberieSchemeAuthority.TryChangeStance(
                        crime, request.IntValue, out bool changed, out string failure))
                    throw new InvalidOperationException(failure);
                if (changed)
                {
                    heroes.Remove("victim7");
                    heroes.Remove("victim8");
                    times.Remove(7);
                    times.Remove(8);
                    SetStaticField("_clanval", null);
                    SetStaticField("_clanval2", null);
                }
                return;
            }

            if (request.Operation == FourberieOperation.SelectSchemeVictim)
            {
                ApplySchemeVictimSelection(heroes, crime, request);
                return;
            }

            if (request.Operation == FourberieOperation.SelectSchemeType)
            {
                if (!FourberieSchemeAuthority.TryDecodeSelection(
                        request.IntValue, out int slot, out int scheme))
                    throw new InvalidOperationException("invalid scheme selection");
                Hero victim = ResolveMappedHero("victim" + slot) ??
                              throw new InvalidOperationException("scheme victim is unavailable");
                EnsureSchemeTarget(actor, actorParty, victim, scheme);
                if (crime.Contains(slot * 100 + 40) || crime.Contains(slot * 100 + 41))
                    throw new InvalidOperationException("scheme selection cannot change during its lifecycle");
                crime[slot] = scheme;
                return;
            }

            int lifecycleSlot = request.IntValue;
            if (request.Operation == FourberieOperation.AbortScheme)
            {
                if (!FourberieSchemeAuthority.TryAbort(crime, lifecycleSlot, out string failure))
                    throw new InvalidOperationException(failure);
                heroes.Remove("victim" + lifecycleSlot);
                times.Remove(lifecycleSlot);
                return;
            }
            if (request.Operation == FourberieOperation.ClearCompletedScheme)
            {
                if (!FourberieSchemeAuthority.TryClearCompleted(crime, lifecycleSlot, out string failure))
                    throw new InvalidOperationException(failure);
                heroes.Remove("victim" + lifecycleSlot);
                times.Remove(lifecycleSlot);
                return;
            }

            Hero target = ResolveMappedHero("victim" + lifecycleSlot) ??
                          throw new InvalidOperationException("scheme victim is unavailable");
            int selectedScheme = ReadInt(crime, lifecycleSlot);
            EnsureSchemeTarget(actor, actorParty, target, selectedScheme);
            int rank = target.IsFactionLeader ? 3 : target.IsClanLeader ? 2 : 1;
            int randomOffset = MBRandom.RandomInt(0, 2);
            if (!FourberieSchemeAuthority.TryPlan(
                    selectedScheme, rank, randomOffset, out var plan, out string planFailure))
                throw new InvalidOperationException(planFailure);
            if (!FourberieSchemeAuthority.TryStart(
                    crime, lifecycleSlot, actor.Gold, plan, out string startFailure))
                throw new InvalidOperationException(startFailure);

            times[lifecycleSlot] = CampaignTime.Now;
            if (plan.Fee > 0)
                GiveGoldAction.ApplyBetweenCharacters(actor, null, plan.Fee, false);

            object[] arguments = { lifecycleSlot, selectedScheme, false, null, 0, 0, 0, 0 };
            int successChance;
            using (new BarterPlayerContext(actor, actorParty))
                successChance = Convert.ToInt32(RequiredMethod(
                    BehaviorTypeName, "SchemeSucc", parameterCount: 8).Invoke(null, arguments));
            int coverage = Convert.ToInt32(arguments[4]);
            bool success = selectedScheme == 7 || MBRandom.RandomInt(0, 101) > 100 - successChance;
            bool detected = !success && MBRandom.RandomInt(0, 101) > 100 - coverage;
            FourberieSchemeAuthority.SetOutcome(crime, lifecycleSlot, selectedScheme, success, detected);
        }
    }

    private void ApplySchemeVictimSelection(
        IDictionary heroes,
        IDictionary crime,
        NetworkRequestFourberieOperation request)
    {
        int slot = request.IntValue;
        if (crime.Contains(slot * 100 + 40) || crime.Contains(slot * 100 + 41))
            throw new InvalidOperationException("scheme victim cannot change during its lifecycle");
        if (!objectManager.TryGetObject(request.TargetId, out Hero target) || target == null ||
            !target.IsAlive || target.IsChild || target.Clan == null)
            throw new InvalidOperationException("selected scheme victim is no longer eligible");

        string otherId = heroes.Contains("victim" + (slot == 7 ? 8 : 7))
            ? heroes["victim" + (slot == 7 ? 8 : 7)] as string
            : null;
        if (string.Equals(otherId, target.StringId, StringComparison.Ordinal))
            throw new InvalidOperationException("the same hero cannot occupy both scheme slots");

        heroes["victim" + slot] = target.StringId;
    }

    private void EnsureSchemeTarget(Hero actor, MobileParty actorParty, Hero victim, int scheme)
    {
        if (victim == null || !victim.IsAlive || victim.IsChild || victim.Clan?.Kingdom == null)
            throw new InvalidOperationException("scheme victim is no longer eligible");

        int network;
        using (new BarterPlayerContext(actor, actorParty))
            network = Convert.ToInt32(RequiredMethod(
                BehaviorTypeName, "SchemeNet", parameterCount: 1).Invoke(null, new object[] { victim.Clan.Kingdom }));
        bool eligible = FourberieSchemeAuthority.IsTargetEligible(
            scheme,
            victim.Clan.Influence >= 200f,
            victim.CanDie((KillCharacterAction.KillCharacterActionDetail)1),
            victim.IsClanLeader,
            victim.IsPartyLeader,
            victim.MapFaction?.IsAtWarWith(actorParty.MapFaction) == true,
            network);
        if (!eligible)
            throw new InvalidOperationException("scheme target requirements changed before execution");
    }

    private IEnumerable<(CharacterObject Troop, int Count)> ResolveTroops(
        IEnumerable<FourberieTroopSelection> selections)
    {
        foreach (FourberieTroopSelection selection in selections)
        {
            if (!objectManager.TryGetObject(selection.TroopId, out CharacterObject troop) || troop == null)
                throw new InvalidOperationException("selected troop no longer exists");
            yield return (troop, selection.Count);
        }
    }

    private IEnumerable<(EquipmentElement Equipment, int Delta)> ResolveItems(
        IEnumerable<FourberieItemSelection> selections)
    {
        foreach (FourberieItemSelection selection in selections)
        {
            if (!objectManager.TryGetObject(selection.ItemId, out ItemObject item) || item == null)
                throw new InvalidOperationException("selected item no longer exists");
            ItemModifier modifier = null;
            if (!string.IsNullOrEmpty(selection.ItemModifierId) &&
                (!objectManager.TryGetObject(selection.ItemModifierId, out modifier) || modifier == null))
                throw new InvalidOperationException("selected item modifier no longer exists");
            yield return (new EquipmentElement(item, modifier), selection.DeltaToSafehouse);
        }
    }

    private static int ExactItemCount(ItemRoster roster, EquipmentElement equipment)
    {
        if (roster == null) return 0;
        int index = roster.FindIndexOfElement(equipment);
        return index < 0 ? 0 : roster.GetElementCopyAtIndex(index).Amount;
    }

    private bool IsBanditRecruitEligible(CharacterObject troop, string cultureId)
    {
        if (troop.Culture?.StringId != cultureId) return false;
        if (troop.Tier == 2) return true;

        Type listType = assembly.GetType("Fourberie.ListHelper", throwOnError: true, ignoreCase: false);
        if (AccessTools.Field(listType, "_troopList")?.GetValue(null) is not IEnumerable list) return false;
        foreach (object entry in list)
        {
            Type tupleType = entry?.GetType();
            string item1 = tupleType?.GetField("m_Item1", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string ??
                           tupleType?.GetProperty("Item1")?.GetValue(entry) as string;
            string item2 = tupleType?.GetField("m_Item2", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string ??
                           tupleType?.GetProperty("Item2")?.GetValue(entry) as string;
            if (item1 == cultureId && item2 == troop.StringId) return true;
        }
        return false;
    }

    private bool TryResolveCurrentSettlement(
        MobileParty actorParty,
        string settlementId,
        out Settlement settlement)
    {
        settlement = null;
        return !string.IsNullOrEmpty(settlementId) &&
               objectManager.TryGetObject(settlementId, out settlement) && settlement != null &&
               actorParty.CurrentSettlement == settlement;
    }

    private Dictionary<CharacterObject, int> CaptureCounts(
        TroopRoster roster,
        IEnumerable<FourberieTroopSelection> selections)
    {
        if (roster == null) return null;
        return ResolveTroops(selections).ToDictionary(value => value.Troop, value => roster.GetTroopCount(value.Troop));
    }

    private static void RestoreCounts(TroopRoster roster, Dictionary<CharacterObject, int> counts)
    {
        if (roster == null || counts == null) return;
        using (new AllowedThread())
            foreach (var pair in counts)
                roster.AddToCounts(pair.Key, pair.Value - roster.GetTroopCount(pair.Key), false, 0, 0, true, -1);
    }

    private static Dictionary<CharacterObject, int> CaptureAllCounts(TroopRoster roster) =>
        roster?.GetTroopRoster().ToDictionary(element => element.Character, element => element.Number);

    private static ItemRosterElement[] CaptureAllItems(ItemRoster roster) =>
        roster == null
            ? null
            : Enumerable.Range(0, roster.Count)
                .Select(roster.GetElementCopyAtIndex)
                .ToArray();

    private static void RestoreItems(ItemRoster roster, ItemRosterElement[] elements)
    {
        if (roster == null || elements == null) return;
        using (new AllowedThread())
        {
            roster.Clear();
            roster.Add(elements);
        }
    }

    private static void RestoreGold(Hero actor, int previousGold)
    {
        int difference = previousGold - actor.Gold;
        if (difference == 0) return;
        using (new AllowedThread())
        {
            if (difference > 0) GiveGoldAction.ApplyBetweenCharacters(null, actor, difference, false);
            else GiveGoldAction.ApplyBetweenCharacters(actor, null, -difference, false);
        }
    }

    private static void TryDestroyCreated(
        MobileParty candidate,
        MobileParty previous,
        ICollection<string> errors)
    {
        if (candidate == null || ReferenceEquals(candidate, previous) || !candidate.IsActive) return;
        try
        {
            using (new AllowedThread()) DestroyPartyAction.Apply(null, candidate);
        }
        catch (Exception exception)
        {
            errors.Add("created party " + candidate.StringId + ": " + exception.Message);
        }
    }

    private MethodInfo RequiredMethod(string typeName, string methodName, int parameterCount)
    {
        Type type = assembly.GetType(typeName, throwOnError: true, ignoreCase: false);
        MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                                          candidate.GetParameters().Length == parameterCount);
        return method ?? throw new MissingMethodException(typeName, methodName);
    }

    private object GetStaticField(string fieldName)
    {
        Type type = assembly.GetType(BehaviorTypeName, throwOnError: true, ignoreCase: false);
        FieldInfo field = AccessTools.Field(type, fieldName) ??
                          throw new MissingFieldException(BehaviorTypeName, fieldName);
        return field.GetValue(null);
    }

    private void SetStaticField(string fieldName, object value)
    {
        Type type = assembly.GetType(BehaviorTypeName, throwOnError: true, ignoreCase: false);
        FieldInfo field = AccessTools.Field(type, fieldName) ??
                          throw new MissingFieldException(BehaviorTypeName, fieldName);
        field.SetValue(null, value);
    }

    private IDictionary GetDictionary(string fieldName) =>
        GetStaticField(fieldName) as IDictionary ??
        throw new InvalidOperationException("Fourberie field " + fieldName + " is not a dictionary");

    private int CountStaticCollection(string fieldName)
    {
        object value = GetStaticField(fieldName);
        if (value is ICollection collection) return collection.Count;
        if (value is IEnumerable enumerable) return enumerable.Cast<object>().Count();
        throw new InvalidOperationException("Fourberie field " + fieldName + " is not a collection");
    }

    private static int ReadInt(IDictionary dictionary, object key) =>
        dictionary.Contains(key) ? Convert.ToInt32(dictionary[key]) : 0;

    private static void Increment(IDictionary dictionary, object key, int change) =>
        dictionary[key] = ReadInt(dictionary, key) + change;
}

internal enum FourberieReplayDecision
{
    New,
    Replay,
    Conflict,
}

internal sealed class FourberieRequestLedger<TKey>
{
    private sealed class Entry
    {
        public Entry(string key, NetworkFourberieOperationResult result)
        {
            Key = key;
            Result = result;
        }

        public string Key { get; }
        public NetworkFourberieOperationResult Result { get; }
    }

    private readonly object sync = new object();
    private readonly Dictionary<TKey, Dictionary<long, Entry>> entries = new Dictionary<TKey, Dictionary<long, Entry>>();
    private readonly int capacity;

    public FourberieRequestLedger(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public FourberieReplayDecision Inspect(
        TKey peer,
        long requestId,
        string key,
        out NetworkFourberieOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries) ||
                !peerEntries.TryGetValue(requestId, out Entry entry))
            {
                result = null;
                return FourberieReplayDecision.New;
            }

            result = entry.Result;
            return string.Equals(entry.Key, key, StringComparison.Ordinal)
                ? FourberieReplayDecision.Replay
                : FourberieReplayDecision.Conflict;
        }
    }

    public void Record(TKey peer, long requestId, string key, NetworkFourberieOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries))
            {
                peerEntries = new Dictionary<long, Entry>();
                entries.Add(peer, peerEntries);
            }
            if (peerEntries.ContainsKey(requestId)) return;
            if (peerEntries.Count >= capacity)
                peerEntries.Remove(peerEntries.Keys.Min());
            peerEntries.Add(requestId, new Entry(key, result));
        }
    }

    public void Reset()
    {
        lock (sync) entries.Clear();
    }
}

internal static class FourberieCapabilityPolicy
{
    public static bool IsEnabled(bool optionEnabled, bool routeReady) => optionEnabled && routeReady;
}

internal sealed class FourberieCapabilitySource : Core.IWorkshopCapabilitySource
{
    internal const string ModuleId = "Fourberie";
    internal const string Operation = "Gameplay";

    private readonly Configuration.IModConfig modConfig;

    public FourberieCapabilitySource(Configuration.IModConfig modConfig)
    {
        this.modConfig = modConfig;
    }

    public IEnumerable<Core.WorkshopCapability> CaptureCapabilities()
    {
        var options = modConfig.Data == null
            ? Configuration.ModConfigProvider.ModOptions
            : new Configuration.ModOptions(modConfig.Data.ModOptions ?? new Configuration.ModOptionsData());
        bool enabled = FourberieCapabilityPolicy.IsEnabled(
            options.IsWorkshopModuleEnabled(ModuleId),
            FourberiePatchRuntime.Current != null);
        yield return new Core.WorkshopCapability(
            ModuleId,
            Operation,
            enabled,
            enabled ? string.Empty : "Fourberie is disabled or its authoritative gameplay route is unavailable.");
    }
}
