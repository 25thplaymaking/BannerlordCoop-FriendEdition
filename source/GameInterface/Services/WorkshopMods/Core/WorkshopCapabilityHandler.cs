using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

internal sealed class WorkshopCapabilityHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<WorkshopCapabilityHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry registry;
    private readonly IWorkshopCapabilitySource[] sources;
    private readonly IPlayerManager playerManager;
    private readonly ModConfigRequestGate<NetPeer> requestGate = new();
    private readonly object gate = new();

    private WorkshopCapabilitySnapshot current;
    private ModConfigSnapshot acceptedConfig;
    private bool campaignReady;
    private bool clientRequested;

    public WorkshopCapabilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry registry,
        IEnumerable<IWorkshopCapabilitySource> sources,
        IPlayerManager playerManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.configAuthority = configAuthority;
        this.registry = registry;
        this.sources = (sources ?? Enumerable.Empty<IWorkshopCapabilitySource>()).ToArray();
        this.playerManager = playerManager;

        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Subscribe<NetworkRequestWorkshopCapabilities>(HandleRequest);
        messageBroker.Subscribe<NetworkWorkshopCapabilities>(HandleSnapshot);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Unsubscribe<NetworkRequestWorkshopCapabilities>(HandleRequest);
        messageBroker.Unsubscribe<NetworkWorkshopCapabilities>(HandleSnapshot);
    }

    internal void HandleCampaignReady(MessagePayload<CampaignReady> _)
    {
        lock (gate) campaignReady = true;
        StartWhenReady();
    }

    internal void HandleHostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        ModConfigSnapshot snapshot = payload?.What.Snapshot;
        if (!configAuthority.IsCurrent(snapshot)) return;

        lock (gate) acceptedConfig = snapshot;
        StartWhenReady();
    }

    private void StartWhenReady()
    {
        ModConfigSnapshot config;
        lock (gate)
        {
            if (!campaignReady || acceptedConfig == null) return;
            config = acceptedConfig;
            if (ModInformation.IsServer && current != null) return;
            if (ModInformation.IsClient && clientRequested) return;
        }

        if (ModInformation.IsServer)
        {
            WorkshopCapability[] capabilities = sources
                .SelectMany(source => source.CaptureCapabilities() ?? Enumerable.Empty<WorkshopCapability>())
                .ToArray();
            var snapshot = new WorkshopCapabilitySnapshot(config.SessionId, revision: 0, capabilities);
            WorkshopCapabilityApplyResult applied = registry.Apply(snapshot);
            if (applied != WorkshopCapabilityApplyResult.Applied &&
                applied != WorkshopCapabilityApplyResult.AlreadyCurrent)
                throw new InvalidOperationException($"The server capability snapshot could not be committed: {applied}.");

            lock (gate)
            {
                if (current != null) return;
                current = snapshot;
            }
            network.SendAll(new NetworkWorkshopCapabilities(snapshot));
            return;
        }

        if (!ModInformation.IsClient) return;
        lock (gate) clientRequested = true;
        network.SendAll(new NetworkRequestWorkshopCapabilities(
            new WorkshopCapabilitySnapshot(config.SessionId, revision: 0, Array.Empty<WorkshopCapability>())));
    }

    private void HandleRequest(MessagePayload<NetworkRequestWorkshopCapabilities> payload)
    {
        if (!ModInformation.IsServer || payload?.Who is not NetPeer peer) return;
        if (playerManager == null || !playerManager.TryGetPlayer(peer, out _))
        {
            Logger.Warning("Ignoring Workshop capability request from unmapped peer {Peer}", peer.Id);
            return;
        }
        if (!payload.What.TryValidateWireShape(out string failure))
        {
            Logger.Warning("Disconnecting peer {Peer} after malformed Workshop capability request: {Failure}", peer.Id, failure);
            peer.Disconnect();
            return;
        }

        WorkshopCapabilitySnapshot snapshot;
        lock (gate) snapshot = current;
        if (snapshot == null) return;
        if (!string.Equals(payload.What.SessionId, snapshot.SessionId, StringComparison.Ordinal))
        {
            Logger.Warning("Disconnecting peer {Peer} after conflicting Workshop capability session request", peer.Id);
            peer.Disconnect();
            return;
        }
        if (!requestGate.TryAccept(peer)) return;

        GameThread.RunSafe(
            () => network.Send(peer, new NetworkWorkshopCapabilities(snapshot)),
            true,
            nameof(HandleRequest));
    }

    private void HandleSnapshot(MessagePayload<NetworkWorkshopCapabilities> payload)
    {
        if (ModInformation.IsServer)
        {
            if (payload?.Who is NetPeer peer) peer.Disconnect();
            return;
        }
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer)
        {
            Logger.Warning("Rejected locally published Workshop capability snapshot");
            return;
        }
        if (!configAuthority.IsTrustedServer(serverPeer) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            !string.Equals(config.SessionId, payload.What.Snapshot?.SessionId, StringComparison.Ordinal))
        {
            Logger.Warning("Rejected Workshop capability snapshot from an untrusted or conflicting server transport");
            serverPeer.Disconnect();
            return;
        }

        GameThread.RunSafe(() =>
        {
            WorkshopCapabilityApplyResult result = registry.Apply(payload.What.Snapshot);
            if (result == WorkshopCapabilityApplyResult.Malformed ||
                result == WorkshopCapabilityApplyResult.Conflict)
            {
                Logger.Warning("Rejected Workshop capability snapshot with result {Result}", result);
                serverPeer.Disconnect();
            }
        }, true, nameof(HandleSnapshot));
    }
}
