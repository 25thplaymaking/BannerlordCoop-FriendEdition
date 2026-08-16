using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.States;
using Coop.Core.Server.Services.Time.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;

namespace Coop.Core.Server.Services.Time.Handlers;

/// <summary>
/// Handles time requests and commands the authoritative time control.
/// </summary>
public class TimeHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<TimeHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ITimeControlInterface timeControlInterface;
    private readonly IPlayerManager playerManager;
    private readonly IConnectionCollection connectionCollection;
    private readonly INetwork network;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<TimeControlEnum, NetworkTimeSpeedChangeResult> timeRoute;
    private readonly Dictionary<string, DateTime> lastRequests = new Dictionary<string, DateTime>();
    private static readonly TimeSpan RequestRateLimit = TimeSpan.FromMilliseconds(250);
    private long revision;

    public TimeHandler(IMessageBroker messageBroker, ITimeControlInterface timeControlInterface,
        IPlayerManager playerManager = null, IConnectionCollection connectionCollection = null,
        INetwork network = null, IModConfigAuthority configAuthority = null,
        IAuthorityRequestRouter authorityRequestRouter = null)
    {
        this.messageBroker = messageBroker;
        this.timeControlInterface = timeControlInterface;
        this.playerManager = playerManager;
        this.connectionCollection = connectionCollection;
        this.network = network;
        this.configAuthority = configAuthority;
        if (playerManager != null && network != null && configAuthority != null && authorityRequestRouter != null)
        {
            timeRoute = authorityRequestRouter.Register(
                AuthorityRoute<TimeControlEnum, NetworkRequestTimeSpeedChange, NetworkTimeSpeedChangeResult>.Define(
                    "time.speed.request", AuthorityRouteKind.Command, CreateHeader,
                    (mode, header) => new NetworkRequestTimeSpeedChange(mode, header), request => request.Header,
                    result => result.Header,
                    request => Enum.IsDefined(typeof(TimeControlEnum), request.NewControlMode) ? null : "time-mode-invalid",
                    request => ((int)request.NewControlMode).ToString(), ValidateHeader, ExecuteTimeRequest,
                    CreateTerminal, _ => AuthorityCommitProbeResult.Applied, _ => { }, PresentTerminal,
                    configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                    isExpectedClientResult: (_, result) => Enum.IsDefined(typeof(TimeControlEnum), result.EffectiveMode)));
        }
        this.messageBroker.Subscribe<TimeSpeedChangedAttempted>(Handle_TimeSpeedChanged);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<TimeSpeedChangedAttempted>(Handle_TimeSpeedChanged);
        timeRoute?.Dispose();
    }

    internal void Handle_TimeSpeedChanged(MessagePayload<TimeSpeedChangedAttempted> obj)
    {
        Logger.Information(
            "Local time control requested by {Source}: mode={RequestedMode}",
            obj.Who?.GetType().Name ?? "<unknown>",
            obj.What.NewControlMode);

        timeControlInterface.ServerSetTimeControl(obj.What.NewControlMode);
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
        return header.ExpectedRevision == current.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private AuthorityServerReply<NetworkTimeSpeedChangeResult> ExecuteTimeRequest(
        AuthorityServerContext context, NetworkRequestTimeSpeedChange request)
    {
        if (context.Player == null || !playerManager.IsConnected(context.Player) || !IsCampaignReady(context.Peer))
            return Reply(context.Header, timeControlInterface.GetTimeControl(), AuthorityResultStatus.Unauthorized,
                "time-requester-not-ready", false);
        if (lastRequests.TryGetValue(context.Player.ControllerId, out var last) && DateTime.UtcNow - last < RequestRateLimit)
            return Reply(context.Header, timeControlInterface.GetTimeControl(), AuthorityResultStatus.Rejected,
                "time-request-rate-limited", false);

        timeControlInterface.ServerSetTimeControl(request.NewControlMode);
        TimeControlEnum effective = timeControlInterface.GetTimeControl();
        long committedRevision = ++revision;
        var header = new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId,
            AuthorityResultStatus.Accepted, committedRevision, effective == request.NewControlMode ? null : "time-mode-clamped");
        try
        {
            network.SendAll(new NetworkTimeSpeedAuthorityState(effective, header));
            lastRequests[context.Player.ControllerId] = DateTime.UtcNow;
            return new AuthorityServerReply<NetworkTimeSpeedChangeResult>(
                new NetworkTimeSpeedChangeResult(effective, header), true);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Authority time state publication failed");
            return Reply(context.Header, effective, AuthorityResultStatus.ExecutionFailed,
                "time-state-publication-failed", false, committedRevision);
        }
    }

    private bool IsCampaignReady(LiteNetLib.NetPeer peer)
    {
        if (connectionCollection == null) return true;
        foreach (var connection in connectionCollection)
        {
            if (connection.Peer == peer) return connection.State is CampaignState;
        }
        return false;
    }

    private static AuthorityServerReply<NetworkTimeSpeedChangeResult> Reply(AuthorityRequestHeader requestHeader,
        TimeControlEnum effective, AuthorityResultStatus status, string reason, bool statePublished, long revision = 0) =>
        new(new NetworkTimeSpeedChangeResult(effective, new AuthorityResultHeader(requestHeader.SessionId,
            requestHeader.RequestId, status, revision, reason)), statePublished);

    private static NetworkTimeSpeedChangeResult CreateTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(TimeControlEnum.Pause,
        new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason));

    private static void PresentTerminal(AuthorityClientOutcome<NetworkTimeSpeedChangeResult> outcome)
    {
        if (!outcome.Applied) Logger.Warning("Time request ended without apply: {Reason}", outcome.ReasonCode);
    }
}
