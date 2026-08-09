using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GameInterface.Services.CampaignService.Handlers;

internal class LoadModConfigHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<LoadModConfigHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IModConfig modConfig;
    private readonly IModConfigAuthority configAuthority;
    private readonly ITimeControlInterface timeControlInterface;
    private readonly IPlayerManager playerManager;
    private readonly ModConfigRequestGate<NetPeer> requestGate = new();

    public LoadModConfigHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IModConfig modConfig,
        IModConfigAuthority configAuthority,
        ITimeControlInterface timeControlInterface,
        IPlayerManager playerManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.modConfig = modConfig;
        this.configAuthority = configAuthority;
        this.timeControlInterface = timeControlInterface;
        this.playerManager = playerManager;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<NetworkRequestServerModConfig>(Handle_NetworkRequestServerModConfig);
        messageBroker.Subscribe<NetworkLoadModConfig>(Handle_NetworkLoadModConfig);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<NetworkRequestServerModConfig>(Handle_NetworkRequestServerModConfig);
        messageBroker.Unsubscribe<NetworkLoadModConfig>(Handle_NetworkLoadModConfig);
    }

    internal void Handle_CampaignReady(MessagePayload<CampaignReady> obj)
    {
        // Use server's config
        if (ModInformation.IsClient)
        {
            if (!configAuthority.TryGetCurrent(out ModConfigSnapshot accepted))
            {
                Logger.Fatal("Client entered CampaignReady without accepting the host mod-config handshake barrier");
                throw new InvalidOperationException(
                    "The client cannot enter the campaign without an accepted host mod-config snapshot.");
            }

            ApplyConfigs();
            messageBroker.Publish(this, new HostModConfigAccepted(accepted));
            network.SendAll(new NetworkRequestServerModConfig(accepted));
            return;
        }

        if (!ModInformation.IsServer) return;

        // InitializeHost validates the raw resolved schema, requires the explicit Friend Edition
        // Birth & Death invariant, and atomically installs ModOptions before this event is published.
        ModConfigSnapshot snapshot = configAuthority.InitializeHost(modConfig.Data);
        ApplyConfigs();

        string sourcePath = modConfig is ModConfig fileConfig
            ? fileConfig.ResolvedPath ?? "<unresolved CoopData/mod-config.json>"
            : "<in-memory mod-config>";
        Logger.Information(
            "Authoritative host mod-config accepted from {Path}: protocol={Protocol}, session={Session}, " +
            "revision={Revision}, sha256={Sha256}, difficulty.birthAndDeath={BirthAndDeath}",
            sourcePath,
            snapshot.ProtocolVersion,
            snapshot.SessionId,
            snapshot.Revision,
            snapshot.Sha256,
            snapshot.BirthAndDeathEnabled);

        messageBroker.Publish(this, new HostModConfigAccepted(snapshot));
        network.SendAll(new NetworkLoadModConfig(snapshot));
    }

    private void Handle_NetworkRequestServerModConfig(MessagePayload<NetworkRequestServerModConfig> obj)
    {
        if (!ModInformation.IsServer || obj?.Who is not NetPeer peer) return;

        // Initial configuration is delivered by the module-validation response. Requests are only
        // valid after the peer has an authenticated controller mapping and are bounded per peer.
        if (playerManager == null || !playerManager.TryGetPlayer(peer, out _))
        {
            Logger.Warning("Disconnecting unmapped peer {Peer} that requested the host mod-config", peer.Id);
            peer.Disconnect();
            return;
        }
        if (!obj.What.TryValidateWireShape(out string requestFailure))
        {
            Logger.Warning("Disconnecting peer {Peer} after malformed mod-config request: {Failure}",
                peer.Id, requestFailure);
            peer.Disconnect();
            return;
        }
        if (!requestGate.TryAccept(peer))
        {
            Logger.Warning("Rate-limited repeated mod-config request from peer {Peer}", peer.Id);
            return;
        }

        GameThread.RunSafe(() =>
        {
            if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot))
            {
                Logger.Fatal("Disconnecting peer {Peer}: authoritative host mod-config is unavailable", peer.Id);
                peer.Disconnect();
                return;
            }
            network.Send(peer, new NetworkLoadModConfig(snapshot));
        }, true, nameof(Handle_NetworkRequestServerModConfig));
    }

    private void Handle_NetworkLoadModConfig(MessagePayload<NetworkLoadModConfig> obj)
    {
        // Servers never consume wire configuration. Clients reject local/null broker publication;
        // the initial module/config barrier pins the authoritative remote peer before any late resync.
        if (ModInformation.IsServer)
        {
            if (obj?.Who is NetPeer inboundPeer)
            {
                Logger.Warning(
                    "Disconnecting peer {Peer} that attempted to overwrite the authoritative host mod-config",
                    inboundPeer.Id);
                inboundPeer.Disconnect();
            }
            else
            {
                Logger.Warning("Rejected locally published NetworkLoadModConfig on the server");
            }
            return;
        }
        if (obj?.Who is not NetPeer serverPeer)
        {
            Logger.Warning("Rejected locally published NetworkLoadModConfig on the client");
            return;
        }
        if (!configAuthority.IsTrustedServer(serverPeer))
        {
            Logger.Fatal("Rejected host mod-config from a transport peer not pinned by the initial handshake");
            serverPeer.Disconnect();
            return;
        }

        GameThread.RunSafe(() =>
        {
            ModConfigAcceptanceResult result = configAuthority.AcceptClientSnapshot(obj.What.Snapshot);
            if (!result.Succeeded)
            {
                Logger.Fatal(
                    "Rejected host mod-config snapshot ({Status}): {Reason}",
                    result.Status,
                    result.Reason);
                serverPeer.Disconnect();
                return;
            }

            if (result.Status == ModConfigAcceptanceStatus.Accepted)
            {
                Logger.Information(
                    "Accepted host mod-config resync: session={Session}, revision={Revision}, " +
                    "sha256={Sha256}, difficulty.birthAndDeath={BirthAndDeath}",
                    obj.What.Snapshot.SessionId,
                    obj.What.Snapshot.Revision,
                    obj.What.Snapshot.Sha256,
                    obj.What.Snapshot.BirthAndDeathEnabled);
            }
            messageBroker.Publish(this, new HostModConfigAccepted(obj.What.Snapshot));
        }, true, nameof(Handle_NetworkLoadModConfig));
    }

    private void ApplyConfigs()
    {
        if (!ModConfigProvider.ModOptions.FastForwardEnabled)
        {
            timeControlInterface.AddFastForwardPolicy(() => false);
        }
    }
}

