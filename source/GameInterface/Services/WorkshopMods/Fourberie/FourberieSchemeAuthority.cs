using System;
using System.Collections;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal readonly struct FourberieSchemePlan
{
    public FourberieSchemePlan(int scheme, int fee, int agentPoolKey, int agentCost, int durationDays)
    {
        Scheme = scheme;
        Fee = fee;
        AgentPoolKey = agentPoolKey;
        AgentCost = agentCost;
        DurationDays = durationDays;
    }

    public int Scheme { get; }
    public int Fee { get; }
    public int AgentPoolKey { get; }
    public int AgentCost { get; }
    public int DurationDays { get; }
}

internal sealed class FourberieSchemeSelectionSnapshot
{
    private static readonly int[] CrimeKeys =
    {
        7, 71, 72, 73, 74, 710, 720, 730, 740, 741, 750,
        8, 81, 82, 83, 84, 810, 820, 830, 840, 841, 850,
    };
    private static readonly string[] HeroKeys = { "victim7", "victim8" };
    private static readonly int[] TimeKeys = { 7, 8 };

    private readonly Dictionary<int, object> crimeValues = new Dictionary<int, object>();
    private readonly HashSet<int> presentCrimeKeys = new HashSet<int>();
    private readonly Dictionary<string, object> heroValues = new Dictionary<string, object>(StringComparer.Ordinal);
    private readonly HashSet<string> presentHeroKeys = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<int, object> timeValues = new Dictionary<int, object>();
    private readonly HashSet<int> presentTimeKeys = new HashSet<int>();

    public FourberieSchemeSelectionSnapshot(IDictionary crime, IDictionary heroes, IDictionary times)
    {
        Capture(crime, CrimeKeys, crimeValues, presentCrimeKeys);
        Capture(heroes, HeroKeys, heroValues, presentHeroKeys);
        Capture(times, TimeKeys, timeValues, presentTimeKeys);
    }

    public void Restore(IDictionary crime, IDictionary heroes, IDictionary times)
    {
        Restore(crime, CrimeKeys, crimeValues, presentCrimeKeys);
        Restore(heroes, HeroKeys, heroValues, presentHeroKeys);
        Restore(times, TimeKeys, timeValues, presentTimeKeys);
    }

    private static void Capture<TKey>(
        IDictionary source,
        IEnumerable<TKey> keys,
        IDictionary<TKey, object> values,
        ISet<TKey> present)
    {
        if (source == null) return;
        foreach (TKey key in keys)
        {
            if (!source.Contains(key)) continue;
            present.Add(key);
            values[key] = source[key];
        }
    }

    private static void Restore<TKey>(
        IDictionary target,
        IEnumerable<TKey> keys,
        IReadOnlyDictionary<TKey, object> values,
        ISet<TKey> present)
    {
        if (target == null) return;
        foreach (TKey key in keys)
        {
            if (present.Contains(key)) target[key] = values[key];
            else target.Remove(key);
        }
    }
}

/// <summary>
/// Pinned Fourberie 1.4.7.5 scheme rules. The server supplies the target facts and random offset;
/// clients send only stable target/type/lifecycle intent.
/// </summary>
internal static class FourberieSchemeAuthority
{
    public static FourberieSchemeSelectionSnapshot CaptureSelection(
        IDictionary crime,
        IDictionary heroes,
        IDictionary times) => new FourberieSchemeSelectionSnapshot(crime, heroes, times);

    public static bool TryPlan(
        int scheme,
        int targetRank,
        int randomOffset,
        out FourberieSchemePlan plan,
        out string failure)
    {
        plan = default;
        failure = null;
        if (scheme < 1 || scheme > 8 || targetRank < 1 || targetRank > 3 ||
            randomOffset < 0 || randomOffset > 1)
        {
            failure = "scheme plan is outside the pinned Fourberie range";
            return false;
        }

        int pool = scheme == 3 || scheme == 6 ? 320 : 310;
        int agents = scheme == 4 || scheme == 5 || scheme == 7 || targetRank < 3 ? 1 : 2;
        int fee = scheme switch
        {
            1 => 35_000 * targetRank,
            2 => 45_000,
            3 => 65_000,
            4 => 0,
            5 => 10_000,
            6 => 60_000,
            7 => 5_000,
            8 => 40_000 * targetRank,
            _ => 0,
        };
        int duration = scheme switch
        {
            1 => 3 + randomOffset + targetRank,
            2 => 4 + randomOffset,
            3 => 4 + randomOffset + targetRank,
            4 => 2 + randomOffset,
            5 => 1,
            6 => 4 + randomOffset + targetRank,
            7 => 1,
            8 => 4 + randomOffset + targetRank,
            _ => 0,
        };

        plan = new FourberieSchemePlan(scheme, fee, pool, agents, duration);
        return true;
    }

