using Common.Messaging;
using GameInterface.Services.Players.Data;

namespace GameInterface.Services.Players.Messages;

public readonly struct PlayerRegistrationChanged : IEvent
{
    public readonly Player Player;

    public PlayerRegistrationChanged(Player player)
    {
        Player = player;
    }
}
