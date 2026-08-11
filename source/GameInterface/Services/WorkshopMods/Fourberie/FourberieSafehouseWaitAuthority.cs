using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieSafehouseWaitAuthority
{
    public static bool CanChangeWaitState(
        string settlementId,
        string currentBaseId,
        string currentSettlementId,
        bool isTown,
        IDictionary crime,
        out string failure)
    {
        if (isTown || crime?.Contains(550) != true ||
            string.IsNullOrEmpty(settlementId) ||
            !string.Equals(settlementId, currentBaseId, StringComparison.Ordinal) ||
            !string.Equals(settlementId, currentSettlementId, StringComparison.Ordinal))
        {
            failure = "the safehouse wait context is stale or invalid";
            return false;
        }

        failure = null;
        return true;
    }

    public static void Commit(IDictionary crime, bool waiting)
    {
        if (crime == null) throw new ArgumentNullException(nameof(crime));
        if (waiting) crime[9] = 1;
        else crime.Remove(9);
    }
}
