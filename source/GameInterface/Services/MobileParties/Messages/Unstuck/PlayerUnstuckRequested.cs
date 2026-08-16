using Common.Messaging;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Messages.Unstuck;

/// <summary>
/// The local player asked to be forced out of stuck map states (coop.debug.mobileparty.unstuck).
/// The handler fails closed until a server-verifiable stuck-state proof exists.
/// </summary>
internal readonly struct PlayerUnstuckRequested : IEvent
{
    public MobileParty Party { get; }

    public PlayerUnstuckRequested(MobileParty party)
    {
        Party = party;
    }
}
