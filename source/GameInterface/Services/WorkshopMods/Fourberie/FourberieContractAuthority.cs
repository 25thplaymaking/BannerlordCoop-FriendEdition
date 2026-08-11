using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieContractAuthority
{
    public static bool TrySetOffers(
        IDictionary crime,
        IDictionary heroes,
        bool enabled,
        out string failure)
    {
        if (crime == null || heroes == null)
            return Fail("Fourberie contract state is unavailable", out failure);

        bool active = crime.Contains(200) || heroes.Contains("contractGiver") ||
                      heroes.Contains("contractTarget");
        if (active)
            return Fail("a Fourberie contract is already active", out failure);

        if (enabled) crime[203] = 0;
        else crime.Remove(203);
        failure = null;
        return true;
    }

    public static bool TryPlanAbort(
        IDictionary crime,
        IDictionary heroes,
        out string giverId,
        out string failure)
    {
        giverId = heroes?.Contains("contractGiver") == true
            ? heroes["contractGiver"] as string
            : null;
        string targetId = heroes?.Contains("contractTarget") == true
            ? heroes["contractTarget"] as string
            : null;
        if (crime == null || heroes == null || !crime.Contains(200) ||
            string.IsNullOrEmpty(giverId) || string.IsNullOrEmpty(targetId))
            return Fail("no complete Fourberie contract is active", out failure);

        failure = null;
        return true;
    }

    public static void CommitAbort(IDictionary crime, IDictionary heroes, int cooldown)
    {
        heroes.Remove("contractGiver");
        heroes.Remove("contractTarget");
        crime.Remove(200);
        crime.Remove(201);
        crime.Remove(204);
        crime[204] = cooldown;
    }

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
