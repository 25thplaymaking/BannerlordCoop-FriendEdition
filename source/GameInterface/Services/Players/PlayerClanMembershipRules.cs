namespace GameInterface.Services.Players;

public static class PlayerClanMembershipRules
{
    public static bool CanCreateIndependentParty(int currentParties, int partyLimit, bool emergency)
        => emergency || currentParties < partyLimit;
}
