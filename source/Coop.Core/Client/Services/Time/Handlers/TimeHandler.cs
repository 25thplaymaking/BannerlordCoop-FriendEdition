using Common.Logging;
using Common;
using Common.Messaging;
using Common.Network;
using Coop.Core.Server.Services.Time.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.MapEvents;
using Serilog;
using System;
using System.Collections.Generic;

namespace Coop.Core.Client.Services.Time.Handlers
{
    /// <summary>
    /// Handles time control for the client
    /// </summary>
    public class TimeHandler : IHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger<TimeHandler>();

        private readonly IMessageBroker messageBroker;
        private readonly ITimeControlInterface timeControlInterface;
        private readonly IModConfigAuthority configAuthority;
        private readonly IAuthorityRouteHandle<TimeControlEnum, NetworkTimeSpeedChangeResult> timeRoute;
        private readonly Dictionary<long, NetworkTimeSpeedAuthorityState> correlatedStates = new Dictionary<long, NetworkTimeSpeedAuthorityState>();
        private MapEventFastForwardState mapEventState = MapEventFastForwardState.NotBlocked;

        public TimeHandler(IMessageBroker messageBroker, INetwork network, ITimeControlInterface timeControlInterface,
            IModConfigAuthority configAuthority = null, IAuthorityRequestRouter authorityRequestRouter = null)
        {
            this.messageBroker = messageBroker;
            this.timeControlInterface = timeControlInterface;
            this.configAuthority = configAuthority;
            if (configAuthority != null && authorityRequestRouter != null)
            {
                timeRoute = authorityRequestRouter.Register(
                    AuthorityRoute<TimeControlEnum, NetworkRequestTimeSpeedChange, NetworkTimeSpeedChangeResult>.Define(
                        "time.speed.request", AuthorityRouteKind.Command, CreateHeader,
                        (mode, header) => new NetworkRequestTimeSpeedChange(mode, header), request => request.Header,
                        result => result.Header,
                        request => Enum.IsDefined(typeof(TimeControlEnum), request.NewControlMode) ? null : "time-mode-invalid",
                        request => ((int)request.NewControlMode).ToString(), ValidateHeader, (_, _) =>
                            throw new InvalidOperationException("Client cannot execute a time authority request."),
                        CreateTerminal, ProbeCommit, RequestResync, PresentTerminal, configAuthority.IsTrustedServer,
                        AuthorityTimeoutPolicy.CampaignMutation,
                        isExpectedClientResult: (_, result) => Enum.IsDefined(typeof(TimeControlEnum), result.EffectiveMode)));
            }
            messageBroker.Subscribe<TimeSpeedChangedAttempted>(Handle_TimeSpeedChanged);
            messageBroker.Subscribe<NetworkChangeTimeControlMode>(Handle_NetworkTimeSpeedChanged);
            messageBroker.Subscribe<NetworkTimeSpeedAuthorityState>(Handle_AuthorityTimeState);
            messageBroker.Subscribe<NetworkMapEventLockChanged>(Handle_NetworkMapEventLockChanged);
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<TimeSpeedChangedAttempted>(Handle_TimeSpeedChanged);
            messageBroker.Unsubscribe<NetworkChangeTimeControlMode>(Handle_NetworkTimeSpeedChanged);
            messageBroker.Unsubscribe<NetworkTimeSpeedAuthorityState>(Handle_AuthorityTimeState);
            messageBroker.Unsubscribe<NetworkMapEventLockChanged>(Handle_NetworkMapEventLockChanged);
            timeRoute?.Dispose();
        }

        internal void Handle_TimeSpeedChanged(MessagePayload<TimeSpeedChangedAttempted> obj)
        {
            var newMode = obj.What.NewControlMode;

            if (mapEventState.IsBlocked && newMode == TimeControlEnum.Play_2x)
            {
                messageBroker.Publish(this, new SendInformationMessage(mapEventState.BlockedMessage));
                return;
            }

            Logger.Verbose("Client changing time to {mode} from server", newMode);

            if (timeRoute != null) timeRoute.Submit(newMode);
            else Logger.Warning("Time speed request ignored because the authority route is unavailable");
        }

