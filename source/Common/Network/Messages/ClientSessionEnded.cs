using Common.Messaging;
using LiteNetLib;

namespace Common.Network.Messages;

/// <summary>
/// Client-local transport teardown. Unlike <see cref="PlayerDisconnected"/>, this is emitted by
/// the client connection lifecycle so client-owned authority waiters can always be released.
/// </summary>
public readonly struct ClientSessionEnded : IEvent
{
    public ClientSessionEnded(DisconnectInfo disconnectInfo) => DisconnectInfo = disconnectInfo;

    public DisconnectInfo DisconnectInfo { get; }
}
