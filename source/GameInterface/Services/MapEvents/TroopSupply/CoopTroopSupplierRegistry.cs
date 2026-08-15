using System;
using System.Collections.Generic;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// Bridges the network handler (which receives a side's reserve from the server) to the
/// <see cref="CoopTroopSupplier"/>s the injection patch installs into the mission. A reserve message can
/// arrive before or after the mission (and thus the supplier) is built, so a reserve that arrives early is
/// buffered (latest wins) and applied when the matching supplier registers. Static because the injection
/// patch can't resolve DI services.
/// </summary>
public static class CoopTroopSupplierRegistry
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, CoopTroopSupplier> Suppliers = new Dictionary<string, CoopTroopSupplier>();
    private static readonly Dictionary<string, (PartyReserve[] Reserve, int SideTotal, int PlayerParties, long AllocationRevision, int BattleSize)> Pending =
        new Dictionary<string, (PartyReserve[], int, int, long, int)>();
    private static readonly HashSet<string> AwaitingRefresh = new HashSet<string>();

    private static string Key(string mapEventId, BattleSideEnum side) => mapEventId + "|" + (int)side;

    /// <summary>[Game thread] A supplier was installed into the mission; apply any reserve buffered for it.</summary>
    public static void Register(CoopTroopSupplier supplier)
    {
        lock (Gate)
        {
            var key = Key(supplier.MapEventId, supplier.Side);
            Suppliers[key] = supplier;

            if (Pending.TryGetValue(key, out var buffered))
            {
                supplier.SetReserve(buffered.Reserve, buffered.SideTotal, buffered.PlayerParties,
                    buffered.BattleSize, buffered.AllocationRevision);
                Pending.Remove(key);
            }

            // The ownership-expanded signal can arrive before mission construction. An older entry-time
            // reserve may already be buffered; applying it preserves its pointers, but it must not make the
            // new supplier eligible for sizing until this side's post-signal full reserve has arrived.
            if (AwaitingRefresh.Contains(key))
                supplier.BeginAuthoritativeRefresh();
        }
    }

    /// <summary>
    /// The server is expanding this receiver from its entry-time party to complete ownership of the battle.
    /// Both sides are deliberately made unpopulated together; each becomes ready only after its own full
    /// reserve message arrives. This prevents mission sizing between the first and second side messages.
    /// </summary>
    public static void BeginCompleteRefresh(string mapEventId)
    {
        lock (Gate)
        {
            foreach (var side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                var key = Key(mapEventId, side);
                AwaitingRefresh.Add(key);
                if (Suppliers.TryGetValue(key, out var supplier))
                    supplier.BeginAuthoritativeRefresh();
            }
        }
    }

    /// <summary>[Network thread] Set a side's reserve (full authoritative set), or buffer it until a supplier
    /// exists. Returns the final local pointers of the parties the REPLACE dropped (the BR-033 flush payload;
    /// see <see cref="CoopTroopSupplier.SetReserve"/>) — empty when buffered: with no supplier, nothing was
    /// ever supplied locally, so there is nothing beyond the server's own ledger to flush.</summary>
    /// <param name="sideTotalTroops">Every troop on this side across all owners; 0 means the server did not
    /// send one, in which case the supplier keeps sizing from what it owns.</param>
    public static IReadOnlyList<(string PartyId, int Supplied)> Feed(string mapEventId, BattleSideEnum side,
        PartyReserve[] reserve, int sideTotalTroops = 0, int playerOwnedPartyCount = 0,
        long allocationRevision = 0, int battleSize = 0)
    {
        lock (Gate)
        {
            var key = Key(mapEventId, side);
            AwaitingRefresh.Remove(key);
            if (Suppliers.TryGetValue(key, out var supplier))
                return supplier.SetReserve(reserve, sideTotalTroops, playerOwnedPartyCount, battleSize,
                    allocationRevision);

            Pending[key] = (reserve, sideTotalTroops, playerOwnedPartyCount, allocationRevision, battleSize);
            return Array.Empty<(string, int)>();
        }
    }

    /// <summary>The suppliers installed for a battle (used to read supplied pointers for progress reporting).</summary>
    public static IReadOnlyList<CoopTroopSupplier> GetSuppliers(string mapEventId)
    {
        lock (Gate)
        {
            var prefix = mapEventId + "|";
            var result = new List<CoopTroopSupplier>();
            foreach (var pair in Suppliers)
                if (pair.Key.StartsWith(prefix))
                    result.Add(pair.Value);
            return result;
        }
    }

    /// <summary>Drop everything for a battle (on mission end).</summary>
    public static void ClearBattle(string mapEventId)
    {
        lock (Gate)
        {
            var prefix = mapEventId + "|";
            foreach (var key in new List<string>(Suppliers.Keys))
                if (key.StartsWith(prefix)) Suppliers.Remove(key);
            foreach (var key in new List<string>(Pending.Keys))
                if (key.StartsWith(prefix)) Pending.Remove(key);
            foreach (var key in new List<string>(AwaitingRefresh))
                if (key.StartsWith(prefix)) AwaitingRefresh.Remove(key);
        }
    }
}
