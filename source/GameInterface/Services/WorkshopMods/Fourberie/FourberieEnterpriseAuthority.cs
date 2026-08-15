using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Explicit server transaction for Fourberie's criminal-enterprise dictionary. The keys, costs,
/// and limits mirror the pinned 1.4.7.6 CriminalVM without invoking its client UI layer.
/// </summary>
internal static class FourberieEnterpriseAuthority
{
    private const int UpgradePoolKey = 6;
    private const int EnterpriseCountKey = 60;

    public static bool TryStart(
        IDictionary state,
        int businessKey,
        int availableGold,
        out int goldCost,
        out string failure)
    {
        goldCost = businessKey switch
        {
            11 => 0,
            21 => 10_000,
            31 => 15_000,
            _ => -1,
        };
        if (state == null || goldCost < 0)
            return Fail("unsupported criminal business", out failure);

        int markerKey = businessKey / 10;
        if (state.Contains(markerKey) || state.Contains(businessKey))
            return Fail("criminal business already exists", out failure);
        if (availableGold < goldCost)
            return Fail("controller cannot afford this criminal business", out failure);

        state[EnterpriseCountKey] = Read(state, EnterpriseCountKey) + 1;
        state[markerKey] = 0;
        state[businessKey] = 1;
        failure = null;
        return true;
    }

    public static bool TryUpgrade(
        IDictionary state,
        int businessKey,
        int partnershipCount,
        int territoryCount,
        out string failure)
    {
        if (state == null || !TryLimit(state, businessKey, partnershipCount, territoryCount, out int limit))
            return Fail("unsupported criminal-business upgrade", out failure);
        if (!state.Contains(businessKey / 10))
            return Fail("criminal business does not exist", out failure);
        if (Read(state, UpgradePoolKey) <= 0)
            return Fail("no criminal-business upgrade points remain", out failure);
        if (Read(state, businessKey) >= limit)
            return Fail("criminal business is already at its server limit", out failure);

        state[UpgradePoolKey] = Read(state, UpgradePoolKey) - 1;
        state[businessKey] = Read(state, businessKey) + 1;
        failure = null;
        return true;
    }

    public static bool TryDowngrade(IDictionary state, int businessKey, out string failure)
    {
        if (state == null || !IsBusinessKey(businessKey))
            return Fail("unsupported criminal-business downgrade", out failure);
        if (!state.Contains(businessKey / 10) || !state.Contains(businessKey))
            return Fail("criminal business does not exist", out failure);
        if (Read(state, businessKey) <= 0)
            return Fail("criminal business is already at its minimum", out failure);

        state[businessKey] = Read(state, businessKey) - 1;
        state[UpgradePoolKey] = Read(state, UpgradePoolKey) + 1;
        failure = null;
        return true;
    }

    private static bool TryLimit(
        IDictionary state,
        int businessKey,
        int partnershipCount,
        int territoryCount,
        out int limit)
    {
        limit = businessKey switch
        {
            11 => Math.Max(0, partnershipCount + territoryCount) * 3,
            12 => 100,
            21 => Read(state, 22) / 5,
            22 => 100,
            31 => Read(state, 32) / 5,
            32 => 200,
            _ => -1,
        };
        return limit >= 0;
    }

    private static bool IsBusinessKey(int businessKey) =>
        businessKey == 11 || businessKey == 12 || businessKey == 21 ||
        businessKey == 22 || businessKey == 31 || businessKey == 32;

    private static int Read(IDictionary state, int key) =>
        state.Contains(key) ? Convert.ToInt32(state[key]) : 0;

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
