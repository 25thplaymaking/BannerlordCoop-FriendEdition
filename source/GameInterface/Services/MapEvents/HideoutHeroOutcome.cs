using Common.Logging;
using GameInterface.Services.MobileParties.Extensions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Decides a hideout by the hero who went in, the way the base game does, rather than by who still has
/// bodies on the roster.
/// </summary>
/// <remarks>
/// A hideout is not an ordinary battle. In the base game it is settled by the attacking hero: go down
/// and your men drag you out wounded, the hideout is untouched, and it has to be raided again once you
/// have healed. Co-op lost that, so a player could enter, die, and still be paid for clearing it.
/// <para>
/// The reward request was never the bug — the client already gates it on
/// <c>battle.WinningSide == encounter.PlayerSide</c>. What was wrong is who the winner was.
/// <c>MapEvent.CheckIfOneSideHasLost</c> decides on <c>RecalculateMemberCountOfSide</c>, so a downed
/// hero whose escort still has members reads as an attacker victory. The hero-decides rule lives in the
/// base game's <c>HideoutMissionController</c>, and co-op substitutes its own battle controllers, so
/// nothing was left to apply it.
/// </para>
/// <para>
/// This restores it on the authority, which is the only side entitled to decide an outcome. Everything
/// downstream then follows on its own: the server sends each client the corrected
/// <c>WinningSide</c>, the client's reward gate does not fire, no loot is committed, and the hideout
/// keeps its bandits and must be raided again.
/// </para>
/// <para>
/// Wounded is a sound test for "went down here" rather than "arrived hurt", because the base game will
/// not let a wounded hero start a hideout mission in the first place — <c>HideoutCampaignBehavior</c>
/// blocks both the send-troops and direct-assault menu options on <c>Hero.MainHero.IsWounded</c>. A
/// hero who is wounded when the battle resolves was healthy when it began.
/// </para>
/// </remarks>
internal static class HideoutHeroOutcome
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HideoutHeroOutcome));

    /// <summary>
    /// Map events whose hideout outcome has already been settled, so forcing the winner — which
    /// re-enters <c>OnBattleWon</c> through the <c>BattleState</c> setter — cannot recurse.
    /// </summary>
    private static readonly ConditionalWeakTable<MapEvent, object> Decided = new ConditionalWeakTable<MapEvent, object>();
    private static readonly object DecidedMarker = new object();

    /// <summary>One attacking party, reduced to what the decision actually depends on.</summary>
    internal readonly struct Attacker
    {
        public Attacker(bool isPlayerParty, bool hasLeader, bool leaderWounded)
        {
            IsPlayerParty = isPlayerParty;
            HasLeader = hasLeader;
            LeaderWounded = leaderWounded;
        }

        public bool IsPlayerParty { get; }
        public bool HasLeader { get; }
        public bool LeaderWounded { get; }
    }

    /// <summary>
    /// Whether the defenders should be declared the winner. Pure, so the rule can be tested without a
    /// campaign.
    /// </summary>
    internal static bool DefendersWin(bool isHideoutBattle, bool defendersAlreadyWon, IEnumerable<Attacker> attackers)
    {
        // Only hideouts. Every other battle is settled by troops, which is correct for it.
        if (!isHideoutBattle) return false;

        // Nothing to correct, and re-forcing a winner would re-enter the result path for no reason.
        if (defendersAlreadyWon) return false;

        if (attackers == null) return false;

        foreach (Attacker attacker in attackers)
        {
            // AI parties are left entirely alone: the hero rule is the PLAYER's hideout experience, and
            // an AI raid resolved by simulation has no hero standing in a mission to go down.
            if (!attacker.IsPlayerParty || !attacker.HasLeader) continue;

            if (attacker.LeaderWounded) return true;
        }

        return false;
    }

    /// <summary>
    /// Applies the rule to a live map event. True when the outcome was flipped, in which case the
    /// caller must stand down — forcing the winner re-enters the result path, and that re-entry does
    /// the work.
    /// </summary>
    internal static bool TryForceDefenderVictory(MapEvent mapEvent)
    {
        if (mapEvent == null) return false;

        try
        {
            if (!mapEvent.IsHideoutBattle) return false;

            // Settle a hideout once. The forced winner re-enters through the BattleState setter, and
            // that second pass has to run the ordinary result path rather than decide again.
            if (Decided.TryGetValue(mapEvent, out _)) return false;
            Decided.Add(mapEvent, DecidedMarker);

            if (!DefendersWin(
                    isHideoutBattle: true,
                    defendersAlreadyWon: mapEvent.BattleState == BattleState.DefenderVictory,
                    attackers: DescribeAttackers(mapEvent)))
            {
                return false;
            }

            Logger.Information(
                "Hideout decided against the attackers: the raiding hero went down, so the hideout stands.");

            mapEvent.SetOverrideWinner(BattleSideEnum.Defender);
            return true;
        }
        catch (Exception error)
        {
            // A battle that resolves the old way is far better than one that never resolves, so any
            // failure here leaves the outcome exactly as it was.
            Logger.Error(error, "Could not apply the hideout hero rule; leaving the battle outcome alone.");
            return false;
        }
    }

    private static IEnumerable<Attacker> DescribeAttackers(MapEvent mapEvent)
    {
        foreach (MapEventParty mapEventParty in mapEvent.AttackerSide.Parties)
        {
            MobileParty party = mapEventParty?.Party?.MobileParty;
            if (party == null) continue;

            Hero leader = party.LeaderHero;
            yield return new Attacker(
                isPlayerParty: party.IsPlayerParty(),
                hasLeader: leader != null,
                leaderWounded: leader?.IsWounded == true);
        }
    }
}
