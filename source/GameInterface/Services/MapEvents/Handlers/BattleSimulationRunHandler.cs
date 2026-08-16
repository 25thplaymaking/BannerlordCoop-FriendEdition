using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Util;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MapEventSides.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Runs auto-resolve battle simulations authoritatively on the server, paced by the requesting client.
/// </summary>
/// <remarks>
/// Client: the send-troops consequence prefix asks <c>BattleStartCoordinator</c> to start the auto-resolve, which
/// blocks on the server's accept before the scoreboard opens;
/// the local engine is disabled and <see cref="BattleSimulationReplay"/> instead drives the playback off
/// <c>_numTicks</c>, emitting <see cref="RequestAdvanceBattleSimulation"/> each round.
/// Server: on the request it only sets the simulation up; each advance resolves that many rounds, syncing
/// casualties (via the TroopRoster patches) and <c>BattleState</c> as it goes and streaming the round's
/// scoreboard updates back as <see cref="NetworkBattleSimulationRound"/>. When the battle is decided it
/// finalizes and sends <see cref="NetworkBattleSimulationFinished"/>.
/// Client: replays each round onto the scoreboard and finishes once playback drains.
/// </remarks>
internal class BattleSimulationRunHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleSimulationRunHandler>();

    // Safety bound so a non-terminating simulation can never hang the server thread.
    private const int MaxSimulationRounds = 10000;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IMapEventLogger mapEventLogger;

    private sealed class ActiveSimulation
    {
        public NetPeer Peer;
        public MapEvent MapEvent;
        public ForwardingBattleObserver Observer;
        public IBattleObserver PreviousObserver;
    }

    private readonly Dictionary<string, ActiveSimulation> activeSimulations = new();
    private readonly object simLock = new();

    public BattleSimulationRunHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IMapEventLogger mapEventLogger,
        IPlayerManager playerManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.mapEventLogger = mapEventLogger;
        this.playerManager = playerManager;

        messageBroker.Subscribe<RequestAdvanceBattleSimulation>(Handle_RequestAdvanceBattleSimulation);
        messageBroker.Subscribe<NetworkAdvanceBattleSimulation>(Handle_NetworkAdvanceBattleSimulation);
        messageBroker.Subscribe<NetworkBattleSimulationRound>(Handle_NetworkBattleSimulationRound);
        messageBroker.Subscribe<NetworkBattleSimulationLoot>(Handle_NetworkBattleSimulationLoot);
        messageBroker.Subscribe<NetworkBattleSimulationFinished>(Handle_NetworkBattleSimulationFinished);
        messageBroker.Subscribe<NetworkOpenBattleSimulation>(Handle_NetworkOpenBattleSimulation);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        messageBroker.Subscribe<MapEventPartyBattlePartyAdded>(Handle_MapEventPartyBattlePartyAdded);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<RequestAdvanceBattleSimulation>(Handle_RequestAdvanceBattleSimulation);
        messageBroker.Unsubscribe<NetworkAdvanceBattleSimulation>(Handle_NetworkAdvanceBattleSimulation);
        messageBroker.Unsubscribe<NetworkBattleSimulationRound>(Handle_NetworkBattleSimulationRound);
        messageBroker.Unsubscribe<NetworkBattleSimulationLoot>(Handle_NetworkBattleSimulationLoot);
        messageBroker.Unsubscribe<NetworkBattleSimulationFinished>(Handle_NetworkBattleSimulationFinished);
        messageBroker.Unsubscribe<NetworkOpenBattleSimulation>(Handle_NetworkOpenBattleSimulation);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        messageBroker.Unsubscribe<MapEventPartyBattlePartyAdded>(Handle_MapEventPartyBattlePartyAdded);
    }

    /// <summary>[Client] Forward a playback-paced advance to the server.</summary>
    private void Handle_RequestAdvanceBattleSimulation(MessagePayload<RequestAdvanceBattleSimulation> payload)
    {
        network.SendAll(new NetworkAdvanceBattleSimulation(payload.What.MapEventId, payload.What.Rounds));
    }

    /// <summary>[Server, game thread] Sets up an admitted simulation and queues its canonical state publication.</summary>
    internal BattleStartDecision TryStartSimulation(string mapEventId, MapEvent mapEvent, NetPeer requestingPeer,
        MobileParty requestingParty, long requestId)
    {
        bool claimed = false;
        bool accepted = false;
        bool activeSimulationAdded = false;
        bool publicationStarted = false;
        string publicationStage = "not-started";
        IBattleObserver previousObserver = null;
        try
        {
            if (mapEvent.IsUnsupportedMultiPlayerHostileAction())
                return BattleStartDecision.Reject("unsupported-hostile-action");
            if (!ServerBattleModeArbiter.TryClaimSimulation(mapEventId, out var isNewSimulationClaim))
                return BattleStartDecision.Reject("conflicting-battle-mode");
            claimed = isNewSimulationClaim;
            lock (simLock)
            {
                if (activeSimulations.ContainsKey(mapEventId))
                    return BattleStartDecision.Reject("duplicate-simulation-start");
            }

            var observer = new ForwardingBattleObserver(objectManager);
            previousObserver = mapEvent.BattleObserver;
            mapEvent.BattleObserver = observer;
            mapEvent.SimulateBattleSetup(null);
            observer.FlushRound();
            lock (simLock)
            {
                activeSimulations[mapEventId] = new ActiveSimulation
                {
                    Peer = requestingPeer,
                    MapEvent = mapEvent,
                    Observer = observer,
                    PreviousObserver = previousObserver,
                };
                activeSimulationAdded = true;
            }

            // The route sends its result only after these reliable state messages have been queued.
            publicationStage = "battle-mode-set";
            publicationStarted = true;
            network.SendAll(new NetworkBattleModeSet(mapEventId, (int)BattleStartMode.Simulation));
            publicationStage = "open-battle-simulation";
            network.SendAllBut(requestingPeer, new NetworkOpenBattleSimulation(mapEventId));
            mapEventLogger.DebugMapEvent(mapEvent, "Battle simulation set up; awaiting client-paced advances");
            accepted = true;
            return BattleStartDecision.Accepted();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to set up admitted battle simulation for {MapEventId}", mapEventId);
            if (activeSimulationAdded && !publicationStarted)
            {
                lock (simLock) activeSimulations.Remove(mapEventId);
            }
            if (!accepted && !publicationStarted) mapEvent.BattleObserver = previousObserver;
            if (publicationStarted)
            {
                Logger.Fatal("Partial authority publication; isolating simulation participants. Route={Route} RequestId={RequestId} Event={Event} Mode={Mode} Stage={Stage}",
                    "map-event.battle-start", requestId, mapEventId, BattleStartMode.Simulation, publicationStage);
                DisconnectSimulationParticipants(mapEvent);
            }
            return publicationStarted
                ? BattleStartDecision.Isolated("simulation-start-partial-publication")
                : BattleStartDecision.Failed("simulation-start-failed");
        }
        finally
        {
            // Preserve the claim after a partial publication; handing this event to the other mode would
            // compound a transport failure into conflicting authoritative mutation.
            if (claimed && !accepted && !publicationStarted) ServerBattleModeArbiter.Release(mapEventId);
        }
    }

    private void DisconnectSimulationParticipants(MapEvent mapEvent)
    {
        // ActiveSimulation retains the requester until this disconnect reaches the existing orphan cleanup path.
        foreach (var player in playerManager.Players)
        {
            if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party) ||
                mapEvent.FindMapEventParty(party.Party) == null ||
                !playerManager.TryGetPeer(player.ControllerId, out var peer))
                continue;
            peer.Disconnect();
        }
    }

    /// <summary>
    /// [Server] A party was added to a side that has an active simulation (a player joining the battle, or an
    /// AI reinforcement). The simulation's troop pool was allocated once at setup and never re-reads the side's
    /// parties, so without this the joiner never fights and never appears on any scoreboard. Fold its troops
    /// into the live pool; the +1s fired here are captured by the attached observer and stream out with the
    /// next advance, so every client with the window open gets the new rows through the normal round pipeline.
    /// </summary>
    private void Handle_MapEventPartyBattlePartyAdded(MessagePayload<MapEventPartyBattlePartyAdded> payload)
    {
        if (ModInformation.IsClient)
            return;

        var side = payload.What.MapEventSide;
        var joiningParty = payload.What.MapEventParty;
        if (side?.MapEvent == null || joiningParty == null)
            return;

        // Publishing happens during the native AddPartyInternal, which already runs on the game thread; this
        // only touches the simulation pool via the party object, so it is safe here. GameThread.RunSafe runs inline
        // when already on that thread.
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetId(side.MapEvent, out var mapEventId))
                return;

            ActiveSimulation sim;
            lock (simLock)
            {
                if (!activeSimulations.TryGetValue(mapEventId, out sim))
                    return;
            }

            if (sim.MapEvent.IsUnsupportedMultiPlayerHostileAction())
            {
                EndSimulationSession(sim);
                lock (simLock)
                {
                    activeSimulations.Remove(mapEventId);
                }

                CompleteSimulation(mapEventId, sim.MapEvent);
                mapEventLogger.DebugMapEvent(sim.MapEvent, "Stopped hostile-action battle simulation because an unsupported hostile-action player party joined");
                return;
            }

            AddPartyToActiveSimulation(side, joiningParty);
        }, context: nameof(Handle_MapEventPartyBattlePartyAdded));
    }

    /// <summary>[Server, main thread] Allocate a late-joining party's troops into the live simulation pool.</summary>
    private void AddPartyToActiveSimulation(MapEventSide side, MapEventParty joiningParty)
    {
        try
        {
            // A troop-limited battle (hideout, lord's hall) trims and locks its rosters at setup; vanilla
            // refuses to re-ready a locked side, so a late joiner cannot be folded in safely there.
            if (side._troopAllocationsLocked)
                return;

            if (side._simulationTroopList == null || side._allocatedTroops == null || side._readyTroopsPriorityList == null)
                return;

            var sizeOfParty = joiningParty.Party.NumberOfHealthyMembers;
            if (sizeOfParty <= 0)
                return;

            // Build the joiner's ready-troop entries and allocate them, mirroring MapEventSide.MakeReadyParty +
            // AllocateTroops but only for this one party so the existing parties' allocations stay untouched.
            int startIndex = side._readyTroopsPriorityList.Count;
            joiningParty.SetParticipatingTroopCount(sizeOfParty);
            joiningParty.Update();
            Campaign.Current.Models.TroopSupplierProbabilityModel
                .EnqueueTroopSpawnProbabilitiesAccordingToUnitSpawnPrioritization(
                    joiningParty, null, false, sizeOfParty, false, side._readyTroopsPriorityList);

            var observer = side.MapEvent.BattleObserver;
            int allocated = 0;
            for (int i = startIndex; i < side._readyTroopsPriorityList.Count; i++)
            {
                var (element, party, _) = side._readyTroopsPriorityList[i];
                var descriptor = element.Descriptor;
                if (side._allocatedTroops.ContainsKey(descriptor))
                    continue;

                side._simulationTroopList.Add(descriptor);
                side._allocatedTroops.Add(descriptor, party);
                observer?.TroopNumberChanged(side.MissionSide, party.Party, element.Troop, 1);
                allocated++;
            }

            side._readyTroopsPriorityList.RemoveRange(startIndex, side._readyTroopsPriorityList.Count - startIndex);

            mapEventLogger.DebugMapEvent(side.MapEvent,
                "Folded joining party {PartyId} into the active simulation ({TroopCount} troops)",
                joiningParty.Party.Id, allocated);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to add joining party to active battle simulation");
        }
    }

    /// <summary>[Server] Resolve the requested number of rounds, streaming each round back.</summary>
    private void Handle_NetworkAdvanceBattleSimulation(MessagePayload<NetworkAdvanceBattleSimulation> payload)
    {
        if (ModInformation.IsClient)
            return;

        var mapEventId = payload.What.MapEventId;

        ActiveSimulation sim;
        lock (simLock)
        {
            if (!activeSimulations.TryGetValue(mapEventId, out sim))
                return;
        }

        var maxRounds = payload.What.MaxRounds;
        var finished = false;
        NetworkBattleSimulationLoot lootMessage = default;
        var hasLoot = false;

        GameThread.RunSafe(() =>
        {
            if (sim.MapEvent.IsUnsupportedMultiPlayerHostileAction())
            {
                EndSimulationSession(sim);
                finished = true;
                return;
            }

            // Accumulate every round resolved in this advance into one update. Normal playback advances
            // a single round per call (one packet per round, as before), but a "skip" resolves the whole
            // remaining battle in one advance: batching keeps that from flooding the peer's outbound
            // queue with a packet per round.
            var batched = new List<BattleSimTroopChange>();

            int rounds = 0;
            while (rounds < maxRounds && rounds < MaxSimulationRounds && !sim.MapEvent.HasWinner)
            {
                rounds++;
                sim.MapEvent.SimulatePlayerEncounterBattle();

                var changes = sim.Observer.FlushRound();
                if (changes.Length > 0)
                    batched.AddRange(changes);
            }

            // Broadcast to everyone: the pacer and the spectators (clients in this event) all replay these rounds.
            // Clients not in the event ignore rounds for a map event they have no active playback for.
            if (batched.Count > 0)
                network.SendAll(new NetworkBattleSimulationRound(mapEventId, batched.ToArray()));

            if (sim.MapEvent.HasWinner)
            {
                // Capture the casualties and winner contributions before tearing the simulation down so the
                // winning client can re-run the native loot flow locally and open its loot screen.
                if (PlayerWonSimulation(sim.MapEvent))
                {
                    lootMessage = new NetworkBattleSimulationLoot(
                        mapEventId,
                        sim.MapEvent.BattleState,
                        CollectDefeatedCasualties(sim.MapEvent),
                        CollectWinnerContributions(sim.MapEvent));
                    hasLoot = true;
                }

                EndSimulationSession(sim);
                finished = true;
            }
        }, blocking: true, context: nameof(Handle_NetworkAdvanceBattleSimulation));

        if (finished)
        {
            lock (simLock)
            {
                activeSimulations.Remove(mapEventId);
            }

            mapEventLogger.DebugMapEvent(sim.MapEvent, "Server-side battle simulation finished. BattleState={BattleState}", sim.MapEvent.BattleState);

            // Loot first so the client applies it before the finish closes the playback. Broadcast: every
            // winning-side player (the pacer or a joiner) needs the loot flow; each client applies it only
            // if its own party is among the winners.
            if (hasLoot)
                network.SendAll(lootMessage);

            CompleteSimulation(mapEventId, sim.MapEvent);
        }
    }

    /// <summary>[Client] Resolve a streamed round and queue it for playback.</summary>
    private void Handle_NetworkBattleSimulationRound(MessagePayload<NetworkBattleSimulationRound> payload)
    {
        var message = payload.What;
        if (message.Changes == null || message.Changes.Length == 0)
            return;

        // Resolve and enqueue on the main thread: objectManager can be mutated by the main thread's
        // Add/Remove, and BattleSimulationReplay's round queue is drained on the main-thread tick.
        GameThread.RunSafe(() =>
        {
            // Rounds are broadcast to everyone; only clients actually playing this simulation (the pacer and the
            // in-event spectators) replay them. Checked here (not on the network thread) so it observes the Begin
            // done by NetworkOpenBattleSimulation, which is queued onto the main thread before this.
            if (!BattleSimulationReplay.IsActiveFor(message.MapEventId))
                return;

            var resolved = new List<BattleSimulationReplay.ResolvedChange>(message.Changes.Length);
            foreach (var change in message.Changes)
            {
                if (!objectManager.TryGetObject<PartyBase>(change.PartyId, out var party))
                    continue;

                // A client builds its own party's troops from its local scoreboard setup when it opens, so it must
                // never re-add them from the stream. The only positive change for the local party is the server's
                // fold-in +1 fired when this client joined an in-progress simulation, which would double its count.
                if (party == PartyBase.MainParty && change.Number > 0)
                    continue;

                if (!TryResolveCharacterObject(change.CharacterId, change.IsHero, out var character))
                    continue;

                resolved.Add(new BattleSimulationReplay.ResolvedChange(
                    (BattleSideEnum)change.Side, party, character,
                    change.Number, change.NumberKilled, change.NumberWounded, change.NumberRouted, change.KillCount, change.NumberReadyToUpgrade));
            }

            if (resolved.Count > 0)
                BattleSimulationReplay.EnqueueRound(resolved.ToArray());
        }, context: nameof(Handle_NetworkBattleSimulationRound));
    }

    /// <summary>[Server] True when a player party is on the winning side, so a client needs the loot screen.</summary>
    private static bool PlayerWonSimulation(MapEvent mapEvent)
    {
        if (mapEvent.WinningSide == BattleSideEnum.None)
            return false;

        return mapEvent.GetMapEventSide(mapEvent.WinningSide).Parties
            .Any(p => p.Party.MobileParty?.IsPlayerParty() == true);
    }

    /// <summary>[Server] Serialize each defeated party's simulation casualties for the client to replay.</summary>
    private BattleSimDefeatedParty[] CollectDefeatedCasualties(MapEvent mapEvent)
    {
        var parties = new List<BattleSimDefeatedParty>();

        foreach (var defeated in mapEvent.GetMapEventSide(mapEvent.DefeatedSide).Parties)
        {
            if (!objectManager.TryGetId(defeated.Party, out var partyId))
                continue;

            var died = SerializeCasualties(defeated.DiedInBattle);
            var wounded = SerializeCasualties(defeated.WoundedInBattle);
            if (died.Length == 0 && wounded.Length == 0)
                continue;

            parties.Add(new BattleSimDefeatedParty(partyId, died, wounded));
        }

        return parties.ToArray();
    }

    /// <summary>[Server] Serialize each winning party's battle contribution; the loot chance models need it &gt; 0.</summary>
    private BattleSimWinner[] CollectWinnerContributions(MapEvent mapEvent)
    {
        var winners = new List<BattleSimWinner>();

        foreach (var winner in mapEvent.GetMapEventSide(mapEvent.WinningSide).Parties)
        {
            if (!objectManager.TryGetId(winner.Party, out var partyId))
                continue;

            winners.Add(new BattleSimWinner(partyId, winner.ContributionToBattle));
        }

        return winners.ToArray();
    }

    private BattleSimCasualty[] SerializeCasualties(TroopRoster roster)
    {
        var casualties = new List<BattleSimCasualty>();

        foreach (var element in roster.GetTroopRoster())
        {
            var character = element.Character;
            if (character == null)
                continue;

            var isHero = character.IsHero;
            var objectToResolve = isHero ? (object)character.HeroObject : character;
            if (objectToResolve == null || !objectManager.TryGetId(objectToResolve, out var characterId))
                continue;

            casualties.Add(new BattleSimCasualty(characterId, isHero, element.Number, element.WoundedNumber));
        }

        return casualties.ToArray();
    }

    /// <summary>
    /// [Client] Replay the simulation casualties and apply the winning <c>BattleState</c> so the native
    /// PlayerEncounter result flow rolls the loot and opens the loot screen on the next tick.
    /// </summary>
    private void Handle_NetworkBattleSimulationLoot(MessagePayload<NetworkBattleSimulationLoot> payload)
    {
        var message = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!BattleSimulationReplay.IsActiveFor(message.MapEventId))
                return;

            if (!objectManager.TryGetObject<MapEvent>(message.MapEventId, out var mapEvent))
                return;

            // Broadcast message: only a client whose own party is on the winning side runs the loot flow;
            // losing or uninvolved spectators ignore it.
            if (!IsWinningLootRecipient(message))
                return;

            // Re-applying server-authoritative results; the roster patches must stand down during the apply.
            using (new AllowedThread())
            {
                ApplyWinnerContributions(mapEvent, message.Winners);
                ApplyDefeatedPartyCasualties(mapEvent, message.DefeatedParties);
                mapEvent.BattleState = message.WinningState;
            }
        });
    }

    private bool IsWinningLootRecipient(NetworkBattleSimulationLoot message)
    {
        if (!objectManager.TryGetId(PartyBase.MainParty, out var mainPartyId))
            return false;

        return message.Winners?.Any(w => w.PartyId == mainPartyId) == true;
    }

    private void ApplyWinnerContributions(MapEvent mapEvent, BattleSimWinner[] winners)
    {
        // The loot/capture chance models drop any winner with ContributionToBattle == 0, which is the
        // case on the client (its simulation engine never ran). Restore the server's values first.
        foreach (var winner in winners ?? Array.Empty<BattleSimWinner>())
        {
            if (!objectManager.TryGetObject<PartyBase>(winner.PartyId, out var winnerParty))
                continue;

            var winnerMapEventParty = mapEvent.FindMapEventParty(winnerParty);
            if (winnerMapEventParty != null)
                winnerMapEventParty._contributionToBattle = winner.ContributionToBattle;
        }
    }

    private void ApplyDefeatedPartyCasualties(MapEvent mapEvent, BattleSimDefeatedParty[] defeatedParties)
    {
        foreach (var defeated in defeatedParties ?? Array.Empty<BattleSimDefeatedParty>())
        {
            if (!objectManager.TryGetObject<PartyBase>(defeated.PartyId, out var party))
                continue;

            var mapEventParty = mapEvent.FindMapEventParty(party);
            if (mapEventParty == null)
                continue;

            ApplyCasualties(mapEventParty.DiedInBattle, defeated.Died);
            ApplyCasualties(mapEventParty.WoundedInBattle, defeated.Wounded);
        }
    }

    private void ApplyCasualties(TroopRoster roster, BattleSimCasualty[] casualties)
    {
        if (casualties == null)
            return;

        foreach (var casualty in casualties)
        {
            if (!TryResolveCharacterObject(casualty.CharacterId, casualty.IsHero, out var character))
                continue;

            roster.AddToCounts(character, casualty.Number, insertAtFront: false, casualty.WoundedNumber);
        }
    }

    /// <summary>[Client] Server finished simulating: end playback once the queued rounds drain.</summary>
    private void Handle_NetworkBattleSimulationFinished(MessagePayload<NetworkBattleSimulationFinished> payload)
    {
        // Both the encounter state and the replay's finish flag belong to the main-thread tick.
        GameThread.RunSafe(() =>
        {
            // Broadcast to everyone; only a client actually playing this simulation finishes it.
            if (!BattleSimulationReplay.IsActiveFor(payload.What.MapEventId))
                return;

            if (PlayerEncounter.CurrentBattleSimulation == null)
            {
                Logger.Warning("Received {Message} but no battle simulation is active", nameof(NetworkBattleSimulationFinished));
                return;
            }

            BattleSimulationReplay.RequestFinish();
        }, context: nameof(Handle_NetworkBattleSimulationFinished));
    }

    /// <summary>
    /// [Client] Another player started an auto-resolve simulation for a map event. If this client's own party is in
    /// that event (and it isn't the initiator), open the same simulation window as a passive spectator so it can
    /// watch the server-streamed results. Clients not in the event ignore it.
    /// </summary>
    private void Handle_NetworkOpenBattleSimulation(MessagePayload<NetworkOpenBattleSimulation> payload)
    {
        if (ModInformation.IsServer)
            return;

        var mapEventId = payload.What.MapEventId;

        GameThread.RunSafe(() =>
        {
            // The initiator already has it open and is pacing it — it opened synchronously when the server accepted
            // its blocking request, marking the replay active.
            if (BattleSimulationReplay.IsActiveFor(mapEventId))
                return;

            if (!objectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent) || mapEvent == null)
                return;

            // Only a player actually in this battle (in its encounter) spectates it.
            if (PlayerEncounter.Current == null || PlayerEncounter.Battle != mapEvent)
                return;

            if (PlayerEncounter.CurrentBattleSimulation != null)
                return;

            CloseEncounterMenuBehindSimulation();

            var mapState = Game.Current.GameStateManager.LastOrDefault<MapState>();
            if (mapState == null)
            {
                Logger.Warning("Cannot open spectator battle simulation: no active MapState");
                return;
            }

            // Begin (which marks the replay active) must precede StartBattleSimulation. InitSimulation(null, null)
            // builds the scoreboard from the event's full parties; the server-streamed rounds then drive it.
            BattleSimulationReplay.Begin(mapEventId, spectator: true);
            PlayerEncounter.InitSimulation(null, null);
            mapState.StartBattleSimulation();

            mapEventLogger.DebugMapEvent(mapEvent, "Opened spectator battle simulation window");
        }, context: nameof(Handle_NetworkOpenBattleSimulation));
    }

    private static void CloseEncounterMenuBehindSimulation()
    {
        var campaign = Campaign.Current;
        var mapState = Game.Current?.GameStateManager?.ActiveState as MapState;
        var menuContext = campaign?.CurrentMenuContext;

        try
        {
            GameMenu.ExitToLast();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to exit encounter menu before opening spectator battle simulation");
        }

        try
        {
            menuContext?.Destroy();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to destroy encounter menu before opening spectator battle simulation");
        }

        if (mapState?.AtMenu == true)
            mapState.ExitMenuMode();
        if (mapState != null)
            mapState.GameMenuId = null;
        if (campaign?.MapStateData != null)
            campaign.MapStateData.GameMenuId = null;
    }

    /// <summary>
    /// [Server] The pacing client dropped: finish and tear down any simulations it was driving so the
    /// swapped-in observer is restored and the tracking entry doesn't leak.
    /// </summary>
    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (ModInformation.IsClient)
            return;

        var peer = payload.What.PlayerId;

        List<KeyValuePair<string, ActiveSimulation>> orphaned;
        lock (simLock)
        {
            orphaned = activeSimulations.Where(entry => entry.Value.Peer == peer).ToList();
        }

        if (orphaned.Count == 0)
            return;

        GameThread.RunSafe(() =>
        {
            foreach (var entry in orphaned)
            {
                var sim = entry.Value;

                // No client left to pace the playback, so resolve whatever remains and let the battle
                // reach its decision (casualties still sync to the other clients via the troop-roster
                // patches) instead of leaving the map event half-simulated.
                int rounds = 0;
                while (rounds < MaxSimulationRounds && !sim.MapEvent.HasWinner)
                {
                    rounds++;
                    sim.MapEvent.SimulatePlayerEncounterBattle();
                }

                EndSimulationSession(sim);
                mapEventLogger.DebugMapEvent(sim.MapEvent, "Battle simulation client disconnected; finished server-side. BattleState={BattleState}", sim.MapEvent.BattleState);

                // Tell the spectators the simulation is over, otherwise they stay stuck in the spectator window now
                // that the pacing client (which would have driven it to completion) is gone.
                CompleteSimulation(entry.Key, sim.MapEvent);
            }
        }, blocking: true, context: nameof(Handle_PlayerDisconnected));

        lock (simLock)
        {
            foreach (var entry in orphaned)
                activeSimulations.Remove(entry.Key);
        }
    }

    /// <summary>
    /// [Server] Close client playback, then publish <see cref="MapEventConcluded"/> for a decided auto-resolve
    /// so the shared finalize path runs — <see cref="BattleFinalizeHandler"/> destroys the defeated party and
    /// closes every involved player's encounter, exactly as a manual battle's victory <c>BattleState</c> does.
    /// Manual battles reach this via <c>NetworkChangeBattleState</c>; the server-run simulation sets the state internally and never
    /// travels that route, so without this the beaten party survives and the encounter menu loops. An undecided
    /// stop releases the simulation claim so the battle can be retried. The finalize handler dedupes per event,
    /// so this is safe alongside any other finalize.
    /// </summary>
    internal void CompleteSimulation(string mapEventId, MapEvent mapEvent)
    {
        network.SendAll(new NetworkBattleSimulationFinished(mapEventId));

        if (mapEvent == null || !mapEvent.HasWinner)
        {
            ServerBattleModeArbiter.Release(mapEventId);
            return;
        }

        var playerPartyIds = MapEventPlayerPartyCollector.CollectPartyIds(mapEvent, objectManager);
        messageBroker.Publish(this, new MapEventConcluded(mapEventId, playerPartyIds));
    }

    /// <summary>
    /// [Server, main thread] End the simulation session: commit XP and release the simulation troop
    /// allocations exactly like <c>MapEvent.SimulateBattleRoundEndSession</c>, then restore the observer.
    /// </summary>
    private static void EndSimulationSession(ActiveSimulation sim)
    {
        foreach (var side in sim.MapEvent._sides)
        {
            sim.MapEvent.CommitXpGains();
            side.EndSimulation();
        }

        sim.MapEvent.BattleObserver = sim.PreviousObserver;
    }

    private bool TryResolveCharacterObject(string objectId, bool isHero, out CharacterObject characterObject)
    {
        characterObject = null;

        if (isHero)
        {
            if (!objectManager.TryGetObject<Hero>(objectId, out var hero))
                return false;

            characterObject = hero.CharacterObject;
            return characterObject != null;
        }

        return objectManager.TryGetObject(objectId, out characterObject);
    }
}
