using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieSafehouseTraderPlan
{
    public FourberieSafehouseTraderPlan(int slaveReduction, int goldReward, int cooldownIncrease)
    {
        SlaveReduction = slaveReduction;
        GoldReward = goldReward;
        CooldownIncrease = cooldownIncrease;
    }

    public int SlaveReduction { get; }
    public int GoldReward { get; }
    public int CooldownIncrease { get; }
}

internal static class FourberieSafehouseTraderAuthority
{
    public static bool CanExecute(
        string settlementId,
        string currentBaseId,
        string currentSettlementId,
        bool isTown,
        IDictionary crime,
        float elapsedDays,
        out string failure)
    {
        int cooldownDays = 5 + ReadInt(crime, 556);
        if (isTown || crime?.Contains(550) != true ||
            string.IsNullOrEmpty(settlementId) ||
            !string.Equals(settlementId, currentBaseId, StringComparison.Ordinal) ||
            !string.Equals(settlementId, currentSettlementId, StringComparison.Ordinal) ||
            elapsedDays < cooldownDays)
        {
            failure = "the crooked-trader safehouse context is stale or invalid";
            return false;
        }

        failure = null;
        return true;
    }

    public static bool TryCreatePlan(
        IDictionary crime,
        FourberieOperation operation,
        Func<int, int> ransomValue,
        Func<float> regionWealth,
        out FourberieSafehouseTraderPlan plan,
        out string failure)
    {
        plan = null;
        if (crime == null || ransomValue == null || regionWealth == null)
        {
            failure = "the crooked-trader state is unavailable";
            return false;
        }

        int slaves = Math.Max(0, ReadInt(crime, 1500));
        int reduction;
        int reward;
        int cooldown;
        switch (operation)
        {
            case FourberieOperation.SellQuarterSlaves:
                reduction = slaves / 4;
                reward = ransomValue(reduction);
                cooldown = 1;
                break;
            case FourberieOperation.SellHalfSlaves:
                reduction = slaves / 2;
                reward = ransomValue(reduction);
                cooldown = 1;
                break;
            case FourberieOperation.DeclineCrookedTrader:
                reduction = 0;
                reward = 0;
                cooldown = 3;
                break;
            case FourberieOperation.RobCrookedTrader:
                reduction = 0;
                reward = (int)regionWealth() / 2;
                cooldown = 10;
                break;
            default:
                failure = "the crooked-trader operation is unknown";
                return false;
        }

        if (reward < 0)
        {
            failure = "the crooked-trader reward is invalid";
            return false;
        }

        plan = new FourberieSafehouseTraderPlan(reduction, reward, cooldown);
        failure = null;
        return true;
    }

    public static void Commit(
        IDictionary crime,
        IDictionary times,
        FourberieSafehouseTraderPlan plan,
        object campaignTime)
    {
        if (crime == null) throw new ArgumentNullException(nameof(crime));
        if (times == null) throw new ArgumentNullException(nameof(times));
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        times[556] = campaignTime;
        if (plan.SlaveReduction > 0)
            crime[1500] = Math.Max(0, ReadInt(crime, 1500) - plan.SlaveReduction);
        crime[556] = ReadInt(crime, 556) + plan.CooldownIncrease;
    }

    private static int ReadInt(IDictionary dictionary, object key) =>
        dictionary?.Contains(key) == true ? Convert.ToInt32(dictionary[key]) : 0;
}
