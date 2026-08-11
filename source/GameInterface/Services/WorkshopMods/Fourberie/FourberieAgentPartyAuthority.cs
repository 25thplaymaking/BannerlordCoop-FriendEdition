using System;
using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieAgentPartyAuthority
{
    private const int SaboteurPoolKey = 300;
    private const int OtherTrainingPoolKey = 301;
    private const int PartyCapacity = 20;

    public static bool TryTakeForCreate(IDictionary state, out int count, out string failure) =>
        TryTake(state, PartyCapacity, out count, out failure);

    public static bool TryTakeForRefill(
        IDictionary state,
        int currentPartyCount,
        out int count,
        out string failure)
    {
        if (currentPartyCount < 0 || currentPartyCount >= PartyCapacity)
        {
            count = 0;
            failure = "saboteur party is already at capacity";
            return false;
        }
        return TryTake(state, PartyCapacity - currentPartyCount, out count, out failure);
    }

    public static bool TryReturnDisbanded(
        IDictionary state,
        int saboteurs,
        int otherTroops,
        out string failure)
    {
        if (state == null || saboteurs < 0 || otherTroops < 0)
        {
            failure = "invalid saboteur-party roster";
            return false;
        }

        state[SaboteurPoolKey] = Read(state, SaboteurPoolKey) + saboteurs;
        state[OtherTrainingPoolKey] = Read(state, OtherTrainingPoolKey) + otherTroops;
        failure = null;
        return true;
    }

    private static bool TryTake(IDictionary state, int capacity, out int count, out string failure)
    {
        int available = state == null ? 0 : Read(state, SaboteurPoolKey);
        count = Math.Min(available, capacity);
        if (count <= 0)
        {
            failure = "no trained saboteurs are available";
            return false;
        }

        state[SaboteurPoolKey] = available - count;
        failure = null;
        return true;
    }

    private static int Read(IDictionary state, int key) =>
        state.Contains(key) ? Convert.ToInt32(state[key]) : 0;
}
