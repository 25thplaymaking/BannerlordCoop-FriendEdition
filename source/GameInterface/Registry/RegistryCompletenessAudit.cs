using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Registry;

/// <summary>
/// Verifies the loaded campaign graph immediately after every auto-registry has run. Load/deserialization can
/// legitimately emit lookup failures before registration; this audit is the boundary that distinguishes those
/// early misses from a server that actually became joinable with unwired objects.
/// </summary>
internal static class RegistryCompletenessAudit
{
    internal readonly struct Result
    {
        public bool Available { get; }
        public int Expected { get; }
        public int Missing { get; }
        public int Parties { get; }
        public int Armies { get; }
        public int MapEvents { get; }
        public int Rosters { get; }
        public string MissingExamples { get; }

        public Result(bool available, int expected, int missing, int parties, int armies, int mapEvents,
            int rosters, string missingExamples)
        {
            Available = available;
            Expected = expected;
            Missing = missing;
            Parties = parties;
            Armies = armies;
            MapEvents = mapEvents;
            Rosters = rosters;
            MissingExamples = missingExamples;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    public static Result Run(IObjectManager objectManager)
    {
        var campaign = Campaign.Current;
        if (campaign == null)
            return new Result(false, 0, 0, 0, 0, 0, 0, string.Empty);

        var expected = new Dictionary<object, string>(ReferenceComparer.Instance);
        int parties = 0;
        int armies = 0;
        int mapEvents = 0;
        int rosters = 0;

        void Add(object obj, string label)
        {
            if (obj != null && !expected.ContainsKey(obj))
                expected.Add(obj, label);
        }

        var seenArmies = new HashSet<object>(ReferenceComparer.Instance);
        foreach (var party in MobileParty.All)
        {
            if (party == null) continue;
            parties++;
            Add(party, $"MobileParty:{party.StringId}");
            Add(party.Party, $"PartyBase:{party.StringId}");
            Add(party.Ai, $"MobilePartyAi:{party.StringId}");
            Add(party.ItemRoster, $"ItemRoster:{party.StringId}");
            Add(party.MemberRoster, $"MemberRoster:{party.StringId}");
            Add(party.PrisonRoster, $"PrisonRoster:{party.StringId}");
            rosters += 3;

            if (party.Army != null && seenArmies.Add(party.Army))
            {
                armies++;
                Add(party.Army, $"Army:{party.Army.LeaderParty?.StringId ?? "leader-missing"}");
            }
        }

        foreach (var kingdom in campaign.Kingdoms)
        {
            if (kingdom == null) continue;
            foreach (var army in kingdom.Armies)
            {
                if (army == null || !seenArmies.Add(army)) continue;
                armies++;
                Add(army, $"Army:{army.LeaderParty?.StringId ?? "leader-missing"}");
            }
        }

        foreach (var settlement in Settlement.All)
        {
            if (settlement == null) continue;
            Add(settlement.Party, $"SettlementParty:{settlement.StringId}");
            Add(settlement.Party?.PrisonRoster, $"SettlementPrisonRoster:{settlement.StringId}");
            Add(settlement.ItemRoster, $"SettlementItemRoster:{settlement.StringId}");
            Add(settlement.Stash, $"SettlementStash:{settlement.StringId}");
        }

        var liveMapEvents = campaign.MapEventManager?.MapEvents;
        if (liveMapEvents != null)
        {
            foreach (var mapEvent in liveMapEvents)
            {
                if (mapEvent == null || mapEvent.StringId == null) continue;
                mapEvents++;
                Add(mapEvent, $"MapEvent:{mapEvent.StringId}");
                Add(mapEvent.Component, $"MapEventComponent:{mapEvent.StringId}");

                foreach (var side in mapEvent._sides ?? Array.Empty<MapEventSide>())
                {
                    if (side?.Parties == null) continue;
                    Add(side, $"MapEventSide:{mapEvent.StringId}");
                    foreach (var mapEventParty in side.Parties)
                    {
                        if (mapEventParty == null) continue;
                        Add(mapEventParty, $"MapEventParty:{mapEvent.StringId}");
                        Add(mapEventParty._woundedInBattle, $"MapEventWounded:{mapEvent.StringId}");
                        Add(mapEventParty._diedInBattle, $"MapEventDied:{mapEvent.StringId}");
                        Add(mapEventParty._routedInBattle, $"MapEventRouted:{mapEvent.StringId}");
                    }
                }
            }
        }

        int missing = 0;
        var examples = new List<string>();
        foreach (var pair in expected)
        {
            if (objectManager.Contains(pair.Key)) continue;
            missing++;
            if (examples.Count < 12) examples.Add(pair.Value);
        }

        return new Result(
            true,
            expected.Count,
            missing,
            parties,
            armies,
            mapEvents,
            rosters,
            string.Join(", ", examples));
    }
}
