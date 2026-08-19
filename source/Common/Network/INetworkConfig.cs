using LiteNetLib;
using System;
using System.Net;

namespace Common.Network;

public interface INetworkConfig
{
    string Address { get; }
    int Port { get; }
    string Token { get; }
    string P2PToken { get; }
    /// <summary>
    /// True when the session is reached through a local tunnel pump (Steam P2P) instead of
    /// a direct address. Tunneled peers cannot NAT-punch each other, so mission traffic
    /// stays on the server relay.
    /// </summary>
    bool IsTunneled { get; }
    TimeSpan ConnectionTimeout { get; }
    int MaxPacketsInQueue { get; }
    int ResumePacketsInQueue { get; }
    TimeSpan AuditTimeout { get; }
    TimeSpan ObjectCreationTimeout { get; }

    /// <summary>
    /// How long a client may take to confirm it applied an authoritative decision that requires
    /// ENTERING A MISSION, as opposed to merely creating an object.
    /// </summary>
    /// <remarks>
    /// Mission entry loads a scene: terrain, agents, voice banks, coop battle behaviours and the
    /// P2P battle instance. That is seconds of work before the client can possibly confirm, and it
    /// scales with the map a conversion ships. Budgeting it with <see cref="ObjectCreationTimeout"/>
    /// made every battle entry a race against a 5s deadline that the load itself nearly exhausted —
    /// measured live at 5618 ms for a load that started 4 s earlier. Losing that race trips
    /// failClosedOnApplyFailure, which cancels the co-op session, disposes the container and leaves
    /// every Harmony patch unable to resolve ISyncPolicy: the client dies in a log flood.
    /// </remarks>
    TimeSpan MissionEntryTimeout { get; }
    TimeSpan NetworkPollInterval { get; }
    IPAddress LanAddress { get; }
    int LanPort { get; }
    IPAddress WanAddress { get; }
    int WanPort { get; }
    TimeSpan PingInterval { get; }
    TimeSpan ReconnectDelay { get; }
    TimeSpan DisconnectTimeout { get; }
    /// <summary>
    /// LiteNetLib's internal logic-thread cycle (NetManager.UpdateTime): resends, packet merging and
    /// reliable-window advances happen at this cadence.
    /// </summary>
    TimeSpan UpdateTime { get; }
    NatAddressType NATType { get; }
}
