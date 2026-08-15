using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal readonly struct FourberieFightClubResult
{
    public FourberieFightClubResult(
        int fightType, int round, int knockouts, int trialFightType,
        bool training, bool gangTrial, bool patronTrial, bool handToHand, int fameDelta = 0)
    {
        FightType = fightType;
        Round = round;
        Knockouts = knockouts;
        TrialFightType = trialFightType;
        Training = training;
        GangTrial = gangTrial;
        PatronTrial = patronTrial;
        HandToHand = handToHand;
        FameDelta = fameDelta;
    }

    public int FightType { get; }
    public int Round { get; }
    public int Knockouts { get; }
    public int TrialFightType { get; }
    public bool Training { get; }
    public bool GangTrial { get; }
    public bool PatronTrial { get; }
    public bool HandToHand { get; }
    public int FameDelta { get; }
}

internal static class FourberieFightClubResultCodec
{
    public static int Encode(FourberieFightClubResult value) =>
        value.FightType |
        (value.Round << 3) |
        (value.Knockouts << 8) |
        (value.TrialFightType << 13) |
        (value.Training ? 1 << 16 : 0) |
        (value.GangTrial ? 1 << 17 : 0) |
        (value.PatronTrial ? 1 << 18 : 0) |
        (value.HandToHand ? 1 << 19 : 0) |
        ((value.FameDelta + 1024) << 20);

    public static FourberieFightClubResult Decode(int encoded) => new FourberieFightClubResult(
        encoded & 7,
        (encoded >> 3) & 31,
        (encoded >> 8) & 31,
        (encoded >> 13) & 7,
        (encoded & (1 << 16)) != 0,
        (encoded & (1 << 17)) != 0,
        (encoded & (1 << 18)) != 0,
        (encoded & (1 << 19)) != 0,
        ((encoded >> 20) & 2047) - 1024);

    public static bool IsValid(int encoded)
    {
        if (encoded <= 0) return false;
        FourberieFightClubResult value = Decode(encoded);
        return value.FightType is 1 or 3 or 4 or 5 &&
               value.Round is >= 1 and <= 20 && value.Knockouts is >= 0 and <= 20 &&
               value.TrialFightType is >= 0 and <= 5 &&
               (!value.PatronTrial || value.TrialFightType is 1 or 3 or 4 or 5) &&
               !(value.Training && (value.GangTrial || value.PatronTrial)) &&
               !(value.GangTrial && value.PatronTrial) && value.FameDelta is >= -1024 and <= 1023;
    }
}

/// <summary>Executes the pinned fight-club payout and career rules on the authoritative host.</summary>
internal static class FourberieFightClubAuthority
{
    public static void Commit(
        FourberieFightClubResult result,
        Hero actor,
        Settlement settlement,
        Hero patron,
        IDictionary crime,
        IDictionary heroes,
        Assembly assembly)
    {
        if (!FourberieFightClubResultCodec.IsValid(FourberieFightClubResultCodec.Encode(result)) ||
            actor == null || settlement?.Town == null || crime == null || heroes == null || assembly == null)
            throw new InvalidOperationException("fight-club authority context is invalid");
        if (!result.Training && !result.GangTrial && !result.PatronTrial && patron == null && heroes.Contains("pitPatron"))
            throw new InvalidOperationException("the canonical patron could not be resolved");

        Type controller = assembly.GetType("Fourberie.FourbFightClubController", true, false);
        Type behavior = assembly.GetType("Fourberie.FourbFightClubBehavior", true, false);
        FieldInfo gangField = AccessTools.Field(controller, "_isGangTrial");
        bool previousGang = gangField?.GetValue(null) is bool old && old;
        try
        {
            gangField?.SetValue(null, result.GangTrial);
            if (!result.GangTrial && result.FameDelta != 0)
                crime[950] = Math.Max(0, Math.Min(5000, Read(crime, 950) + result.FameDelta));
            if (result.FightType is 1 or 4)
            {
                if (result.Training)
                {
                    GiveGold(actor, (int)settlement.Town.Prosperity / 500);
                    Fame(behavior, 5, result.Round, result.HandToHand);
                }
                else if (result.GangTrial)
                {
                    GainRenownAction.Apply(actor, 1f, false);
                }
                else
                {
                    GiveGold(actor, result.Round * ((int)settlement.Town.Prosperity / 100));
                    Fame(behavior, result.Round * 20, result.Round, result.HandToHand);
                    PayPatron(behavior, patron, result.FightType);
                    GainRenownAction.Apply(actor, 1f, false);
                }
                Increment(crime, 9501);
                Increment(crime, 9503);
            }
            else
            {
                if (result.Knockouts > 0)
                    GiveGold(actor, (int)settlement.Town.Prosperity / Math.Max(10, 100 - result.Knockouts * 10));
                Fame(behavior, 20 * result.FightType, result.Knockouts, result.HandToHand);
                PayPatron(behavior, patron, result.FightType);
                GainRenownAction.Apply(actor, 1f, false);
                Increment(crime, 9501);
            }

            if (result.PatronTrial && settlement.Owner != null)
            {
                heroes["pitPatron"] = settlement.Owner.StringId;
                PayPatron(behavior, settlement.Owner, result.TrialFightType);
            }
            else if (!result.GangTrial && crime.Contains(903)) Increment(crime, 903);

            crime.Remove(900);
            crime.Remove(951);
        }
        finally
        {
            gangField?.SetValue(null, previousGang);
        }
    }

    private static void Fame(Type behavior, int amount, int tier, bool handToHand) =>
        AccessTools.Method(behavior, "PitFightFameGain")?.Invoke(
            null, new object[] { 950, amount, tier, "Authoritative pit result", handToHand });

    private static void PayPatron(Type behavior, Hero patron, int fightType)
    {
        if (patron == null) return;
        object[] arguments = { patron, fightType, true, false };
        AccessTools.Method(behavior, "PatronPaycheck")?.Invoke(null, arguments);
    }

    private static void GiveGold(Hero actor, int amount)
    {
        if (amount > 0) GiveGoldAction.ApplyBetweenCharacters(null, actor, amount, false);
    }

    private static int Read(IDictionary values, int key) =>
        values.Contains(key) ? Convert.ToInt32(values[key]) : 0;

    private static void Increment(IDictionary values, int key) => values[key] = Read(values, key) + 1;
}
