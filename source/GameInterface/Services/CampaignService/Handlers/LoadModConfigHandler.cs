using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.PlayerCaptivityService.Messages;
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
    private readonly IAuthorityRouteHandle<ModConfigRefreshIntent, NetworkModConfigQueryResult> refreshRoute;

    public LoadModConfigHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IModConfig modConfig,
        IModConfigAuthority configAuthority,
        ITimeControlInterface timeControlInterface,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.modConfig = modConfig;
        this.configAuthority = configAuthority;
        this.timeControlInterface = timeControlInterface;

        refreshRoute = authorityRequestRouter.Register(
            AuthorityRoute<ModConfigRefreshIntent, NetworkRequestServerModConfig, NetworkModConfigQueryResult>.Define(
                "bootstrap.mod-config", AuthorityRouteKind.BootstrapQuery,
                CreateRefreshHeader,
                (_, header) => new NetworkRequestServerModConfig(header),
                request => request.Header,
                result => result.Header,
                request => request.TryValidateWireShape(out var failure) ? null : "invalid-config-query",
                _ => "refresh",
                ValidateRefreshHeader,
                ExecuteRefresh,
                CreateRefreshTerminal,
                ProbeRefreshApplied,
                _ => { },
                PresentRefreshTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery));
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<NetworkLoadModConfig>(Handle_NetworkLoadModConfig);
        messageBroker.Subscribe<NetworkModConfigQueryResult>(Handle_NetworkModConfigQueryResult);
        messageBroker.Subscribe<CampaignTick>(HandleCampaignTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<NetworkLoadModConfig>(Handle_NetworkLoadModConfig);
        messageBroker.Unsubscribe<NetworkModConfigQueryResult>(Handle_NetworkModConfigQueryResult);
        messageBroker.Unsubscribe<CampaignTick>(HandleCampaignTick);
        refreshRoute.Dispose();
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
            refreshRoute.Submit(default);
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

    private AuthorityRequestHeader CreateRefreshHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private string ValidateRefreshHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return "config-unavailable";
        return header.ProtocolVersion == snapshot.ProtocolVersion &&
            string.Equals(header.SessionId, snapshot.SessionId, StringComparison.Ordinal)
            ? null
            : "stale-config-session";
    }

    private AuthorityServerReply<NetworkModConfigQueryResult> ExecuteRefresh(
        AuthorityServerContext context,
        NetworkRequestServerModConfig _)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot))
            return new AuthorityServerReply<NetworkModConfigQueryResult>(
                new NetworkModConfigQueryResult(context.Header, AuthorityResultStatus.Unavailable, null, "config-unavailable"), false);

        return new AuthorityServerReply<NetworkModConfigQueryResult>(
            new NetworkModConfigQueryResult(context.Header, AuthorityResultStatus.Accepted, snapshot, null), true);
    }

    private static NetworkModConfigQueryResult CreateRefreshTerminal(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reasonCode) =>
        new NetworkModConfigQueryResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeRefreshApplied(NetworkModConfigQueryResult result) =>
        result.Snapshot != null && configAuthority.IsCurrent(result.Snapshot)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;

    private void PresentRefreshTerminal(AuthorityClientOutcome<NetworkModConfigQueryResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Mod-config refresh ended without application. Completion={Completion} Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }

    private void HandleCampaignTick(MessagePayload<CampaignTick> _)
    {
        if (ModInformation.IsClient) refreshRoute.Poll();
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

        AcceptSnapshot(serverPeer, obj.What.Snapshot, nameof(Handle_NetworkLoadModConfig));
    }

    private void Handle_NetworkModConfigQueryResult(MessagePayload<NetworkModConfigQueryResult> obj)
    {
        if (ModInformation.IsServer || obj?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer)) return;
        if (obj.What.Status == AuthorityResultStatus.Accepted)
            AcceptSnapshot(serverPeer, obj.What.Snapshot, nameof(Handle_NetworkModConfigQueryResult));
    }

    private void AcceptSnapshot(NetPeer serverPeer, ModConfigSnapshot snapshot, string context)
    {
        GameThread.RunSafe(() =>
        {
            ModConfigAcceptanceResult result = configAuthority.AcceptClientSnapshot(snapshot);
            if (!result.Succeeded)
            {
                Logger.Fatal("Rejected host mod-config snapshot ({Status}): {Reason}", result.Status, result.Reason);
                serverPeer.Disconnect();
                return;
            }
            messageBroker.Publish(this, new HostModConfigAccepted(snapshot));
        }, true, context);
    }

    private void ApplyConfigs()
    {
        if (!ModConfigProvider.ModOptions.FastForwardEnabled)
        {
            timeControlInterface.AddFastForwardPolicy(() => false);
        }
    }

    private readonly struct ModConfigRefreshIntent
    {
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
