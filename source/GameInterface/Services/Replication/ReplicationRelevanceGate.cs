using Common;
using Common.Logging;
using Common.Network.Coalescing;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace GameInterface.Services.Replication;

/// <summary>
/// Holds coalesced roster updates for parties and settlements that no player is anywhere near.
/// </summary>
/// <remarks>
/// Replication volume is <c>world size x rate of change x player count</c>, and nothing filtered it by
/// whether a client could observe the change. Measured on a live 2-5 player session: parties grew from
/// 2,711 to 4,291, one ten-second window carried 1,031,168 messages, and per-peer reliable queues
/// reached 69,037 packets. LiteNetLib keeps 64 packets in flight, so per-peer throughput is
/// window / RTT — about 1.2 MB/s at 66 ms — and once the queue runs away
/// <c>OverloadedPeerManager</c> correctly stops campaign time. The world visibly freezes, which is what
/// players reported as the session becoming unplayable.
/// <para>
/// Troop and item rosters are the two largest routes, together roughly 55% of all messages, and both
/// already flow through <see cref="ISendCoalescer"/>. Gating that one flush therefore reaches most of
/// the traffic without touching 500-odd individual send sites.
/// </para>
/// <para>
/// This HOLDS, it does not drop. Coalesced payloads merge — troop counts sum, item totals sum — so an
/// update held across many changes still carries the correct end state when it finally goes out. A
/// distant party whose roster churns repeatedly costs one message instead of many, and
/// <see cref="MaximumHold"/> bounds how stale anything can get. Live positions are read every flush, so
/// a party stops being held the moment a player rides near it; there is no "became relevant" edge to
/// get wrong.
/// </para>
/// <para>
/// Every failure path returns true. A wrongly held update is a divergence; a wrongly sent one is only a
/// packet.
/// </para>
/// </remarks>
public class ReplicationRelevanceGate : ICoalesceGate
{
    private static readonly ILogger Logger = LogManager.GetLogger<ReplicationRelevanceGate>();

