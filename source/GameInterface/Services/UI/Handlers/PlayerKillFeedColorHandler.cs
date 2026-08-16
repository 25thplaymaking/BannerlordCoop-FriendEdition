using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.UI.CoopOptions;
using GameInterface.Services.UI.CoopOptions.Providers.KillFeedTab;
using GameInterface.Services.UI.Messages;
using LiteNetLib;
using Serilog;
using System.Linq;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.UI.Handlers;

public class PlayerKillFeedColorHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerKillFeedColorHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IPlayerKillFeedColorService colorService;
    private readonly ICoopOptionsStore optionsStore;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<PlayerKillFeedColor, NetworkKillFeedColorResult> colorRoute;
    private readonly Dictionary<string, DateTime> lastAcceptedColorChange = new Dictionary<string, DateTime>();
    private readonly Dictionary<long, NetworkUpdateKillFeedColor> correlatedUpdates = new Dictionary<long, NetworkUpdateKillFeedColor>();
    private long colorRevision;
    private static readonly TimeSpan ChangeRateLimit = TimeSpan.FromMilliseconds(250);

    public PlayerKillFeedColorHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IPlayerManager playerManager,
        IPlayerKillFeedColorService colorService,
        ICoopOptionsStore optionsStore,
        IControllerIdProvider controllerIdProvider,
        IModConfigAuthority configAuthority = null,
        IAuthorityRequestRouter authorityRequestRouter = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.playerManager = playerManager;
        this.colorService = colorService;
        this.optionsStore = optionsStore;
        this.controllerIdProvider = controllerIdProvider;
        this.configAuthority = configAuthority;

        if (configAuthority != null && authorityRequestRouter != null)
        {
            colorRoute = authorityRequestRouter.Register(
                AuthorityRoute<PlayerKillFeedColor, NetworkRequestKillFeedColor, NetworkKillFeedColorResult>.Define(
                    "preference.killfeed-color", AuthorityRouteKind.Command, CreateHeader,
                    (color, header) => new NetworkRequestKillFeedColor(color.Red, color.Green, color.Blue, header),
                    request => request.Header, result => result.Header,
                    request => PlayerKillFeedColor.TryCreate(request.Red, request.Green, request.Blue, out _)
                        ? null : "killfeed-color-invalid",
                    request => request.Red + ":" + request.Green + ":" + request.Blue,
                    ValidateHeader, ExecuteColorChange, CreateTerminal, ProbeColorCommit, RequestColorResync,
                    PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                    isExpectedClientResult: (request, result) =>
                        result.Header.Status != AuthorityResultStatus.Accepted ||
                        (request.Red == result.Red && request.Green == result.Green && request.Blue == result.Blue)));
        }

        messageBroker.Subscribe<PlayerKillFeedColorSelected>(Handle_PlayerKillFeedColorSelected);
        messageBroker.Subscribe<PlayerKillFeedColorResendRequested>(Handle_PlayerKillFeedColorResendRequested);
        if (colorRoute == null)
            messageBroker.Subscribe<NetworkRequestKillFeedColor>(Handle_NetworkRequestKillFeedColor);
        messageBroker.Subscribe<NetworkUpdateKillFeedColor>(Handle_NetworkUpdateKillFeedColor);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerKillFeedColorSelected>(Handle_PlayerKillFeedColorSelected);
        messageBroker.Unsubscribe<PlayerKillFeedColorResendRequested>(Handle_PlayerKillFeedColorResendRequested);
        if (colorRoute == null)
            messageBroker.Unsubscribe<NetworkRequestKillFeedColor>(Handle_NetworkRequestKillFeedColor);
        messageBroker.Unsubscribe<NetworkUpdateKillFeedColor>(Handle_NetworkUpdateKillFeedColor);
        colorRoute?.Dispose();
    }

    private void Handle_PlayerKillFeedColorSelected(MessagePayload<PlayerKillFeedColorSelected> payload)
    {
        if (ModInformation.IsServer) return;

        var color = payload.What.Color;

        CacheLocalColor(color);
        if (colorRoute != null) colorRoute.Submit(color);
        else network.SendAll(new NetworkRequestKillFeedColor(color.Red, color.Green, color.Blue));
    }

    private void Handle_PlayerKillFeedColorResendRequested(MessagePayload<PlayerKillFeedColorResendRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!optionsStore.TryLoad(out var options)) return;
        if (!KillFeedOptionsTabProvider.TryGetKillFeedColor(options, out var color)) return;

        CacheLocalColor(color);
        if (colorRoute != null) colorRoute.Submit(color);
        else network.SendAll(new NetworkRequestKillFeedColor(color.Red, color.Green, color.Blue));
    }

    private void Handle_NetworkRequestKillFeedColor(MessagePayload<NetworkRequestKillFeedColor> payload)
    {
        if (ModInformation.IsClient) return;

        var request = payload.What;
        if (!PlayerKillFeedColor.TryCreate(request.Red, request.Green, request.Blue, out var color))
        {
            Logger.Warning("Ignoring invalid kill-feed color request: {Red}, {Green}, {Blue}",
                request.Red, request.Green, request.Blue);
            return;
        }

        if (payload.Who is not NetPeer peer)
        {
            Logger.Warning("Ignoring kill-feed color request without a network peer");
            return;
        }

        if (!playerManager.TryGetPlayer(peer, out var player))
        {
            Logger.Warning("Ignoring kill-feed color request from an unregistered peer");
            return;
        }

        foreach (var knownColor in colorService.GetColors().Where(kvp => kvp.Key != player.ControllerId))
        {
            network.Send(peer, new NetworkUpdateKillFeedColor(
                knownColor.Key,
                knownColor.Value.Red,
                knownColor.Value.Green,
                knownColor.Value.Blue));
        }

        colorService.SetColor(player.ControllerId, color);
        network.SendAll(new NetworkUpdateKillFeedColor(player.ControllerId, color.Red, color.Green, color.Blue));
    }

    private void Handle_NetworkUpdateKillFeedColor(MessagePayload<NetworkUpdateKillFeedColor> payload)
    {
        if (ModInformation.IsServer) return;

        var update = payload.What;
        if (string.IsNullOrEmpty(update.ControllerId)) return;
        if (!PlayerKillFeedColor.TryCreate(update.Red, update.Green, update.Blue, out var color)) return;

        colorService.SetColor(update.ControllerId, color);
        if (update.Header.RequestId > 0 && !string.IsNullOrEmpty(update.Header.SessionId))
            correlatedUpdates[update.Header.RequestId] = update;
    }

    private void CacheLocalColor(PlayerKillFeedColor color)
    {
        if (string.IsNullOrEmpty(controllerIdProvider.ControllerId)) return;

        colorService.SetColor(controllerIdProvider.ControllerId, color);
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (header.SessionId != current.SessionId)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private AuthorityServerReply<NetworkKillFeedColorResult> ExecuteColorChange(
        AuthorityServerContext context, NetworkRequestKillFeedColor request)
    {
        if (!PlayerKillFeedColor.TryCreate(request.Red, request.Green, request.Blue, out var color))
            return Reply(context.Header, context.Player?.ControllerId, request, AuthorityResultStatus.InvalidRequest,
                "killfeed-color-invalid", 0, false);
        if (context.Player == null || string.IsNullOrWhiteSpace(context.Player.ControllerId) ||
            !playerManager.IsConnected(context.Player))
            return Reply(context.Header, null, request, AuthorityResultStatus.Unauthorized,
                "player-not-connected", 0, false);
        if (lastAcceptedColorChange.TryGetValue(context.Player.ControllerId, out var lastChange) &&
            DateTime.UtcNow - lastChange < ChangeRateLimit)
            return Reply(context.Header, context.Player.ControllerId, request, AuthorityResultStatus.Rejected,
                "killfeed-color-rate-limited", 0, false);

        long revision = ++colorRevision;
        var stateHeader = new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId,
            AuthorityResultStatus.Accepted, revision, null);
        try
        {
            colorService.SetColor(context.Player.ControllerId, color);
            network.SendAll(new NetworkUpdateKillFeedColor(context.Player.ControllerId, color.Red, color.Green, color.Blue,
                stateHeader));
            lastAcceptedColorChange[context.Player.ControllerId] = DateTime.UtcNow;
            return new AuthorityServerReply<NetworkKillFeedColorResult>(
                new NetworkKillFeedColorResult(context.Player.ControllerId, color.Red, color.Green, color.Blue, stateHeader), true);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Kill-feed colour publication failed; requester will resync");
            return Reply(context.Header, context.Player.ControllerId, request, AuthorityResultStatus.ExecutionFailed,
                "killfeed-color-publication-failed", revision, false);
        }
    }

    private static AuthorityServerReply<NetworkKillFeedColorResult> Reply(AuthorityRequestHeader header,
        string controllerId, NetworkRequestKillFeedColor request, AuthorityResultStatus status, string reason,
        long revision, bool statePublished) =>
        new(new NetworkKillFeedColorResult(controllerId, request.Red, request.Green, request.Blue,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, revision, reason)), statePublished);

    private static NetworkKillFeedColorResult CreateTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) =>
        new(null, 0, 0, 0, new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason));

    private AuthorityCommitProbeResult ProbeColorCommit(NetworkKillFeedColorResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        if (!correlatedUpdates.TryGetValue(result.Header.RequestId, out var update)) return AuthorityCommitProbeResult.Pending;
        return update.Header.SessionId == result.Header.SessionId &&
               update.Header.RequestId == result.Header.RequestId &&
               update.Header.CommittedRevision == result.Header.CommittedRevision &&
               update.ControllerId == result.ControllerId && update.Red == result.Red && update.Green == result.Green &&
               update.Blue == result.Blue
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Invalid;
    }

    private void RequestColorResync(NetworkKillFeedColorResult _)
    {
        // The colour is cosmetic. A failed replica apply asks the normal join/state resend path to
        // republish it; it deliberately does not disconnect a gameplay peer.
        messageBroker.Publish(this, new PlayerKillFeedColorResendRequested());
    }

    private static void PresentTerminalOutcome(AuthorityClientOutcome<NetworkKillFeedColorResult> outcome)
    {
        if (!outcome.Applied)
            Logger.Warning("Kill-feed colour update did not apply: {Reason}", outcome.ReasonCode);
    }
}
