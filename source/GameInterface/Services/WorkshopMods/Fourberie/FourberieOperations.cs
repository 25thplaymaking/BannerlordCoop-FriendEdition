using Common;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Barters;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieOperationExecutor
{
    private const string BehaviorTypeName = "Fourberie.FourberieBehavior";
    private readonly Assembly assembly;
    private readonly IObjectManager objectManager;

    public FourberieOperationExecutor(Assembly assembly, IObjectManager objectManager)
    {
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.objectManager = objectManager ?? throw new ArgumentNullException(nameof(objectManager));
    }

    public bool TryExecute(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request,
        out string failure)
    {
        failure = null;
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
        MobileParty previousCaravan = GetStaticField("_insucaraF") as MobileParty;
        MobileParty previousBandits = GetStaticField("_insubandF") as MobileParty;
        MobileParty previousAgents = GetStaticField("_agentsParty") as MobileParty;
        bool previousAgentsWasActive = previousAgents?.IsActive == true;
        Dictionary<CharacterObject, int> previousAgentCounts = CaptureAllCounts(previousAgents?.MemberRoster);
        MobileParty previousCrimeBase = GetStaticField("_crimeBaseParty") as MobileParty;
        bool previousCrimeBaseWasActive = previousCrimeBase?.IsActive == true;
        int previousActorGold = actor.Gold;

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

            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    "Fourberie operation rollback failed after " + reported.Message + ": " +
                    string.Join("; ", rollbackErrors), reported);

            failure = reported.GetType().Name + ": " + reported.Message;
            return false;
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
