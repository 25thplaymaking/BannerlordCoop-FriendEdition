using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieGrudgeAuthority
{
    internal const int MinimumRandomSurcharge = 43_456;
    internal const int MaximumRandomSurcharge = 46_788;
    internal const int MaximumPayment = 440_364;

    public static bool TryQuote(
        int actorGold,
        int clanGold,
        int grudge,
        int randomSurcharge,
        out int amount,
        out string failure)
    {
        amount = 0;
        if (actorGold < 0 || clanGold < 0 || grudge < 5 ||
            randomSurcharge < MinimumRandomSurcharge || randomSurcharge > MaximumRandomSurcharge)
        {
            failure = "the selected Fourberie grudge cannot be quoted";
            return false;
        }

        int baseAmount = Math.Min(
            Math.Max(actorGold / 5, clanGold / 10),
            100_000 + randomSurcharge);
        amount = grudge >= 100
            ? baseAmount * 3
            : grudge >= 50
                ? baseAmount * 2
                : grudge >= 25
                    ? baseAmount
                    : baseAmount / 2;
        failure = null;
        return true;
    }

    public static bool CanSettle(
        IDictionary grudges,
        IDictionary crime,
        string clanId,
        int quotedGrudge,
        int actorGold,
        int requestedAmount,
        int quotedAmount,
        out string failure)
    {
        int liveGrudge = grudges?.Contains(clanId) == true ? Convert.ToInt32(grudges[clanId]) : 0;
        int spies = crime?.Contains(310) == true ? Convert.ToInt32(crime[310]) : 0;
        if (string.IsNullOrEmpty(clanId) || liveGrudge < 5 || liveGrudge != quotedGrudge || spies < 1 ||
            requestedAmount < 0 || requestedAmount > MaximumPayment || requestedAmount != quotedAmount ||
            actorGold < requestedAmount)
        {
            failure = "the Fourberie grudge quote is stale or unaffordable";
            return false;
        }

        failure = null;
        return true;
    }

    public static void Commit(IDictionary grudges, IDictionary crime, string clanId)
    {
        grudges.Remove(clanId);
        crime[310] = Convert.ToInt32(crime[310]) - 1;
    }
}