    /// <summary>Coalescer channels this gate understands, and the type their instance id names.</summary>
    private static readonly Dictionary<string, Type> GatedChannels = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        { "TroopRosterElementBatch", typeof(TroopRoster) },
        { "ItemRosterUpdate", typeof(ItemRoster) },
    };

    /// <summary>
    /// How far from a player a party still counts as observable, in campaign map units.
    /// </summary>
    /// <remarks>
    /// Deliberately far larger than anything a client can see. Bannerlord's party spotting range is
    /// single digits in these units, so 100 is over ten times the distance at which a party could even
    /// appear on screen. It is still small against the world: Europe 1100 spans roughly 1,405 x 894
    /// units, so one player's circle is about 2.5% of the map area, and the great majority of the
    /// world's parties are held at any moment.
    /// <para>
    /// Erring high is the safe direction. Too generous only means the gate saves less; too tight would
    /// mean a party a player can see updating late.
    /// </para>
    /// </remarks>
    public static float RelevanceRadius = 100f;

    /// <summary>
    /// The longest anything is held. Distant parties change a few times a second, so even a few seconds
    /// collapses many messages into one, while keeping worst-case staleness short enough that a player
    /// arriving at a battle never waits on it.
    /// </summary>
    public TimeSpan MaximumHold { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Turns the gate off without redeploying, for A/B against the packet profile.</summary>
    public static bool Enabled = true;

    private static readonly PropertyInfo TroopRosterOwnerParty =
        AccessTools.Property(typeof(TroopRoster), "OwnerParty");

    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;

    // ItemRoster has no owner back-reference, so the owning party has to be indexed. Parties and
    // settlements hold their rosters for their whole lifetime, so this only needs rebuilding as the
    // world's party set changes, not as rosters change.
    private readonly Dictionary<ItemRoster, Vec2> itemRosterPositions = new Dictionary<ItemRoster, Vec2>();
    private DateTime itemRosterIndexBuiltUtc = DateTime.MinValue;
    private static readonly TimeSpan ItemRosterIndexLifetime = TimeSpan.FromSeconds(15);

    private readonly List<Vec2> playerPositions = new List<Vec2>();

    // Effect of the gate, logged periodically so the saving is visible without a packet capture.
    private int consideredSinceReport;
    private int heldSinceReport;
    private DateTime nextReportUtc = DateTime.MinValue;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(60);

    public ReplicationRelevanceGate(IObjectManager objectManager, IPlayerManager playerManager)
    {
        this.objectManager = objectManager;
        this.playerManager = playerManager;
    }

    public bool ShouldSendNow(CoalesceKey key)
    {
        if (!Enabled || !ModInformation.IsServer) return true;
        if (!GatedChannels.TryGetValue(key.Channel ?? string.Empty, out Type instanceType)) return true;
        if (Campaign.Current == null) return true;

        RefreshPlayerPositions();

        // With nobody connected there is nobody to be near. Send, rather than accumulating a backlog
        // that would land on whoever joins next.
        if (playerPositions.Count == 0) return true;

        if (!TryGetInstancePosition(instanceType, key.InstanceId, out Vec2 position)) return true;

        bool relevant = false;
        float radiusSquared = RelevanceRadius * RelevanceRadius;
        for (int i = 0; i < playerPositions.Count; i++)
        {
            if (playerPositions[i].DistanceSquared(position) <= radiusSquared)
            {
                relevant = true;
                break;
            }
        }

        Record(relevant);
        return relevant;
    }

    private void Record(bool sent)
    {
        consideredSinceReport++;
        if (!sent) heldSinceReport++;

        DateTime now = DateTime.UtcNow;
        if (nextReportUtc == DateTime.MinValue)
        {
            nextReportUtc = now + ReportInterval;
            return;
        }

        if (now < nextReportUtc) return;
        nextReportUtc = now + ReportInterval;

        if (consideredSinceReport > 0)
        {
            Logger.Information(
                "[Relevance] held {Held} of {Considered} coalesced roster updates ({Percent:N0}%) " +
                "for parties beyond {Radius:N0} of every player",
                heldSinceReport, consideredSinceReport,
                100.0 * heldSinceReport / consideredSinceReport, RelevanceRadius);
        }

        consideredSinceReport = 0;
        heldSinceReport = 0;
    }

    private void RefreshPlayerPositions()
    {
        // Read live on every flush: positions change constantly, and a cached list would keep holding
        // updates for a party a player has already ridden up to.
        playerPositions.Clear();

        foreach (var player in playerManager.Players)
        {
            if (string.IsNullOrEmpty(player?.MobilePartyId)) continue;
            if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party)) continue;
            if (party == null || !party.IsActive) continue;

            playerPositions.Add(party.Position.ToVec2());
        }
    }

    private bool TryGetInstancePosition(Type instanceType, string compactId, out Vec2 position)
    {
        position = default;
        if (string.IsNullOrEmpty(compactId)) return false;

        // The wire id has its "{TypeName}_" prefix stripped; the registry keeps the full one.
        string fullId = instanceType.Name + "_" + compactId;

        if (instanceType == typeof(TroopRoster))
        {
            if (!objectManager.TryGetObject<TroopRoster>(fullId, out var troopRoster) || troopRoster == null)
                return false;

            if (!(TroopRosterOwnerParty?.GetValue(troopRoster) is PartyBase owner)) return false;
            return TryGetPartyPosition(owner, out position);
        }

        if (instanceType == typeof(ItemRoster))
        {
            if (!objectManager.TryGetObject<ItemRoster>(fullId, out var itemRoster) || itemRoster == null)
                return false;

            RebuildItemRosterIndexIfStale();
            return itemRosterPositions.TryGetValue(itemRoster, out position);
        }

        return false;
    }

    private static bool TryGetPartyPosition(PartyBase party, out Vec2 position)
    {
        position = default;
        if (party == null) return false;

        if (party.IsMobile && party.MobileParty != null)
        {
            position = party.MobileParty.Position.ToVec2();
            return true;
        }

        if (party.IsSettlement && party.Settlement != null)
        {
            position = party.Settlement.Position.ToVec2();
            return true;
        }

        return false;
    }

    private void RebuildItemRosterIndexIfStale()
    {
        DateTime now = DateTime.UtcNow;
        if (now - itemRosterIndexBuiltUtc < ItemRosterIndexLifetime) return;
        itemRosterIndexBuiltUtc = now;

        itemRosterPositions.Clear();

        try
        {
            foreach (var party in MobileParty.All)
            {
                var partyBase = party?.Party;
                if (partyBase?.ItemRoster == null) continue;
                itemRosterPositions[partyBase.ItemRoster] = party.Position.ToVec2();
            }

            foreach (var settlement in Settlement.All)
            {
                var partyBase = settlement?.Party;
                if (partyBase?.ItemRoster == null) continue;
                itemRosterPositions[partyBase.ItemRoster] = settlement.Position.ToVec2();
            }
        }
        catch (Exception error)
        {
            // A half-built index only costs coverage: an unindexed roster falls through to "send".
            Logger.Warning(error, "Could not rebuild the item-roster owner index; those updates will not be held.");
        }
    }
}
