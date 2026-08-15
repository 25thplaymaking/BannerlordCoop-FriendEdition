using System;
using System.Collections;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal enum FourberieInsideMissionOutcome
{
    None,
    Won,
    Escaped,
    Incapacitated,
}

internal readonly struct FourberieInsideMissionResult
{
    public FourberieInsideMissionResult(FourberieInsideMissionOutcome outcome, bool disguised)
    {
        Outcome = outcome;
        Disguised = disguised;
    }

    public FourberieInsideMissionOutcome Outcome { get; }
    public bool Disguised { get; }
}

internal static class FourberieInsideMissionResultCodec
{
    public static int Encode(FourberieInsideMissionOutcome outcome, bool disguised) =>
        (int)outcome + (disguised ? 3 : 0);

    public static FourberieInsideMissionResult Decode(int value)
    {
        bool disguised = value >= 4;
        int outcome = disguised ? value - 3 : value;
        return outcome is >= 1 and <= 3
            ? new FourberieInsideMissionResult((FourberieInsideMissionOutcome)outcome, disguised)
            : default;
    }
}

/// <summary>
/// Server-owned campaign half of Fourberie's inside-mission callbacks. Mission agents and menus
/// remain local to the controlling client; rewards and persistent consequences are recomputed
/// here from the pinned 1.4.7.6 rules, canonical dictionaries, and server RNG.
/// </summary>
internal static class FourberieInsideMissionAuthority
{
    public static void Commit(
        FourberieOperation operation,
        FourberieInsideMissionResult result,
        Hero actor,
        Settlement settlement,
        IDictionary crime,
        IDictionary heroes,
        Func<string, Hero> resolveHero,
        Func<int, int, int> random)
    {
        if (actor == null || settlement == null || crime == null || heroes == null ||
            resolveHero == null || random == null || result.Outcome == FourberieInsideMissionOutcome.None)
            throw new InvalidOperationException("inside-mission authority context is incomplete");

        switch (operation)
        {
            case FourberieOperation.CompleteGrabAndRun:
                CompleteGrabAndRun(result, actor, settlement, crime, heroes, resolveHero, random);
                break;
            case FourberieOperation.CompleteGangLeaderBashing:
                CompleteBashing(result, actor, settlement, crime, heroes, resolveHero);
                break;
            case FourberieOperation.CompleteIsolatedRobbery:
                CompleteIsolatedRobbery(result, actor, settlement, crime, random);
                break;
            case FourberieOperation.CompletePickpocketFight:
                CompletePickpocket(result, actor, settlement, crime);
                break;
            case FourberieOperation.CompleteGrudgeAssassination:
                SetOutcome(crime, result);
                if (result.Outcome == FourberieInsideMissionOutcome.Won) crime.Remove(512);
                if (result.Outcome == FourberieInsideMissionOutcome.Escaped) crime.Remove(104);
                break;
            case FourberieOperation.CompleteTavernBrawl:
                CompleteTavernBrawl(result, actor, settlement, crime, heroes, resolveHero, random);
                break;
            case FourberieOperation.CompleteLarcenyFight:
                CompleteLarceny(result, actor, settlement, crime, heroes, resolveHero, random);
                break;
            case FourberieOperation.CompleteAlleyFight:
                CompleteAlley(result, actor, settlement, crime);
                break;
            default:
                throw new InvalidOperationException("operation is not an inside-mission outcome");
        }
    }

