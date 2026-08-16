using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.PlayerCaptivityService.Messages;
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
    private readonly IAuthorityRouteHandle<CapabilityRefreshIntent, NetworkWorkshopCapabilityQueryResult> refreshRoute;
    private readonly object gate = new();

    private WorkshopCapabilitySnapshot current;
    private ModConfigSnapshot acceptedConfig;
    private bool campaignReady;
    private DateTime? nextRefreshAttemptUtc;
    private int refreshAttempts;

    public WorkshopCapabilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry registry,
        IEnumerable<IWorkshopCapabilitySource> sources,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.configAuthority = configAuthority;
        this.registry = registry;
        this.sources = (sources ?? Enumerable.Empty<IWorkshopCapabilitySource>()).ToArray();

        refreshRoute = authorityRequestRouter.Register(
            AuthorityRoute<CapabilityRefreshIntent, NetworkRequestWorkshopCapabilities, NetworkWorkshopCapabilityQueryResult>.Define(
                "bootstrap.workshop-capabilities", AuthorityRouteKind.BootstrapQuery,
                CreateRefreshHeader,
                (_, header) => new NetworkRequestWorkshopCapabilities(header),
                request => request.Header,
                result => result.Header,
                request => request.TryValidateWireShape(out var failure) ? null : "invalid-capability-query",
                _ => "refresh",
                ValidateRefreshHeader,
                ExecuteRefresh,
                CreateRefreshTerminal,
                ProbeRefreshApplied,
                _ => { },
                PresentRefreshTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery));

        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Subscribe<NetworkWorkshopCapabilities>(HandleSnapshot);
        messageBroker.Subscribe<NetworkWorkshopCapabilityQueryResult>(HandleQueryResult);
        messageBroker.Subscribe<CampaignTick>(HandleCampaignTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Unsubscribe<NetworkWorkshopCapabilities>(HandleSnapshot);
        messageBroker.Unsubscribe<NetworkWorkshopCapabilityQueryResult>(HandleQueryResult);
        messageBroker.Unsubscribe<CampaignTick>(HandleCampaignTick);
        refreshRoute.Dispose();
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

        lock (gate)
        {
            if (!string.Equals(acceptedConfig?.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            {
                refreshAttempts = 0;
                nextRefreshAttemptUtc = null;
            }
            acceptedConfig = snapshot;
        }
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
            if (ModInformation.IsClient && registry.IsReadyFor(config.SessionId)) return;
            if (ModInformation.IsClient && registry.Readiness == WorkshopCapabilityReadiness.Loading) return;
            if (ModInformation.IsClient && nextRefreshAttemptUtc.HasValue && nextRefreshAttemptUtc > DateTime.UtcNow) return;
            nextRefreshAttemptUtc = null;
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
        registry.MarkLoading(config.SessionId);
        refreshRoute.Submit(default);
    }

    private AuthorityRequestHeader CreateRefreshHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateRefreshHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        return header.ProtocolVersion == snapshot.ProtocolVersion &&
            string.Equals(header.SessionId, snapshot.SessionId, StringComparison.Ordinal)
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
    }

    private AuthorityServerReply<NetworkWorkshopCapabilityQueryResult> ExecuteRefresh(
        AuthorityServerContext context,
        NetworkRequestWorkshopCapabilities _)
    {
        WorkshopCapabilitySnapshot snapshot;
        lock (gate) snapshot = current;
        if (snapshot == null)
            return new AuthorityServerReply<NetworkWorkshopCapabilityQueryResult>(
                new NetworkWorkshopCapabilityQueryResult(context.Header, AuthorityResultStatus.Unavailable, null,
                    "capabilities-unavailable"), false);

        return new AuthorityServerReply<NetworkWorkshopCapabilityQueryResult>(
            new NetworkWorkshopCapabilityQueryResult(context.Header, AuthorityResultStatus.Accepted, snapshot, null), true);
    }

    private static NetworkWorkshopCapabilityQueryResult CreateRefreshTerminal(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reasonCode) =>
        new NetworkWorkshopCapabilityQueryResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeRefreshApplied(NetworkWorkshopCapabilityQueryResult result) =>
        result.Snapshot != null && registry.IsReadyFor(result.Snapshot.SessionId)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;

    private void PresentRefreshTerminal(AuthorityClientOutcome<NetworkWorkshopCapabilityQueryResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        registry.MarkUnavailable();
        lock (gate)
        {
            if (refreshAttempts < 3)
            {
                refreshAttempts++;
                nextRefreshAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3 * refreshAttempts);
            }
        }
        Logger.Warning("Workshop capability refresh ended without application. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private void HandleCampaignTick(MessagePayload<CampaignTick> _)
    {
        if (!ModInformation.IsClient) return;
        refreshRoute.Poll();
        StartWhenReady();
    }

    private void HandleQueryResult(MessagePayload<NetworkWorkshopCapabilityQueryResult> payload)
    {
        if (ModInformation.IsServer || payload?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer)) return;
        if (payload.What.Status != AuthorityResultStatus.Accepted) return;

        ApplySnapshot(serverPeer, payload.What.Snapshot, nameof(HandleQueryResult));
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

        ApplySnapshot(serverPeer, payload.What.Snapshot, nameof(HandleSnapshot));
    }

    private void ApplySnapshot(NetPeer serverPeer, WorkshopCapabilitySnapshot snapshot, string context)
    {
        GameThread.RunSafe(() =>
        {
            WorkshopCapabilityApplyResult result = registry.Apply(snapshot);
            if (result == WorkshopCapabilityApplyResult.Malformed ||
                result == WorkshopCapabilityApplyResult.Conflict)
            {
                Logger.Warning("Rejected Workshop capability snapshot with result {Result}", result);
                serverPeer.Disconnect();
            }
        }, true, context);
    }

    private readonly struct CapabilityRefreshIntent
    {
    }
}
