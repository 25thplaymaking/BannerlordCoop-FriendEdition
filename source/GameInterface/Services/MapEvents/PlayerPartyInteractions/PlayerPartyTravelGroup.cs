using GameInterface.Services.MobileParties.Extensions;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

/// <summary>
/// Creates a lightweight, player-led travel group by attaching one player party to another through
/// Bannerlord's army model. A null kingdom distinguishes these voluntary groups from political armies.
/// </summary>
internal static class PlayerPartyTravelGroup
{
    public static bool CanCreate(PartyBase leaderParty, PartyBase followerParty)
    {
        var leader = leaderParty?.MobileParty;
        var follower = followerParty?.MobileParty;

        if (leader == null || follower == null || leader == follower)
            return false;
        if (!leader.IsActive || !follower.IsActive)
            return false;
        if (!leader.IsPlayerParty() || !follower.IsPlayerParty())
            return false;
        if (leader.LeaderHero == null || follower.LeaderHero == null)
            return false;
        if (leader.Army != null || follower.Army != null)
            return false;
        if (leader.AttachedTo != null || follower.AttachedTo != null)
            return false;
        if (leader.MapEvent != null || follower.MapEvent != null)
            return false;
        if (leader.BesiegerCamp != null || follower.BesiegerCamp != null)
            return false;
        if (leader.CurrentSettlement != null || follower.CurrentSettlement != null)
            return false;
        if (leader.IsCurrentlyAtSea != follower.IsCurrentlyAtSea)
            return false;

        return !AreHostile(leaderParty, followerParty);
    }

    public static bool TryCreate(PartyBase leaderParty, PartyBase followerParty, out Army army)
    {
        army = null;
        if (!CanCreate(leaderParty, followerParty))
            return false;

        var leader = leaderParty.MobileParty;
        var follower = followerParty.MobileParty;

        army = new Army(null, leader, Army.ArmyTypes.Patrolling);
        follower.Army = army;
        army.AddPartyToMergedParties(follower);
        return true;
    }

    public static bool IsTravelGroup(Army army)
        => army?.Kingdom == null &&
           army.LeaderParty?.IsPlayerParty() == true &&
           army.Parties.Count > 0;

    private static bool AreHostile(PartyBase partyA, PartyBase partyB)
    {
        var factionA = partyA?.MapFaction;
        var factionB = partyB?.MapFaction;

        if (factionA == null || factionB == null || factionA == factionB)
            return false;

        return FactionManager.IsAtWarAgainstFaction(factionA, factionB) ||
               HasFactionWar(factionA, factionB) ||
               HasFactionWar(factionB, factionA);
    }

    private static bool HasFactionWar(IFaction faction, IFaction otherFaction)
    {
        try
        {
            return faction.FactionsAtWarWith?.Contains(otherFaction) == true;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }
}
