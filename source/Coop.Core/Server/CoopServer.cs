using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Messages;
using Common.PacketHandlers;
using Common.Serialization;
using Coop.Core.Common.Network;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Session.Messages;
using Coop.Core.Server.Services.Time;
using GameInterface.Services.Entity;
using LiteNetLib;
using LiteNetLib.Utils;
using Serilog;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Coop.Core.Server;

/// <summary>
/// Server used for Coop
/// </summary>
public interface ICoopServer : INetwork, INatPunchListener, IDisposable
{
}

/// <inheritdoc cref="ICoopServer"/>
public class CoopServer : CoopNetworkBase, ICoopServer
{
    private static readonly ILogger Logger = LogManager.GetLogger<CoopServer>();

    public const string ServerControllerId = "Server";

    public override int Priority => 0;

    private readonly IMessageBroker messageBroker;
    private readonly IPacketManager packetManager;
    private readonly IMessagePacketHandler messagePacketHandler;
    private readonly IConnectionMessageQueue connectionMessageQueue;
    private readonly ServerInboundPayloadProcessor<NetPeer> inboundPayloadProcessor;
    // Buffers per-change sends and merges them into one send per key. Drained each tick in Update.
    private readonly ISendCoalescer coalescer;
    private readonly ICoalesceGate sendGate;
    // Lazy breaks the construction cycle: the manager depends on ITimeControlInterface, which depends
    // on INetwork (this server). It is only needed each Update, so deferring construction is fine.
    private readonly Lazy<IOverloadedPeerManager> overloadedPeerManager;

    // Co-hosted NAT-punch rendezvous for P2P instances (taverns etc.). The server's NetManager
    // already has NatPunchEnabled; the MissionManager answers the introduction requests.
    private readonly IMissionManager missionManager;

    public CoopServer(
        INetworkConfig configuration,
        IMessageBroker messageBroker,
        IPacketManager packetManager,
        IMessagePacketHandler messagePacketHandler,
        IConnectionMessageQueue connectionMessageQueue,
        IControllerIdProvider controllerIdProvider,
        IMissionManager missionManager,
        Lazy<IOverloadedPeerManager> overloadedPeerManager,
        ISendCoalescer coalescer,
        ICoalesceGate sendGate,
        ICommonSerializer serializer,
        CancellationTokenSource sessionCancellation) : base(configuration, serializer, sessionCancellation)
    {
        // Dependancy assignment
        this.messageBroker = messageBroker;
        this.packetManager = packetManager;
        this.messagePacketHandler = messagePacketHandler;
        this.connectionMessageQueue = connectionMessageQueue;
        inboundPayloadProcessor = new ServerInboundPayloadProcessor<NetPeer>(
            serializer,
            packetManager.HandleReceive,
            messagePacketHandler.PublishEvent,
            IsolatePeer);
        this.missionManager = missionManager;
        this.overloadedPeerManager = overloadedPeerManager;
        this.coalescer = coalescer;
        this.sendGate = sendGate;

        // Netmanager initialization
        netManager.NatPunchEnabled = true;
        netManager.NatPunchModule.Init(this);

        controllerIdProvider.SetControllerId(ServerControllerId);
    }

    public override void OnConnectionRequest(ConnectionRequest request)
    {
        string suppliedPassword;
        try
        {
            suppliedPassword = request.Data.GetString(ConnectionPassword.MaxLength);
        }
        catch (Exception)
        {
            Logger.Warning("Client connection rejected for {Endpoint}: malformed password data", request.RemoteEndPoint);
            RejectIncorrectPassword(request);
            return;
        }

        if (!ConnectionPassword.IsAccepted(Config.Token, suppliedPassword))
        {
            Logger.Warning("Client connection rejected for {Endpoint}: incorrect password", request.RemoteEndPoint);
            RejectIncorrectPassword(request);
            return;
        }

        Logger.Information("Client connection accepted for {Endpoint}", request.RemoteEndPoint);
        request.Accept();
    }

