using System;
using System.Collections;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieTerritoryAuthority
{
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
}
