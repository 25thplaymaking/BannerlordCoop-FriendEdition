using Common;
using Common.Messaging;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PlayerCaptivityService.Messages;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Server-side auto-conclude for the player-vs-defeated-bandit menu softlock.
///
/// When a co-op client's party meets a much-weaker bandit party (e.g. Looters at 0 healthy troops), the
/// server owns a real field-battle <see cref="MapEvent"/> (created via the client's routed
/// <c>StartBattleInternalPrefix</c>), but it holds it INERT — <c>MapEventPatches.PrefixUpdate</c> blocks
/// <c>MapEvent.Update</c> whenever a client-controlled party is involved, so it never auto-resolves. The
/// only client path to finish it, the "Capture the enemy" menu consequence, is disabled with no
/// replacement, so the encounter never concludes and the client is stuck on the menu.
///
/// This detector runs on the server's <see cref="CampaignTick"/>, finds a player-involved field battle
/// whose enemy side is entirely defeated while <c>BattleState</c> is still <c>None</c>, and publishes the
/// existing <see cref="AuthoritativeBattleConclusionRequested"/> — the same authoritative
/// capture → finalize → <c>NetworkClosePvpEncounter</c> pipeline every player battle-victory already uses,
/// which sends the encountering client the close so its menu resolves. No new message type and no client
/// code: only this filtered scan. NOTE: publishing while <c>BattleState == None</c> is required —
/// <c>DoSurrender</c> sets the state synchronously and <c>ApplyBattleStateChange</c> then early-returns
/// without publishing <c>MapEventConcluded</c>, so it would capture but never close.
///
/// Guardrails: server-only; field battles without a settlement; a live player fighter on the friendly side
/// (no mutual-wipeout); no player on the enemy side (skip PvP); not claimed by a mission/simulation
/// (<see cref="ServerBattleModeArbiter"/>) or a host (<see cref="IBattleHostRegistry"/>); and a stable-tick
/// dwell so we never fire inside the client's MapEvent-adoption window. The dwell threshold and the
/// post-conclude client UX want live-MP verification.
/// </summary>
internal class BanditEncounterAutoConcludeHandler : IHandler
{
    /// <summary>
    /// A candidate must be seen defeated on this many consecutive server ticks before we conclude it, so
    /// the auto-conclude never races the client's <c>MapEventCreationCoordinator.RequestBlocking</c>
    /// adoption. Conservative; tune on a live session.
    /// </summary>
    private const int RequiredStableTicks = 3;

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IBattleHostRegistry hostRegistry;

    // Game-thread only (CampaignTick). Weak-keyed so a destroyed map event is collected, not leaked.
    private readonly ConditionalWeakTable<MapEvent, StableCount> observed = new();
    private readonly ConditionalWeakTable<MapEvent, object> concluded = new();

    public BanditEncounterAutoConcludeHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IBattleHostRegistry hostRegistry)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.hostRegistry = hostRegistry;

        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    private sealed class StableCount { public int Ticks; }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (ModInformation.IsClient) return;

        var manager = Campaign.Current?.MapEventManager;
        if (manager?.MapEvents == null) return;

        // Snapshot: concluding an event mutates the manager's collection downstream.
        foreach (MapEvent mapEvent in manager.MapEvents.ToArray())
        {
            if (mapEvent == null) continue;

            if (!TryGetDefeatedBanditWinner(mapEvent, out BattleState winner))
            {
                observed.Remove(mapEvent);
                continue;
            }

            StableCount counter = observed.GetOrCreateValue(mapEvent);
            counter.Ticks++;
            if (counter.Ticks < RequiredStableTicks) continue;
            if (concluded.TryGetValue(mapEvent, out _)) continue;

            if (!objectManager.TryGetIdWithLogging(mapEvent, out string mapEventId)) continue;
            if (ServerBattleModeArbiter.IsClaimed(mapEventId)) continue; // a live mission/simulation owns it
            if (hostRegistry.TryGet(mapEventId, out _)) continue;        // a host is resolving it

            concluded.Add(mapEvent, new object());
            messageBroker.Publish(this, new AuthoritativeBattleConclusionRequested(mapEventId, winner, 0));
        }
    }

    /// <summary>
    /// True when <paramref name="mapEvent"/> is a player-involved field battle whose enemy side is entirely
    /// defeated while still unconcluded; <paramref name="winner"/> is the player side's victory state.
    /// </summary>
    private static bool TryGetDefeatedBanditWinner(MapEvent mapEvent, out BattleState winner)
    {
        winner = BattleState.None;

        if (!mapEvent.IsFieldBattle) return false;
        if (mapEvent.MapEventSettlement != null) return false;
        if (mapEvent.IsFinalized) return false;
        if (mapEvent.BattleState != BattleState.None) return false;
        if (!mapEvent.ContainsPlayerParty()) return false;

        MapEventSide attacker = mapEvent.AttackerSide;
        MapEventSide defender = mapEvent.DefenderSide;
        if (attacker?.Parties == null || defender?.Parties == null) return false;

        bool playerOnAttacker = SideHasPlayer(attacker);
        MapEventSide friendly = playerOnAttacker ? attacker : defender;
        MapEventSide enemy = playerOnAttacker ? defender : attacker;

        // Friendly side must still have a live fighter (avoids concluding a mutual wipeout).
        if (!SideHasHealthyMember(friendly)) return false;

        // Enemy side must be entirely defeated, hold no settlement, and hold no player party (skip PvP).
        foreach (MapEventParty ep in enemy.Parties)
        {
            PartyBase party = ep?.Party;
            if (party == null) continue;
            if (party.IsSettlement) return false;
            if (party.MobileParty?.IsPlayerParty() == true) return false;
            if (party.NumberOfHealthyMembers > 0) return false;
        }

        winner = playerOnAttacker ? BattleState.AttackerVictory : BattleState.DefenderVictory;
        return true;
    }

    private static bool SideHasPlayer(MapEventSide side) =>
        side.Parties.Any(p => p?.Party?.MobileParty?.IsPlayerParty() == true);

    private static bool SideHasHealthyMember(MapEventSide side) =>
        side.Parties.Any(p => p?.Party != null && p.Party.NumberOfHealthyMembers > 0);

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
    }
}
