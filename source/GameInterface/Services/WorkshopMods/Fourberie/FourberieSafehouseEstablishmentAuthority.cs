using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieSafehouseEstablishmentAuthority
{
    private static readonly int[] BaseUpgradeKeys =
    {
        1, 11, 12,
        2, 21, 22,
        3, 31, 32,
        5, 50, 6, 60, 61,
    };

    public static bool CanEstablish(
        string settlementId,
        string currentSettlementId,
        bool isHideout,
        int cultureRelation,
        string currentBaseId,
        bool currentBaseIsHideout,
        out string failure)
    {
        if (!isHideout || cultureRelation < 25 || currentBaseIsHideout ||
            string.IsNullOrEmpty(settlementId) ||
            !string.Equals(settlementId, currentSettlementId, StringComparison.Ordinal) ||
            string.Equals(settlementId, currentBaseId, StringComparison.Ordinal))
        {
            failure = "the selected Fourberie safehouse site is stale or ineligible";
            return false;
        }

        failure = null;
        return true;
    }

    public static void Commit(
        IDictionary crime,
        IDictionary heroes,
        bool firstBase,
        int relicLocation)
    {
        if (crime == null) throw new ArgumentNullException(nameof(crime));
        if (heroes == null) throw new ArgumentNullException(nameof(heroes));
        if (relicLocation < 1 || relicLocation > 4)
            throw new ArgumentOutOfRangeException(nameof(relicLocation));

        crime[560] = 1;
        crime[561] = 0;
        if (firstBase)
        {
            crime[500] = 1;
            crime[1500] = 0;
        }

        foreach (int key in BaseUpgradeKeys) crime.Remove(key);
        heroes.Remove("paymaster");
        heroes.Remove("enforcer");
        if (!crime.Contains(1000)) crime[1000] = 0;
        if (!crime.Contains(1001)) crime[1001] = 0;
        if (!crime.Contains(557)) crime[557] = relicLocation;
    }
}
