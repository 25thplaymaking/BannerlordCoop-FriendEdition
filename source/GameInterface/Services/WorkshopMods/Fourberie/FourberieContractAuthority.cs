using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieContractAuthority
{
    internal const int MinimumRewardRandom = 1_345;
    internal const int MaximumRewardRandom = 7_788;

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

    public static bool AdvanceProposalCooldown(IDictionary crime)
    {
        if (crime == null || !crime.Contains(203) || crime.Contains(200) || crime.Contains(202)) return false;
        int cooldown = crime.Contains(204) ? System.Convert.ToInt32(crime[204]) : 0;
        if (cooldown <= 0) return true;
        crime[204] = cooldown - 1;
        return false;
    }

    public static bool TryReward(
        int type,
        int giverGold,
        int random,
        out int reward,
        out string failure)
    {
        reward = 0;
        if ((type != 0 && type != 1) || giverGold < 0 ||
            random < MinimumRewardRandom || random > MaximumRewardRandom)
            return Fail("invalid Fourberie contract reward inputs", out failure);

        int baseReward = type == 0 ? 65_000 : 45_000;
        reward = baseReward + System.Math.Min(giverGold / 10, baseReward) + random;
        failure = null;
        return true;
    }

    public static bool TryCommitProposal(
        IDictionary crime,
        IDictionary heroes,
        string giverId,
        string targetId,
        int type,
        int reward,
        out string failure)
    {
        if (crime == null || heroes == null || !crime.Contains(203) || crime.Contains(200) ||
            crime.Contains(202) || string.IsNullOrEmpty(giverId) || string.IsNullOrEmpty(targetId) ||
            giverId == targetId || (type != 0 && type != 1) || reward <= 0)
            return Fail("Fourberie contract proposal state is unavailable", out failure);

        heroes["contractGiver"] = giverId;
        heroes["contractTarget"] = targetId;
        crime[200] = type;
        crime[201] = reward;
        crime[202] = 0;
        failure = null;
        return true;
    }

    public static bool CanRespondToProposal(IDictionary crime, IDictionary heroes, out string failure)
    {
        if (crime == null || heroes == null || !crime.Contains(200) || !crime.Contains(201) ||
            !crime.Contains(202) || heroes["contractGiver"] is not string giver ||
            heroes["contractTarget"] is not string target || string.IsNullOrEmpty(giver) ||
            string.IsNullOrEmpty(target))
            return Fail("no complete Fourberie contract proposal is active", out failure);
        failure = null;
        return true;
    }

    public static void CommitAccept(IDictionary crime, int cooldown)
    {
        crime.Remove(202);
        crime.Remove(204);
        crime[204] = cooldown;
    }

    public static void CommitDecline(IDictionary crime, IDictionary heroes, int cooldown)
    {
        crime.Remove(202);
        CommitAbort(crime, heroes, cooldown);
    }

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
