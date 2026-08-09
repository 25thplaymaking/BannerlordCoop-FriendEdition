using Common.Messaging;
using Common.PacketHandlers;
using Common.Serialization;
using System;
using System.IO;

namespace Coop.Core.Server;

/// <summary>
/// Deserializes and dispatches one admitted inbound payload without allowing a bad peer's
/// payload or handler failure to escape into the shared LiteNetLib polling loop.
/// </summary>
internal sealed class ServerInboundPayloadProcessor<TPeer>
{
    private readonly ICommonSerializer serializer;
    private readonly Action<TPeer, IPacket> packetHandler;
    private readonly Action<TPeer, IMessage> messageHandler;
    private readonly Action<TPeer, int, Exception> isolatePeer;

    internal ServerInboundPayloadProcessor(
        ICommonSerializer serializer,
        Action<TPeer, IPacket> packetHandler,
        Action<TPeer, IMessage> messageHandler,
        Action<TPeer, int, Exception> isolatePeer)
    {
        this.serializer = serializer;
        this.packetHandler = packetHandler;
        this.messageHandler = messageHandler;
        this.isolatePeer = isolatePeer;
    }

    internal bool Process(TPeer peer, byte[] payload)
    {
        try
        {
            object received = serializer.Deserialize(payload);

            if (received is IPacket packet)
            {
                packetHandler(peer, packet);
            }
            else if (received is IMessage message)
            {
                messageHandler(peer, message);
            }
            else
            {
                throw new InvalidDataException(
                    $"Inbound payload deserialized to neither {nameof(IPacket)} nor {nameof(IMessage)}: " +
                    $"{received?.GetType().FullName ?? "null"}");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Isolation is part of this boundary. A secondary logging/disconnect failure must not
            // resurrect the original peer-controlled exception on the shared network poller.
            try
            {
                isolatePeer(peer, payload?.Length ?? -1, ex);
            }
            catch (Exception)
            {
            }

            return false;
        }
    }
}