    private static void RejectIncorrectPassword(ConnectionRequest request)
    {
        var reason = new NetDataWriter();
        reason.Put((byte)ConnectionRejectCode.IncorrectPassword);
        request.Reject(reason);
    }

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        missionManager.HandleIntroductionRequest(netManager.NatPunchModule, localEndPoint, remoteEndPoint, token);
    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        // Not used on server
    }

    public override void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Logger.Warning("Network error from {EndPoint}: {SocketError}", endPoint, socketError);
    }

    public override void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {

    }

    public override void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        int payloadBytes = -1;

        try
        {
            payloadBytes = reader.AvailableBytes;
            if (!ServerInboundPayloadAdmission.IsAllowed(payloadBytes))
            {
                IsolatePeer(peer, payloadBytes, null);
                return;
            }

            inboundPayloadProcessor.Process(peer, reader.GetRemainingBytes());
        }
        catch (Exception ex)
        {
            IsolatePeer(peer, payloadBytes, ex);
        }
    }

    private static void IsolatePeer(NetPeer peer, int payloadBytes, Exception failure)
    {
        try
        {
            if (failure == null)
            {
                Logger.Warning(
                    "Disconnecting peer {PeerId}@{Address}: inbound payload size {PayloadBytes:N0} bytes is outside the allowed range of 1 to {MaximumPayloadBytes:N0} bytes",
                    peer?.Id,
                    peer?.Address,
                    payloadBytes,
                    ServerInboundPayloadAdmission.MaximumPayloadBytes);
            }
            else
            {
                Logger.Warning(
                    failure,
                    "Disconnecting peer {PeerId}@{Address}: failed to process inbound payload of {PayloadBytes:N0} bytes",
                    peer?.Id,
                    peer?.Address,
                    payloadBytes);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            peer?.Disconnect();
        }
        catch (Exception disconnectFailure)
        {
            try
            {
                Logger.Error(disconnectFailure, "Failed to disconnect an invalid inbound peer");
            }
            catch (Exception)
            {
            }
        }
    }

    public override void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        Logger.Warning("Received unconnected message from {EndPoint}", remoteEndPoint);
    }

    public override void OnPeerConnected(NetPeer peer)
    {
        PlayerConnected message = new PlayerConnected(peer);
        messageBroker.Publish(this, message);
    }

    public override void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        PlayerDisconnected message = new PlayerDisconnected(peer, disconnectInfo);
        messageBroker.Publish(this, message);
    }

    public override void Update(TimeSpan frameTime)
    {
        overloadedPeerManager.Value.CheckForOverloadedPeers();

        netManager.PollEvents();
        netManager.NatPunchModule.PollEvents();

        // Drain this tick's coalesced sends. Update runs on the poll thread, but creates and destroys
        // send on the game thread, so we marshal the flush there too: that keeps each merged SendAll
        // ordered behind an object's create and ahead of its destroy, and off netPeer.Send's non-thread-safe
        // path. Inert until a send path enqueues, so the guard avoids queueing an empty flush every tick.
        if (coalescer.HasPending)
        {
            // The gate holds updates about parties no player is near; held keys keep merging, so what
            // eventually goes out is still the correct end state. See ReplicationRelevanceGate.
            GameThread.RunSafe(() => coalescer.Flush(this, sendGate));
        }

        // Send any sub-budget aggregated messages so nothing waits longer than one poll interval.
        FlushPendingMessages();
    }

    public override void Start()
    {
        Logger.Information("Server starting on port {Port}", Config.Port);

        if (netManager.Start(IPAddress.Any, IPAddress.IPv6Any, Config.Port))
        {
            StartNetworkPoller();
            messageBroker.Publish(this, new ServerListening());
            return;
        }

        Logger.Error("Server failed to bind port {Port}; it may already be in use", Config.Port);
    }

    public override void SendAll(IPacket packet)
    {
        SendAll(netManager, packet);
    }

    public override void SendAllBut(NetPeer ignoredPeer, IPacket packet)
    {
        SendAllBut(netManager, ignoredPeer, packet);
    }

    // Every per-peer send funnels through here, so a still-loading peer's world deltas are dropped
    // (pre-save) or held (loading) instead of sent live — broadcasts and direct sends alike. The queue
    // replays the held ones during the join barriers. Connection-level traffic that must always reach a
    // mid-join peer (the save, the join handshake) uses SendImmediate to bypass this.
    public override void Send(NetPeer netPeer, IPacket packet)
    {
        if (connectionMessageQueue.TryHandleBroadcast(netPeer, packet)) return;
        base.Send(netPeer, packet);
    }
}
