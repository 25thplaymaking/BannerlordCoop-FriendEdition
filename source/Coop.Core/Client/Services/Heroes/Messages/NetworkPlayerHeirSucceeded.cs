using Common.Messaging;
using GameInterface.Services.Players.Data;
using ProtoBuf;

namespace Coop.Core.Client.Services.Heroes.Messages;

/// <summary>
/// Sent only to the owning peer after their hero died with an eligible heir: carries the
/// replacement registration so the client switches control into the successor. Every other
/// client (and this one) rebinds the registration through
/// <see cref="NetworkPlayerRegistrationUpdated"/>, which the server broadcasts first.
/// </summary>
[ProtoContract]
public readonly struct NetworkPlayerHeirSucceeded : IEvent
{
    [ProtoMember(1)]
    public readonly Player Player;

    [ProtoMember(2)]
    public readonly string DeadHeroName;

    public NetworkPlayerHeirSucceeded(Player player, string deadHeroName)
    {
        Player = player;
        DeadHeroName = deadHeroName;
    }
}
