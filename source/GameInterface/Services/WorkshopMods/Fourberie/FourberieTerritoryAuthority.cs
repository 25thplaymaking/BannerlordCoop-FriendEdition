using System;
using System.Collections;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieTerritoryAuthority
{
    private static readonly int[] BaseCrimeKeys =
    {
        1, 11, 12,
        2, 21, 22,
        3, 31, 32,
        5, 50, 6, 60, 61,
        500,
    };

    public static bool CanMakeMainBase(
        IEnumerable territories,
        string settlementId,
        bool isTown,
        out string failure)
    {
        bool contains = territories?.Cast<object>().Any(value =>
            string.Equals(value as string, settlementId, StringComparison.Ordinal)) == true;
        if (!isTown || string.IsNullOrEmpty(settlementId) || !contains)
        {
            failure = "the selected Fourberie territory cannot become the main base";
            return false;
        }

        failure = null;
        return true;
    }

    public static void CommitMainBase(IDictionary times, IDictionary heroes, object campaignTime)
    {
        times[500] = campaignTime;
        heroes.Remove("paymaster");
        heroes.Remove("enforcer");
    }

    public static bool CanRemoveNonBase(
        IEnumerable territories,
        string settlementId,
        string currentBaseId,
        out string failure) =>
        CanRemove(territories, settlementId, currentBaseId, requireMainBase: false, out failure);

    public static bool CanAbandonBase(
        IEnumerable territories,
        string settlementId,
        string currentBaseId,
        out string failure) =>
        CanRemove(territories, settlementId, currentBaseId, requireMainBase: true, out failure);

    public static void CommitRemove(IList territories, string settlementId) =>
        territories.Remove(settlementId);

    public static void CommitAbandonBase(
        IList territories,
        string settlementId,
        IDictionary crime,
        IDictionary heroes,
        IDictionary times)
    {
        territories.Remove(settlementId);
        ClearBaseState(crime, heroes, times);
    }

    public static bool CanAbandonSafehouse(
        string settlementId,
        string currentBaseId,
        string currentSettlementId,
        bool isTown,
        out string failure)
    {
        if (isTown || string.IsNullOrEmpty(settlementId) ||
            !string.Equals(settlementId, currentBaseId, StringComparison.Ordinal) ||
            !string.Equals(settlementId, currentSettlementId, StringComparison.Ordinal))
        {
            failure = "the selected Fourberie safehouse abandonment is stale or invalid";
            return false;
        }

        failure = null;
        return true;
    }

    public static int SafehouseSlaveStrength(IDictionary crime) =>
        crime?.Contains(1500) == true ? Math.Max(0, Convert.ToInt32(crime[1500])) : 0;

    public static void CommitAbandonSafehouse(
        IDictionary crime,
        IDictionary heroes,
        IDictionary times)
    {
        if (crime.Contains(557) && Convert.ToInt32(crime[557]) < 10) crime.Remove(557);
        crime.Remove(1500);
        ClearBaseState(crime, heroes, times);
    }

    private static bool CanRemove(
        IEnumerable territories,
        string settlementId,
        string currentBaseId,
        bool requireMainBase,
        out string failure)
    {
        bool listed = territories?.Cast<object>().Any(value =>
            string.Equals(value as string, settlementId, StringComparison.Ordinal)) == true;
        bool isMainBase = string.Equals(settlementId, currentBaseId, StringComparison.Ordinal);
        if (!listed || isMainBase != requireMainBase)
        {
            failure = "the selected Fourberie territory removal is stale or invalid";
            return false;
        }

        failure = null;
        return true;
    }

    private static void ClearBaseState(IDictionary crime, IDictionary heroes, IDictionary times)
    {
        foreach (int key in BaseCrimeKeys) crime.Remove(key);
        heroes.Remove("paymaster");
        heroes.Remove("enforcer");
        times.Remove(500);
        FourberieSchemeAuthority.ClearSlotState(crime, heroes, times, 7);
        FourberieSchemeAuthority.ClearSlotState(crime, heroes, times, 8);
    }
}
