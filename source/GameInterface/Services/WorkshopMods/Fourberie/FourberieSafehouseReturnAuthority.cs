using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieSafehouseReturnAuthority
{
    public static bool CanComplete(
        string settlementId,
        string currentBaseId,
        string currentSettlementId,
        bool isTown,
        IDictionary crime,
        out string failure)
    {
        if (isTown || !HasPendingReturn(crime) || string.IsNullOrEmpty(settlementId) ||
            !string.Equals(settlementId, currentBaseId, StringComparison.Ordinal) ||
            !string.Equals(settlementId, currentSettlementId, StringComparison.Ordinal))
        {
            failure = "the safehouse return context is stale or invalid";
            return false;
        }

        failure = null;
        return true;
    }

    public static bool HasPendingReturn(IDictionary crime) =>
        crime?.Contains(550) == true && Convert.ToInt32(crime[550]) == 1;

    public static void Commit(IDictionary crime)
    {
        if (crime == null) throw new ArgumentNullException(nameof(crime));
        crime.Remove(550);
    }
}