    public static bool IsTargetEligible(
        int scheme,
        bool influenceAboveMinimum,
        bool canDie,
        bool clanLeader,
        bool partyLeader,
        bool atWarWithActor,
        int network)
    {
        return scheme switch
        {
            1 => influenceAboveMinimum,
            2 or 4 or 8 => clanLeader,
            3 => canDie,
            5 => partyLeader,
            6 => atWarWithActor,
            7 => partyLeader && network >= 30,
            _ => false,
        };
    }

    public static bool TryStart(
        IDictionary crime,
        int slot,
        int actorGold,
        FourberieSchemePlan plan,
        out string failure)
    {
        failure = null;
        if (crime == null || !IsSlot(slot) || Read(crime, slot) != plan.Scheme ||
            crime.Contains(slot * 100 + 40) || crime.Contains(slot * 100 + 41))
        {
            failure = "scheme selection is stale or already active";
            return false;
        }
        if (actorGold < plan.Fee)
        {
            failure = "the controller cannot afford the selected scheme";
            return false;
        }
        int available = Read(crime, plan.AgentPoolKey);
        if (available < plan.AgentCost)
        {
            failure = "the selected scheme no longer has enough agents";
            return false;
        }

        crime[plan.AgentPoolKey] = available - plan.AgentCost;
        Replace(crime, slot * 10 + 4, 1);
        Replace(crime, slot * 100 + 40, plan.DurationDays);
        return true;
    }

    public static void SetOutcome(IDictionary crime, int slot, int scheme, bool success, bool detected)
    {
        if (crime == null || !IsSlot(slot) || scheme < 1 || scheme > 8)
            throw new ArgumentOutOfRangeException(nameof(slot));

        crime.Remove(slot * 10 + 1);
        crime.Remove(slot * 10 + 2);
        crime.Remove(slot * 10 + 3);
        int key = slot * 10 + (success ? 1 : detected ? 3 : 2);
        crime[key] = scheme;
    }

    public static bool TryAbort(IDictionary crime, int slot, out string failure)
    {
        failure = null;
        if (crime == null || !IsSlot(slot) || !crime.Contains(slot * 100 + 40))
        {
            failure = "scheme is not active";
            return false;
        }

        ClearSlot(crime, slot);
        return true;
    }

    public static bool TryChangeStance(
        IDictionary crime,
        int stance,
        out bool changed,
        out string failure)
    {
        changed = false;
        failure = null;
        if (crime == null || (stance != 1 && stance != 2))
        {
            failure = "scheme stance is outside the pinned Fourberie range";
            return false;
        }
        if (Read(crime, 500) == stance) return true;

        crime[500] = stance;
        ClearSlot(crime, 7);
        ClearSlot(crime, 8);
        changed = true;
        return true;
    }

    public static bool TryClearCompleted(IDictionary crime, int slot, out string failure)
    {
        failure = null;
        if (crime == null || !IsSlot(slot) || !crime.Contains(slot * 100 + 41))
        {
            failure = "scheme is not complete";
            return false;
        }

        crime.Remove(slot);
        crime.Remove(slot * 10 + 4);
        crime.Remove(slot * 100 + 10);
        crime.Remove(slot * 100 + 20);
        crime.Remove(slot * 100 + 30);
        crime.Remove(slot * 100 + 41);
        crime.Remove(slot * 100 + 50);
        return true;
    }

    public static bool IsSlot(int slot) => slot == 7 || slot == 8;

    public static void ClearSlotState(
        IDictionary crime,
        IDictionary heroes,
        IDictionary times,
        int slot)
    {
        if (crime == null || heroes == null || times == null || !IsSlot(slot))
            throw new ArgumentOutOfRangeException(nameof(slot));
        ClearSlot(crime, slot);
        heroes.Remove("victim" + slot);
        times.Remove(slot);
    }

    public static bool TryDecodeSelection(int value, out int slot, out int scheme)
    {
        slot = value / 10;
        scheme = value % 10;
        return IsSlot(slot) && scheme >= 1 && scheme <= 8;
    }

    private static int Read(IDictionary dictionary, int key) =>
        dictionary.Contains(key) ? Convert.ToInt32(dictionary[key]) : 0;

    private static void Replace(IDictionary dictionary, int key, int value)
    {
        dictionary.Remove(key);
        dictionary[key] = value;
    }

    private static void ClearSlot(IDictionary crime, int slot)
    {
        crime.Remove(slot);
        for (int suffix = 1; suffix <= 4; suffix++) crime.Remove(slot * 10 + suffix);
        for (int suffix = 10; suffix <= 50; suffix += 10) crime.Remove(slot * 100 + suffix);
        crime.Remove(slot * 100 + 41);
    }
}