    private static void CompleteGrabAndRun(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime,
        IDictionary heroes, Func<string, Hero> resolveHero, Func<int, int, int> random)
    {
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            ChangeSecurity(settlement, -2f);
            AddXp(actor, 75f, 100f);
            RewardMappedHero(actor, settlement, heroes, "jobGrabRun", resolveHero, random);
            crime.Remove(102);
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated)
        {
            AddCrime(settlement, 10f);
            actor.AddSkillXp(DefaultSkills.Roguery, 25f);
            crime.Remove(102);
            heroes.Remove("jobGrabRun");
        }
        else crime.Remove(104);
        SetOutcome(crime, result);
    }

    private static void CompleteBashing(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime,
        IDictionary heroes, Func<string, Hero> resolveHero)
    {
        if (result.Disguised)
        {
            actor.AddSkillXp(DefaultSkills.Roguery, 500f);
        }
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            int multiplier = result.Disguised ? 2 : 1;
            GiveGold(actor, (2000 + (int)(settlement.Town?.Prosperity ?? 0f) / 10) * multiplier);
            Hero low = MappedHero(heroes, "lowPowerGl", resolveHero);
            Hero high = MappedHero(heroes, "highPowerGl", resolveHero);
            if (low != null && high != null)
            {
                ChangeRelationAction.ApplyPlayerRelation(low, 5, true, true);
                ChangeRelationAction.ApplyPlayerRelation(high, -5, true, true);
            }
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated)
        {
            AddCrime(settlement, 10f);
            actor.AddSkillXp(DefaultSkills.Roguery, 100f);
            Hero low = MappedHero(heroes, "lowPowerGl", resolveHero);
            if (low != null) ChangeRelationAction.ApplyPlayerRelation(low, -1, true, true);
        }
        else crime.Remove(104);
        SetOutcome(crime, result);
    }

    private static void CompleteIsolatedRobbery(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime,
        Func<int, int, int> random)
    {
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            ChangeSecurity(settlement, -5f);
            GiveGold(actor, settlement.IsTown ? random(138, 334) : random(38, 134));
            actor.AddSkillXp(DefaultSkills.Roguery, 75f);
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated)
        {
            AddCrime(settlement, 20f);
            actor.AddSkillXp(DefaultSkills.Roguery, 100f);
        }
        else crime.Remove(104);
        SetOutcome(crime, result);
    }

    private static void CompletePickpocket(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime)
    {
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            ChangeSecurity(settlement, -5f);
            actor.AddSkillXp(DefaultSkills.Roguery, 75f);
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated)
        {
            AddCrime(settlement, 20f);
            actor.AddSkillXp(DefaultSkills.Roguery, 100f);
        }
        else crime.Remove(104);
        SetOutcome(crime, result);
    }

    private static void CompleteTavernBrawl(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime,
        IDictionary heroes, Func<string, Hero> resolveHero, Func<int, int, int> random)
    {
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            if (!result.Disguised) AddCrime(settlement, 2f);
            ChangeSecurity(settlement, -2f);
            AddXp(actor, 75f, 100f);
            RewardMappedHero(actor, settlement, heroes, "jobBrawl", resolveHero, random);
            Hero protector = RewardMappedHero(actor, settlement, heroes, "pitProtectorTest", resolveHero, random);
            if (protector != null)
            {
                heroes["pitProtector"] = protector.StringId;
                Set(crime, 950, 1);
            }
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated)
        {
            AddCrime(settlement, 5f);
            actor.AddSkillXp(DefaultSkills.Roguery, 25f);
            heroes.Remove("jobBrawl");
            heroes.Remove("pitProtectorTest");
        }
        else
        {
            crime.Remove(103);
            return;
        }
        crime.Remove(103);
        SetOutcome(crime, result);
    }

    private static void CompleteLarceny(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime,
        IDictionary heroes, Func<string, Hero> resolveHero, Func<int, int, int> random)
    {
        int job = Read(crime, 105);
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            if (job == 1 || job == 3)
            {
                ChangeSecurity(settlement, -2f);
                if (job == 1 && !result.Disguised) AddCrime(settlement, 2f);
                RewardMappedHero(actor, settlement, heroes, "jobMessenger", resolveHero, random);
                crime.Remove(105);
            }
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated && (job == 1 || job == 3))
        {
            if (job == 1) AddCrime(settlement, 5f);
            Hero giver = MappedHero(heroes, "jobMessenger", resolveHero);
            if (giver?.IsAlive == true) ChangeRelationAction.ApplyPlayerRelation(giver, -2, true, true);
            actor.AddSkillXp(DefaultSkills.Roguery, 25f);
            crime.Remove(105);
            heroes.Remove("jobMessenger");
        }
        SetOutcome(crime, result);
    }

    private static void CompleteAlley(
        FourberieInsideMissionResult result, Hero actor, Settlement settlement, IDictionary crime)
    {
        if (result.Disguised) actor.AddSkillXp(DefaultSkills.Roguery, 500f);
        if (result.Outcome == FourberieInsideMissionOutcome.Won)
        {
            // The alley ownership choice remains a separate vanilla/Fourberie menu transaction.
            // This outcome records completion without guessing a client-only Alley object identity.
            Set(crime, 98, 1);
        }
        else if (result.Outcome == FourberieInsideMissionOutcome.Incapacitated) Set(crime, 98, 2);
    }

    private static Hero RewardMappedHero(
        Hero actor, Settlement settlement, IDictionary heroes, string key,
        Func<string, Hero> resolveHero, Func<int, int, int> random)
    {
        Hero hero = MappedHero(heroes, key, resolveHero);
        if (hero == null || !hero.IsAlive || hero.CurrentSettlement != settlement)
        {
            if (hero?.IsAlive != true) heroes.Remove(key);
            return null;
        }
        ChangeRelationAction.ApplyPlayerRelation(hero, 2, true, true);
        GiveGold(actor, random(100, 300));
        AddXp(actor, 200f, 100f);
        heroes.Remove(key);
        return hero;
    }

    private static Hero MappedHero(IDictionary heroes, string key, Func<string, Hero> resolveHero) =>
        heroes.Contains(key) ? resolveHero(heroes[key] as string) : null;

    private static void SetOutcome(IDictionary crime, FourberieInsideMissionResult result)
    {
        if (result.Outcome == FourberieInsideMissionOutcome.Escaped) return;
        Set(crime, 98, result.Outcome == FourberieInsideMissionOutcome.Won ? 1 : 2);
    }

    private static void AddXp(Hero actor, float roguery, float athletics)
    {
        actor.AddSkillXp(DefaultSkills.Roguery, roguery);
        actor.AddSkillXp(DefaultSkills.Athletics, athletics);
    }

    private static void GiveGold(Hero actor, int amount)
    {
        if (amount > 0) GiveGoldAction.ApplyBetweenCharacters(null, actor, amount, false);
    }

    private static void AddCrime(Settlement settlement, float amount)
    {
        if (settlement?.OwnerClan?.MapFaction != null && settlement.OwnerClan.IsRebelClan == false)
            ChangeCrimeRatingAction.Apply(settlement.MapFaction, amount, true);
    }

    private static void ChangeSecurity(Settlement settlement, float delta)
    {
        if (settlement?.Town != null) settlement.Town.Security += delta;
    }

    private static int Read(IDictionary dictionary, object key) =>
        dictionary.Contains(key) ? Convert.ToInt32(dictionary[key]) : 0;

    private static void Set(IDictionary dictionary, object key, int value) => dictionary[key] = value;
}
