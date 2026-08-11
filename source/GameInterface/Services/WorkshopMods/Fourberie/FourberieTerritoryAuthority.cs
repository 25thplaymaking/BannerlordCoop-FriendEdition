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
        foreach (int key in BaseCrimeKeys) crime.Remove(key);
        heroes.Remove("paymaster");
        heroes.Remove("enforcer");
        times.Remove(500);
        FourberieSchemeAuthority.ClearSlotState(crime, heroes, times, 7);
        FourberieSchemeAuthority.ClearSlotState(crime, heroes, times, 8);
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
}
