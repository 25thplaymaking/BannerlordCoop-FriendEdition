using Common.Messaging;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Heroes.Messages;

/// <summary>
/// Published on the server after a registered co-op player hero's death fully applied.
/// The succession gate in <c>KillCharacterActionPatches</c> only lets such a death through when
/// <see cref="Successor"/> was resolved, so the server can hand the controller to the heir.
/// </summary>
public class PlayerHeroDied : IEvent
{
    public Hero Victim { get; }
    public string VictimName { get; }
    public Hero Successor { get; }
    public string ControllerId { get; }

    public PlayerHeroDied(Hero victim, Hero successor, string controllerId)
    {
        Victim = victim;
        VictimName = victim?.Name?.ToString();
        Successor = successor;
        ControllerId = controllerId;
    }
}
