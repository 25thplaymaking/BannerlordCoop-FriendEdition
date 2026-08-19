using GameInterface.Services.MapEvents;
using System;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Covers the rule that decides a hideout by the hero rather than by the roster.
/// </summary>
/// <remarks>
/// A hideout is settled by the attacking hero in the base game: go down and your men drag you out, the
/// hideout stands. Co-op lost that and paid players for dying, because
/// <c>MapEvent.CheckIfOneSideHasLost</c> counts surviving members and a downed hero's escort still has
/// bodies.
/// <para>
/// The decision is kept pure so it can be pinned without a campaign — which matters, because the thing
/// it controls is who won a battle, and getting it wrong in the other direction would take a victory
/// away from someone who earned it.
/// </para>
/// </remarks>
public class HideoutHeroOutcomeTests
{
    private static HideoutHeroOutcome.Attacker PlayerLed(bool wounded) =>
        new HideoutHeroOutcome.Attacker(isPlayerParty: true, hasLeader: true, leaderWounded: wounded);

    private static HideoutHeroOutcome.Attacker AiLed(bool wounded) =>
        new HideoutHeroOutcome.Attacker(isPlayerParty: false, hasLeader: true, leaderWounded: wounded);

    private static HideoutHeroOutcome.Attacker Leaderless(bool isPlayerParty) =>
        new HideoutHeroOutcome.Attacker(isPlayerParty, hasLeader: false, leaderWounded: false);

    private static bool Decide(params HideoutHeroOutcome.Attacker[] attackers) =>
        HideoutHeroOutcome.DefendersWin(isHideoutBattle: true, defendersAlreadyWon: false, attackers);

    [Fact]
    public void ADownedRaidingHeroLosesTheHideout()
    {
        // The whole point: enter, go down, and the hideout stands.
        Assert.True(Decide(PlayerLed(wounded: true)));
    }

    [Fact]
    public void AHealthyRaidingHeroKeepsTheirVictory()
    {
        Assert.False(Decide(PlayerLed(wounded: false)));
    }

    [Fact]
    public void OrdinaryBattlesAreLeftAlone()
    {
        // Every other battle is settled by troops, which is correct for it. Applying the hero rule
        // generally would take field battles away from wounded lords who won them.
        Assert.False(HideoutHeroOutcome.DefendersWin(
            isHideoutBattle: false, defendersAlreadyWon: false, new[] { PlayerLed(wounded: true) }));
    }

    [Fact]
    public void AnAiRaidIsNotJudgedByItsLeadersHealth()
    {
        // An AI hideout raid is resolved by simulation; no hero stood in a mission to go down, and a
        // lord who happened to arrive wounded should not hand the bandits a win.
        Assert.False(Decide(AiLed(wounded: true)));
    }

    [Fact]
    public void OneDownedPlayerDecidesItEvenAlongsideAHealthyOne()
    {
        // Co-op specific: the base game only ever has one hero in a hideout. If a raid is shared and
        // anyone leading it goes down, the raid failed.
        Assert.True(Decide(PlayerLed(wounded: false), PlayerLed(wounded: true)));
    }

    [Fact]
    public void ALeaderlessPartyCannotDecideIt()
    {
        Assert.False(Decide(Leaderless(isPlayerParty: true)));
    }

    [Fact]
    public void AnAlreadyLostHideoutIsNotForcedAgain()
    {
        // Re-forcing a winner would re-enter the result path for no reason.
        Assert.False(HideoutHeroOutcome.DefendersWin(
            isHideoutBattle: true, defendersAlreadyWon: true, new[] { PlayerLed(wounded: true) }));
    }

    [Fact]
    public void NoAttackersDecidesNothing()
    {
        Assert.False(Decide(Array.Empty<HideoutHeroOutcome.Attacker>()));
        Assert.False(HideoutHeroOutcome.DefendersWin(
            isHideoutBattle: true, defendersAlreadyWon: false, attackers: null));
    }

    [Fact]
    public void TheDecisionStopsAtTheFirstDownedHero()
    {
        // The sequence is lazily built from live map-event parties, so the rule must not depend on
        // draining it — a throwing tail would otherwise take the battle resolution down.
        IEnumerable<HideoutHeroOutcome.Attacker> ThrowsAfterFirst()
        {
            yield return PlayerLed(wounded: true);
            throw new InvalidOperationException("the rule should have stopped by here");
        }

        Assert.True(HideoutHeroOutcome.DefendersWin(
            isHideoutBattle: true, defendersAlreadyWon: false, ThrowsAfterFirst()));
    }
}