/// <summary>Bounded one-request-per-second gate; one noisy peer cannot starve another.</summary>
internal sealed class ModConfigRequestGate<TPeer> where TPeer : class
{
    private const int MaximumPeers = 64;
    private readonly object gate = new();
    private readonly Dictionary<TPeer, long> accepted = new();
    private readonly Func<long> timestamp;
    private readonly long minimumInterval;

    internal ModConfigRequestGate(Func<long> timestamp = null, long minimumInterval = 0)
    {
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.minimumInterval = minimumInterval > 0 ? minimumInterval : Stopwatch.Frequency;
    }

    internal bool TryAccept(TPeer peer)
    {
        if (peer == null) return false;
        lock (gate)
        {
            long now = timestamp();
            if (accepted.TryGetValue(peer, out long previous) &&
                now >= previous && now - previous < minimumInterval)
                return false;

            if (!accepted.ContainsKey(peer) && accepted.Count >= MaximumPeers)
            {
                TPeer oldest = null;
                long oldestTime = long.MaxValue;
                foreach (var candidate in accepted)
                {
                    if (candidate.Value >= oldestTime) continue;
                    oldest = candidate.Key;
                    oldestTime = candidate.Value;
                }
                if (oldest != null) accepted.Remove(oldest);
            }

            accepted[peer] = now;
            return true;
        }
    }
}
