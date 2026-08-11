using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>Server-owned state transition for the two pinned Fourberie scheme bonus controls.</summary>
internal static class FourberieSchemeBonusAuthority
{
    public static int ComputeBase(
        int roguery,
        int tactics,
        int valor,
        int mercy,
        int honor,
        int calculating)
    {
        int result = Math.Min(roguery / 10, 20) + Math.Min(tactics / 10, 20);
        if (valor != 0) result += 5;
        if (mercy < 0) result += 5;
        else if (mercy > 0) result -= 5;
        if (honor < 0) result += 5;
        else if (honor > 0) result -= 5;
        if (calculating > 0) result += 5;
        return result;
    }

    public static bool TryUpgrade(
        IDictionary crime,
        IDictionary clanState,
        int slot,
        string kingdomId,
        int schemeBase,
        int schemeNetwork,
        out string failure)
    {
        if (crime == null || clanState == null || !IsSlot(slot) || string.IsNullOrEmpty(kingdomId))
            return Fail("invalid scheme-bonus context", out failure);

        int key = BonusKey(slot);
        int current = Read(crime, key);
        int coveragePenalty = Read(clanState, "FMal_" + kingdomId);
        int limit = Math.Min(schemeBase + schemeNetwork, 90);
        if (coveragePenalty + 5 + current * 5 >= limit)
            return Fail("scheme bonus is already at its server limit", out failure);

        crime[key] = current + 1;
        failure = null;
        return true;
    }

    public static bool TryDowngrade(IDictionary crime, int slot, out string failure)
    {
        if (crime == null || !IsSlot(slot))
            return Fail("invalid scheme-bonus context", out failure);
        int key = BonusKey(slot);
        int current = Read(crime, key);
        if (current <= 0) return Fail("scheme bonus is already at zero", out failure);

        crime[key] = current - 1;
        failure = null;
        return true;
    }

    public static bool TryReset(IDictionary crime, int slot, out string failure)
    {
        if (crime == null || !IsSlot(slot))
            return Fail("invalid scheme-bonus context", out failure);
        crime.Remove(BonusKey(slot));
        failure = null;
        return true;
    }

    private static int BonusKey(int slot) => slot * 100 + 50;
    private static bool IsSlot(int slot) => slot == 7 || slot == 8;
    private static int Read(IDictionary dictionary, object key) =>
        dictionary.Contains(key) ? Convert.ToInt32(dictionary[key]) : 0;

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