        internal void Handle_NetworkMapEventLockChanged(MessagePayload<NetworkMapEventLockChanged> obj)
        {
            var wasBlocked = mapEventState.IsBlocked;
            mapEventState = MapEventFastForwardState.FromNetworkMessage(obj.What);

            if (mapEventState.IsBlocked && !wasBlocked)
            {
                messageBroker.Publish(this, new SendInformationMessage(MapEventTimeControlMessages.FastForwardDisabled));
            }
            else if (!mapEventState.IsBlocked && wasBlocked)
            {
                messageBroker.Publish(this, new SendInformationMessage(MapEventTimeControlMessages.FastForwardEnabled));
            }
        }

        internal void Handle_NetworkTimeSpeedChanged(MessagePayload<NetworkChangeTimeControlMode> obj)
        {
            var newMode = obj.What.NewControlMode;

            Logger.Verbose("Client requesting time change to {mode}", newMode);

            timeControlInterface.ClientSetTimeControl(newMode);
        }

        private void Handle_AuthorityTimeState(MessagePayload<NetworkTimeSpeedAuthorityState> obj)
        {
            if (ModInformation.IsServer) return;
            var state = obj.What;
            if (!Enum.IsDefined(typeof(TimeControlEnum), state.EffectiveMode)) return;
            timeControlInterface.ClientSetTimeControl(state.EffectiveMode);
            if (state.Header.RequestId > 0 && !string.IsNullOrEmpty(state.Header.SessionId))
                correlatedStates[state.Header.RequestId] = state;
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

        private static NetworkTimeSpeedChangeResult CreateTerminal(AuthorityRequestHeader header,
            AuthorityResultStatus status, string reason) => new(TimeControlEnum.Pause,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, 0, reason));

        private AuthorityCommitProbeResult ProbeCommit(NetworkTimeSpeedChangeResult result)
        {
            if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
            if (!correlatedStates.TryGetValue(result.Header.RequestId, out var state)) return AuthorityCommitProbeResult.Pending;
            return state.Header.SessionId == result.Header.SessionId &&
                   state.Header.RequestId == result.Header.RequestId &&
                   state.Header.CommittedRevision == result.Header.CommittedRevision &&
                   state.EffectiveMode == result.EffectiveMode
                ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Invalid;
        }

        private static void RequestResync(NetworkTimeSpeedChangeResult result)
        {
            // The subsequent normal server time broadcast is authoritative; no client ever
            // republishes a mode to peers while recovering this cosmetic/UI replica state.
            Logger.Warning("Time replica did not commit for request {RequestId}", result.Header.RequestId);
        }

        private void PresentTerminal(AuthorityClientOutcome<NetworkTimeSpeedChangeResult> outcome)
        {
            if (outcome.Applied) return;
            // A reject or policy clamp must leave the local UI at the server's current mode,
            // never at the user intent that was just denied.
            timeControlInterface.ClientSetTimeControl(timeControlInterface.GetTimeControl());
            Logger.Warning("Time speed request did not apply: {Reason}", outcome.ReasonCode);
        }

        private readonly struct MapEventFastForwardState
        {
            public static MapEventFastForwardState NotBlocked => new MapEventFastForwardState(0);

            public int PlayersInMapEvent { get; }
            public bool IsBlocked => PlayersInMapEvent > 0;
            public string BlockedMessage => MapEventTimeControlMessages.FastForwardBlocked(PlayersInMapEvent);

            private MapEventFastForwardState(int playersInMapEvent)
            {
                PlayersInMapEvent = playersInMapEvent;
            }

            public static MapEventFastForwardState FromNetworkMessage(NetworkMapEventLockChanged message)
            {
                return new MapEventFastForwardState(message.PlayersInMapEvent);
            }
        }
    }
}
