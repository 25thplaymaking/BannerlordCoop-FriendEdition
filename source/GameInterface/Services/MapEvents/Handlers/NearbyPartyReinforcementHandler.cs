using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.PlayerCaptivityService.Messages;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Server-side replacement for vanilla's nearby-party reinforcement, so AI parties standing next to a
/// player's battle actually join it.
/// </summary>
/// <remarks>
/// Vanilla does this from <c>PlayerEncounter.CheckNearbyPartiesToJoinPlayerMapEvent</c>, driven off
/// PlayerEncounter.Update. Co-op suppresses that method outright, because on a client it would mutate the
/// shared MapEventSide locally and desync - so nothing ever pulled nearby parties in and a friendly army
/// could sit beside your battle doing nothing.
///
/// Vanilla's selection method cannot run on a dedicated server: despite accepting only two lists, it reads
/// <c>MobileParty.MainParty</c> and several <c>PlayerEncounter</c> statics internally. The selection below mirrors
/// its map search and faction checks against the authoritative <see cref="MapEvent"/> instead.
///
/// Replication is already in place: <see cref="MapEventPatches"/>' AddInvolvedPartyInternal postfix
/// broadcasts an AI join while the battle is inside its
/// <see cref="ModConfigProvider.ModOptions.PlayerBattleAiJoinWindowHours"/> window. That window existed with
/// nothing to populate it; this is what populates it.
/// </remarks>
internal class NearbyPartyReinforcementHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<NearbyPartyReinforcementHandler>();
    private static readonly CampaignTime ScanInterval = CampaignTime.Hours(0.25f);

    private readonly IMessageBroker messageBroker;
    private readonly Dictionary<MapEvent, CampaignTime> nextScanAt = new();
    private readonly HashSet<MapEvent> failedEvents = new();

    public NearbyPartyReinforcementHandler(IMessageBroker messageBroker)
    {
        this.messageBroker = messageBroker;
        messageBroker.Subscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
        nextScanAt.Clear();
        failedEvents.Clear();
    }

    /// <summary>
    /// The moment a player's battle opens its AI-join window is the one moment reinforcement is guaranteed to
    /// get a look in. CampaignTick alone is not enough: map time stops while a player sits in an encounter, so
    /// a tick-driven scan can go the entire battle without running - which is why nearby lords stood and
    /// watched. This fires from MapEvent.Initialize's postfix, where the window is opened.
    /// </summary>
    private void Handle_PlayerJoinedBattle(MessagePayload<PlayerJoinedBattle> payload)
    {
        if (!ModInformation.IsServer) return;

        // Published with the MapEvent as its source; E2E publishes the same event with a test object, so type-check.
        if (payload.Who is not MapEvent mapEvent) return;

        var skip = WhyNotReinforce(mapEvent);
        if (skip != null)
        {
            Logger.Debug("[Reinforce] battle {MapEventId} opened its join window but will not reinforce: {Reason}",
                mapEvent.StringId ?? "<no id>", skip);
            return;
        }

        ScheduleNextScan(mapEvent);
        TryReinforce(mapEvent, "at start");
    }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (!ModInformation.IsServer) return;

        var events = Campaign.Current?.MapEventManager?.MapEvents;
        if (events == null) return;

        // ToArray: adding a party mutates the event graph while we walk it.
        var activeEvents = events.ToArray();
        PruneFinishedEvents(activeEvents);

        foreach (var mapEvent in activeEvents)
        {
            var skip = WhyNotReinforce(mapEvent);
            if (skip != null)
            {
                nextScanAt.Remove(mapEvent);
                continue;
            }

            if (failedEvents.Contains(mapEvent) || !ReserveScan(mapEvent))
                continue;

            TryReinforce(mapEvent, "during campaign tick");
        }
    }

    private void TryReinforce(MapEvent mapEvent, string phase)
    {
        // A failed battle is disabled after the first exception. Reinforcements are optional; retrying every
        // campaign frame only turns one bad native state into an unbounded exception and disk-I/O storm.
        try
        {
            Reinforce(mapEvent);
        }
        catch (System.Exception e)
        {
            if (failedEvents.Add(mapEvent))
                Logger.Error(e, "Reinforcing player battle {MapEventId} {Phase} failed; disabling reinforcement scans for this battle",
                    mapEvent.StringId ?? "<no id>", phase);
        }
    }

    private void ScheduleNextScan(MapEvent mapEvent)
        => nextScanAt[mapEvent] = CampaignTime.Now + ScanInterval;

    private bool ReserveScan(MapEvent mapEvent)
    {
        var now = CampaignTime.Now;
        if (nextScanAt.TryGetValue(mapEvent, out var next) && now < next)
            return false;

        nextScanAt[mapEvent] = now + ScanInterval;
        return true;
    }

    private void PruneFinishedEvents(IEnumerable<MapEvent> activeEvents)
    {
        var active = new HashSet<MapEvent>(activeEvents.Where(mapEvent => mapEvent != null && !mapEvent.IsFinalized));
        foreach (var mapEvent in nextScanAt.Keys.Where(mapEvent => !active.Contains(mapEvent)).ToArray())
            nextScanAt.Remove(mapEvent);
        failedEvents.RemoveWhere(mapEvent => !active.Contains(mapEvent));
    }

    /// <summary>
    /// Mirrors vanilla's own guards, plus the co-op join window. Returns null when the event SHOULD be
    /// reinforced, otherwise the reason it was skipped - so "no reinforcement" is diagnosable instead of
    /// silent. A bare "nothing happened" is indistinguishable from "never ran", which cost real time.
    /// </summary>
    private static string WhyNotReinforce(MapEvent mapEvent)
    {
        if (mapEvent == null) return "null";
        if (mapEvent.IsFinalized) return "finalized";

        // Vanilla refuses these outright - a raid, a wall assault, and the forced supply/volunteer shakedowns
        // are not battles nearby parties may wander into.
        if (mapEvent.IsRaid) return "raid";
        if (mapEvent.IsSiegeAssault) return "siege assault";
        if (mapEvent.IsForcingSupplies) return "forcing supplies";
        if (mapEvent.IsForcingVolunteers) return "forcing volunteers";

        if (mapEvent.MapEventSettlement?.IsHideout == true) return "hideout";

        // Only player battles reinforce, and only while the window the broadcast path checks is still open -
        // otherwise the join would apply on the server and never reach the clients.
        if (!InteractionPatches.IsWithinAiJoinWindow(mapEvent))
            return "outside the AI join window (none opened, or it expired)";

        if (!mapEvent.InvolvedParties.Any(p => p?.IsMobile == true && p.MobileParty?.IsPlayerParty() == true))
            return "no player party involved";

        return null;
    }

    private static void Reinforce(MapEvent mapEvent)
    {
        Reinforce(mapEvent, FindNearbyParties(mapEvent));
    }

    /// <summary>
    /// Selects and adds eligible candidates using only authoritative map-event state. The candidate seam keeps the
    /// headless rule testable without constructing Bannerlord's spatial index.
    /// </summary>
    internal static void Reinforce(MapEvent mapEvent, IEnumerable<MobileParty> nearbyParties)
    {
        if (mapEvent == null || nearbyParties == null)
            return;

        var attackers = CollectMobileParties(mapEvent, BattleSideEnum.Attacker);
        var defenders = CollectMobileParties(mapEvent, BattleSideEnum.Defender);
        var attackerJoiners = new List<MobileParty>();
        var defenderJoiners = new List<MobileParty>();

        foreach (var party in nearbyParties)
        {
            if (!CanConsider(mapEvent, party))
                continue;

            var canJoinAttackers = mapEvent.CanPartyJoinBattle(party.Party, BattleSideEnum.Attacker);
            var canJoinDefenders = mapEvent.CanPartyJoinBattle(party.Party, BattleSideEnum.Defender);

            // A valid faction stance identifies exactly one side. Ambiguous or unresolved state is safer to skip
            // than to place an AI party on an arbitrary side.
            if (canJoinAttackers == canJoinDefenders)
                continue;

            (canJoinAttackers ? attackerJoiners : defenderJoiners).Add(party);
        }

        // Match DefaultEncounterModel: an ignored non-player party on one side prevents nearby parties from
        // reinforcing the other side.
        if (HasIgnoredAiParty(defenders) || HasIgnoredAiParty(defenderJoiners))
            attackerJoiners.Clear();
        if (HasIgnoredAiParty(attackers) || HasIgnoredAiParty(attackerJoiners))
            defenderJoiners.Clear();

        Logger.Debug("[Reinforce] {MapEventId}: scan found {Att} attacker / {Def} defender joiners",
            mapEvent.StringId ?? "<no id>", attackerJoiners.Count, defenderJoiners.Count);

        AddJoiners(mapEvent, BattleSideEnum.Attacker, attackerJoiners);
        AddJoiners(mapEvent, BattleSideEnum.Defender, defenderJoiners);
    }

    private static List<MobileParty> FindNearbyParties(MapEvent mapEvent)
    {
        var result = new List<MobileParty>();
        var model = Campaign.Current?.Models?.EncounterModel;
        if (mapEvent == null || model == null)
            return result;

        var position = mapEvent.Position;
        var radius = model.GetEncounterJoiningRadius;
        if (mapEvent.IsBlockade || mapEvent.IsBlockadeSallyOut)
        {
            position = mapEvent.MapEventSettlement?.PortPosition ?? position;
            radius = model.NeededMaximumDistanceForEncounteringBlockade * 3f;
        }

        var search = MobileParty.StartFindingLocatablesAroundPosition(position.ToVec2(), radius);
        for (var party = MobileParty.FindNextLocatable(ref search);
             party != null;
             party = MobileParty.FindNextLocatable(ref search))
        {
            result.Add(party);
        }

        return result;
    }

    private static bool CanConsider(MapEvent mapEvent, MobileParty party)
    {
        if (party?.IsActive != true ||
            party.IsPlayerParty() ||
            party.MapEvent != null ||
            party.IsInRaftState ||
            party.SiegeEvent != null ||
            party.CurrentSettlement != null ||
            party.AttachedTo != null)
        {
            return false;
        }

        var battleAtSea = mapEvent.IsBlockade ||
                          mapEvent.IsBlockadeSallyOut ||
                          mapEvent.InvolvedParties.Any(involved =>
                              involved?.IsMobile == true && involved.MobileParty?.IsCurrentlyAtSea == true);
        if (party.IsCurrentlyAtSea != battleAtSea && mapEvent.MapEventSettlement?.IsVillage != true)
            return false;

        return party.IsLordParty ||
               party.IsBandit ||
               party.IsPatrolParty ||
               party.ShouldJoinPlayerBattles;
    }

    private static bool HasIgnoredAiParty(IEnumerable<MobileParty> parties)
        => parties.Any(party => party != null && !party.IsPlayerParty() && party.ShouldBeIgnored);

    private static List<MobileParty> CollectMobileParties(MapEvent mapEvent, BattleSideEnum side)
    {
        var parties = new List<MobileParty>();

        var onSide = mapEvent.PartiesOnSide(side);
        if (onSide == null) return parties;

        foreach (var mapEventParty in onSide)
        {
            if (mapEventParty?.Party?.IsMobile != true) continue;
            parties.Add(mapEventParty.Party.MobileParty);
        }

        return parties;
    }

    private static void AddJoiners(MapEvent mapEvent, BattleSideEnum side, IEnumerable<MobileParty> parties)
    {
        var mapEventSide = mapEvent.GetMapEventSide(side);
        if (mapEventSide == null) return;

        foreach (var party in parties)
        {
            if (party?.MapEvent != null) continue;

            Logger.Debug("Nearby party {PartyId} joins the player battle on the {Side} side",
                party.StringId, side);
            mapEventSide.AddNearbyPartyToPlayerMapEvent(party);
        }
    }
}
