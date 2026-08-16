using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GameInterface.Services.AuthorityRequests;

/// <summary>Registers typed authority routes. It deliberately has no arbitrary RPC entrypoint.</summary>
public interface IAuthorityRequestRouter : IGameAbstraction, IUpdateable, IDisposable
{
    IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
        AuthorityRoute<TIntent, TRequest, TResult> route)
        where TRequest : IMessage
        where TResult : IMessage;

    bool IsRegistered(string routeId, AuthorityRouteKind kind);
}

public sealed class AuthorityRequestRouter : IAuthorityRequestRouter
{
    private static readonly ILogger Logger = LogManager.GetLogger<AuthorityRequestRouter>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly int replayLedgerCapacityPerPeer;
    // The router owns each route subscription for precisely its DI lifetime. Keeping the
    // registration interface here also lets an individually disposed route stop being polled.
    private readonly List<IAuthorityRegistration> registrations = new List<IAuthorityRegistration>();
    private long nextRequestId;
    private bool disposed;

    public AuthorityRequestRouter(
        IMessageBroker messageBroker,
        INetwork network,
        IPlayerManager playerManager,
        int replayLedgerCapacityPerPeer = 256)
    {
        this.messageBroker = messageBroker ?? throw new ArgumentNullException(nameof(messageBroker));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.playerManager = playerManager ?? throw new ArgumentNullException(nameof(playerManager));
        if (replayLedgerCapacityPerPeer < 1)
            throw new ArgumentOutOfRangeException(nameof(replayLedgerCapacityPerPeer));
        this.replayLedgerCapacityPerPeer = replayLedgerCapacityPerPeer;
    }

    public IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
        AuthorityRoute<TIntent, TRequest, TResult> route)
        where TRequest : IMessage
        where TResult : IMessage
    {
        if (route == null) throw new ArgumentNullException(nameof(route));
        if (disposed) throw new ObjectDisposedException(nameof(AuthorityRequestRouter));

        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(TRequest), typeof(AuthorityRouteAttribute));
        if (attribute == null || !string.Equals(attribute.RouteId, route.RouteId, StringComparison.Ordinal) ||
            attribute.Kind != route.Kind)
        {
            throw new InvalidOperationException(
                $"Authority route {route.RouteId} must match {typeof(TRequest).Name}'s AuthorityRouteAttribute.");
        }

        if (registrations.Any(registration =>
                string.Equals(registration.RouteId, route.RouteId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Authority route {route.RouteId} is already registered.");

        RouteRegistration<TIntent, TRequest, TResult> registration = null;
        registration = new RouteRegistration<TIntent, TRequest, TResult>(
            route,
            messageBroker,
            network,
            playerManager,
            NextRequestId,
            replayLedgerCapacityPerPeer,
            () => registrations.Remove(registration));
        registrations.Add(registration);
        return registration;
    }

    public bool IsRegistered(string routeId, AuthorityRouteKind kind) =>
        !string.IsNullOrWhiteSpace(routeId) && !disposed && registrations
            .Any(registration => string.Equals(registration.RouteId, routeId, StringComparison.Ordinal) &&
                                 registration.Kind == kind);

    public int Priority => UpdatePriority.MainLoop.GameThread - 1;

    public void Update(TimeSpan frameTime)
    {
        if (disposed || ModInformation.IsServer) return;
        foreach (var registration in registrations.ToArray())
            registration.Poll();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (var registration in registrations.ToArray()) registration.Dispose();
        registrations.Clear();
    }

    private long NextRequestId()
    {
        long id = Interlocked.Increment(ref nextRequestId);
        if (id > 0) return id;

        Interlocked.Exchange(ref nextRequestId, 0);
        return Interlocked.Increment(ref nextRequestId);
    }

    private interface IAuthorityRegistration : IDisposable
    {
        string RouteId { get; }
        AuthorityRouteKind Kind { get; }
        void Poll();
    }

    private sealed class RouteRegistration<TIntent, TRequest, TResult> :
        IAuthorityRouteHandle<TIntent, TResult>, IAuthorityRegistration
        where TRequest : IMessage
        where TResult : IMessage
    {
        private readonly AuthorityRoute<TIntent, TRequest, TResult> route;
        private readonly IMessageBroker messageBroker;
        private readonly INetwork network;
        private readonly IPlayerManager playerManager;
        private readonly Func<long> nextRequestId;
        private readonly Action unregister;
        private readonly AuthorityRequestLifecycle lifecycle;
        private readonly AuthorityReplayLedger<TResult> replayLedger;
        private readonly Dictionary<long, PendingRequest> pending = new Dictionary<long, PendingRequest>();
        private readonly object sync = new object();
        private readonly Action<MessagePayload<TRequest>> requestHandler;
        private readonly Action<MessagePayload<TResult>> resultHandler;
        private readonly Action<MessagePayload<PlayerDisconnected>> disconnectHandler;
        private readonly Action<MessagePayload<ClientSessionEnded>> clientSessionEndedHandler;
        private bool disposed;

        public RouteRegistration(
            AuthorityRoute<TIntent, TRequest, TResult> route,
            IMessageBroker messageBroker,
            INetwork network,
            IPlayerManager playerManager,
            Func<long> nextRequestId,
            int replayLedgerCapacityPerPeer,
            Action unregister)
        {
            this.route = route;
            this.messageBroker = messageBroker;
            this.network = network;
            this.playerManager = playerManager;
            this.nextRequestId = nextRequestId;
            this.unregister = unregister;
            replayLedger = new AuthorityReplayLedger<TResult>(replayLedgerCapacityPerPeer);
            lifecycle = new AuthorityRequestLifecycle(route.RouteId, route.Kind == AuthorityRouteKind.Command, Logger);

            requestHandler = HandleRequest;
            resultHandler = HandleResult;
            disconnectHandler = HandlePlayerDisconnected;
            clientSessionEndedHandler = HandleClientSessionEnded;
            messageBroker.Subscribe(requestHandler);
            messageBroker.Subscribe(resultHandler);
            messageBroker.Subscribe(disconnectHandler);
            messageBroker.Subscribe(clientSessionEndedHandler);
        }

        public string RouteId => route.RouteId;
        public AuthorityRouteKind Kind => route.Kind;
        public AuthorityRequestLifecycle Lifecycle => lifecycle;

        public AuthorityRequestTicket<TResult> Submit(
            TIntent intent,
            Action<AuthorityClientOutcome<TResult>> completion = null)
        {
            if (disposed) throw new ObjectDisposedException(route.RouteId);
            if (ModInformation.IsServer)
                throw new InvalidOperationException($"Authority route {route.RouteId} cannot submit a client request on the server.");

            long requestId = nextRequestId();
            AuthorityRequestHeader header = route.CreateHeader(requestId);
            var ticket = new AuthorityRequestTicket<TResult>(requestId);
            bool validHeader = header.TryValidate(out var failure);
            if (header.RequestId != requestId || !validHeader)
            {
                Complete(ticket, completion, new AuthorityClientOutcome<TResult>(
                    AuthorityClientCompletion.Rejected,
                    default,
                    "invalid-local-header"));
                Logger.Error("Authority route {Route} refused to submit malformed local header. Failure={Failure}",
                    route.RouteId, failure);
                return ticket;
            }

            TRequest request = route.BuildRequest(intent, header);
            var pendingRequest = new PendingRequest(ticket, request, header, completion, DateTime.UtcNow + route.TimeoutPolicy.ResponseTimeout);
            lock (sync) pending.Add(requestId, pendingRequest);
            lifecycle.BeginClient(requestId.ToString());

            try
            {
                lifecycle.ClientSent(requestId.ToString());
                network.SendAll(request);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Authority route {Route} could not send request {RequestId}", route.RouteId, requestId);
                lifecycle.ClientTimedOut(requestId.ToString(), "send-failed");
                CompletePending(pendingRequest, AuthorityClientCompletion.TimedOut, default, "send-failed");
            }

            return ticket;
        }

        public AuthorityClientOutcome<TResult> SubmitBlocking(TIntent intent)
        {
            var ticket = Submit(intent);
            DateTime deadline = DateTime.UtcNow + route.TimeoutPolicy.ResponseTimeout +
                route.TimeoutPolicy.ApplyTimeout + route.TimeoutPolicy.ResponseTimeout;
            bool completed = GameThread.WaitWhilePumping(() =>
            {
                Poll();
                return ticket.IsCompleted;
            }, deadline);

            if (!completed && !ticket.IsCompleted)
            {
                lock (sync)
                {
                    if (pending.TryGetValue(ticket.RequestId, out var request))
                    {
                        lifecycle.ClientTimedOut(ticket.RequestId.ToString(), "blocking-deadline");
                        CompletePending(request, AuthorityClientCompletion.TimedOut, default, "blocking-deadline");
                    }
                }
            }

            return ticket.Outcome;
        }

        public void Poll()
        {
            if (disposed || ModInformation.IsServer) return;

            PendingRequest[] snapshot;
            lock (sync) snapshot = pending.Values.ToArray();
            DateTime now = DateTime.UtcNow;
            foreach (var request in snapshot)
            {
                if (request.Ticket.IsCompleted) continue;

                if (request.AcceptedResultReceived)
                {
                    AuthorityCommitProbeResult probe;
                    try
                    {
                        // A result that does not describe the submitted feature intent cannot be
                        // repaired by applying a generic resync: it is not evidence for this
                        // request. Complete the ticket before any feature commit probe can run.
                        if (!route.IsExpectedClientResult(request.Request, request.Result))
                        {
                            lifecycle.ReplicaApplyFailed(request.Header.RequestId.ToString(), "invalid-replica");
                            CompletePending(request, AuthorityClientCompletion.ReplicaApplyFailed, request.Result,
                                "invalid-replica");
                            continue;
                        }

                        probe = route.ProbeClientCommit(request.Result);
                    }
                    catch (Exception exception)
                    {
                        Logger.Error(exception, "Authority commit probe failed. Route={Route} RequestId={RequestId}",
                            route.RouteId, request.Header.RequestId);
                        lifecycle.ReplicaApplyFailed(request.Header.RequestId.ToString(), "commit-probe-failed");
                        CompletePending(request, AuthorityClientCompletion.ReplicaApplyFailed, request.Result,
                            "commit-probe-failed");
                        continue;
                    }
                    if (probe == AuthorityCommitProbeResult.Applied)
                    {
                        lifecycle.ReplicaApplied(request.Header.RequestId.ToString(), "applied");
                        CompletePending(request, AuthorityClientCompletion.Applied, request.Result, null);
                    }
                    else if (probe == AuthorityCommitProbeResult.Invalid || now >= request.ApplyDeadline)
                    {
                        if (!route.FailClosedOnApplyFailure && !request.ResyncRequested)
                        {
                            request.ResyncRequested = true;
                            request.ApplyDeadline = now + route.TimeoutPolicy.ApplyTimeout;
                            route.RequestResync(request.Result);
                        }
                        else
                        {
                            lifecycle.ReplicaApplyFailed(request.Header.RequestId.ToString(),
                                probe == AuthorityCommitProbeResult.Invalid ? "invalid-replica" : "apply-timeout");
                            CompletePending(request, AuthorityClientCompletion.ReplicaApplyFailed, request.Result,
                                probe == AuthorityCommitProbeResult.Invalid ? "invalid-replica" : "apply-timeout");
                        }
                    }

                    continue;
                }

                if (now < request.ResponseDeadline) continue;

                if (request.Attempts < route.TimeoutPolicy.RetryCount)
                {
                    request.Attempts++;
                    request.ResponseDeadline = now + Backoff(route.TimeoutPolicy.ResponseTimeout, request.Attempts);
                    try
                    {
                        network.SendAll(request.Request);
                        Logger.Warning("Retrying authority request. Route={Route} RequestId={RequestId} Attempt={Attempt}",
                            route.RouteId, request.Header.RequestId, request.Attempts);
                    }
                    catch (Exception exception)
                    {
                        Logger.Error(exception, "Authority retry send failed. Route={Route} RequestId={RequestId}",
                            route.RouteId, request.Header.RequestId);
                    }
                }
                else
                {
                    lifecycle.ClientTimedOut(request.Header.RequestId.ToString(), "response-timeout");
                    CompletePending(request, AuthorityClientCompletion.TimedOut, default, "response-timeout");
                }
            }
        }

        public void CancelAll(string reasonCode)
        {
            PendingRequest[] snapshot;
            lock (sync) snapshot = pending.Values.ToArray();
            foreach (var request in snapshot)
            {
                lifecycle.ClientCancelled(request.Header.RequestId.ToString(), reasonCode);
                CompletePending(request, AuthorityClientCompletion.Cancelled, default, reasonCode);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelAll("route-disposed");
            messageBroker.Unsubscribe(requestHandler);
            messageBroker.Unsubscribe(resultHandler);
            messageBroker.Unsubscribe(disconnectHandler);
            messageBroker.Unsubscribe(clientSessionEndedHandler);
            replayLedger.Clear();
            unregister?.Invoke();
        }

        private void HandlePlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
        {
            if (payload.What?.PlayerId == null) return;

            replayLedger.Clear(payload.What.PlayerId);
            if (ModInformation.IsClient)
                CancelAll("network-disconnected");
        }

        private void HandleClientSessionEnded(MessagePayload<ClientSessionEnded> _)
        {
            if (!disposed && ModInformation.IsClient)
                CancelAll("client-session-ended");
        }

        private void HandleResult(MessagePayload<TResult> payload)
        {
            if (disposed || ModInformation.IsServer || !(payload.Who is NetPeer) ||
                !route.IsTrustedResultSource(payload.Who)) return;

            AuthorityResultHeader resultHeader = route.ReadResultHeader(payload.What);
            if (!resultHeader.TryValidate(out var failure))
            {
                Logger.Warning("Ignoring malformed authority result. Route={Route} Failure={Failure}", route.RouteId, failure);
                return;
            }

            PendingRequest request;
            lock (sync)
            {
                if (!pending.TryGetValue(resultHeader.RequestId, out request))
                {
                    Logger.Warning("Ignoring late or mismatched authority result. Route={Route} RequestId={RequestId}",
                        route.RouteId, resultHeader.RequestId);
                    return;
                }

                if (!string.Equals(request.Header.SessionId, resultHeader.SessionId, StringComparison.Ordinal))
                {
                    Logger.Warning("Authoritative session changed while request was pending. Route={Route} RequestId={RequestId}",
                        route.RouteId, resultHeader.RequestId);
                    CancelAll("session-replaced");
                    return;
                }

                request.Result = payload.What;
                request.ResultHeader = resultHeader;
                request.ServerPeer = (NetPeer)payload.Who;
            }

            lifecycle.ClientReplyReceived(resultHeader.RequestId.ToString(), resultHeader.Status.ToString());
            if (resultHeader.Status != AuthorityResultStatus.Accepted)
            {
                lifecycle.ClientRejected(resultHeader.RequestId.ToString(), resultHeader.ReasonCode);
                CompletePending(request, AuthorityClientCompletion.Rejected, payload.What, resultHeader.ReasonCode);
                return;
            }

            request.AcceptedResultReceived = true;
            request.ApplyDeadline = DateTime.UtcNow + route.TimeoutPolicy.ApplyTimeout;
        }

        private void HandleRequest(MessagePayload<TRequest> payload)
        {
            if (disposed || ModInformation.IsClient) return;
            if (!(payload.Who is NetPeer peer))
            {
                Logger.Error("Authority route {Route} received a request without a transport peer.", route.RouteId);
                return;
            }

            AuthorityRequestHeader header = route.ReadRequestHeader(payload.What);
            lifecycle.BeginServer(header.RequestId.ToString());
            if (!header.TryValidate(out var failure))
            {
                SendTerminal(peer, header, route.CreateTerminalResult(header, AuthorityResultStatus.InvalidRequest, failure));
                return;
            }

            AuthorityHeaderValidation headerValidation = route.ValidateHeader(header);
            if (!headerValidation.IsValid)
            {
                SendTerminal(peer, header, route.CreateTerminalResult(header, headerValidation.Status, headerValidation.ReasonCode));
                return;
            }

            string wireFailure = route.ValidateWireShape(payload.What);
            if (!string.IsNullOrEmpty(wireFailure))
            {
                SendTerminal(peer, header, route.CreateTerminalResult(header, AuthorityResultStatus.InvalidRequest, wireFailure));
                return;
            }

            Player player = null;
            if (route.RequireAuthenticatedPlayer && !playerManager.TryGetPlayer(peer, out player))
            {
                SendTerminal(peer, header, route.CreateTerminalResult(header, AuthorityResultStatus.Unauthorized, "peer-not-player"));
                return;
            }

            string commandKey = route.BuildCommandKey(payload.What);
            var replay = replayLedger.Inspect(peer, header.SessionId, route.RouteId, header.RequestId, commandKey);
            if (replay.Decision == AuthorityReplayDecision.Conflict)
            {
                Logger.Warning("Disconnecting peer after conflicting authority replay. Route={Route} RequestId={RequestId}",
                    route.RouteId, header.RequestId);
                peer.Disconnect();
                return;
            }
            if (replay.Decision == AuthorityReplayDecision.Completed)
            {
                if (!replay.SuppressReply) SendCached(peer, header, replay.Result);
                return;
            }
            if (replay.Decision == AuthorityReplayDecision.InFlight) return;
            if (replay.Decision == AuthorityReplayDecision.OverCapacity)
            {
                SendTerminal(peer, header, route.CreateTerminalResult(header, AuthorityResultStatus.Unavailable,
                    "authority-overloaded"));
                return;
            }

            lifecycle.ServerAdmitted(header.RequestId.ToString());
            try
            {
                GameThread.Run(() => ExecuteServer(peer, player, header, payload.What), blocking: true,
                    label: $"AuthorityRequest.{route.RouteId}");
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Authority route {Route} could not execute request {RequestId}", route.RouteId, header.RequestId);
                lifecycle.ExecutionFailed(header.RequestId.ToString(), "game-thread-failure");
                SendTerminal(peer, header, route.CreateTerminalResult(header, AuthorityResultStatus.ExecutionFailed, "game-thread-failure"));
            }
        }

        private void ExecuteServer(NetPeer peer, Player player, AuthorityRequestHeader header, TRequest request)
        {
            TResult result;
            bool suppressReply = false;
            try
            {
                var reply = route.Execute(new AuthorityServerContext(peer, player, header, route.RouteId), request);
                result = reply.Result;
                suppressReply = reply.SuppressReply;
                AuthorityResultHeader resultHeader = route.ReadResultHeader(result);
                if (!resultHeader.TryValidate(out var failure) || resultHeader.RequestId != header.RequestId ||
                    !string.Equals(resultHeader.SessionId, header.SessionId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Feature returned an invalid authority result: " + failure);

                if (resultHeader.Status == AuthorityResultStatus.Accepted && !reply.StatePublished)
                {
                    lifecycle.PublicationFailed(header.RequestId.ToString(), "accepted-without-publication");
                    result = route.CreateTerminalResult(header, AuthorityResultStatus.ExecutionFailed, "publication-failed");
                }
                else if (resultHeader.Status == AuthorityResultStatus.Accepted && route.Kind == AuthorityRouteKind.Command)
                {
                    lifecycle.MutationCommitted(header.RequestId.ToString(), "accepted");
                    lifecycle.StatePublished(header.RequestId.ToString(), resultHeader.CommittedRevision.ToString());
                }
                else
                {
                    if (resultHeader.Status != AuthorityResultStatus.Accepted)
                        lifecycle.ServerRejected(header.RequestId.ToString(), resultHeader.ReasonCode);
                }
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Authority route {Route} threw before it could resolve request {RequestId}",
                    route.RouteId, header.RequestId);
                lifecycle.ExecutionFailed(header.RequestId.ToString(), "executor-threw");
                result = route.CreateTerminalResult(header, AuthorityResultStatus.ExecutionFailed, "executor-threw");
            }

            replayLedger.Complete(peer, header.SessionId, route.RouteId, header.RequestId, result, suppressReply);
            if (!suppressReply) SendCached(peer, header, result);
        }

        private void SendTerminal(NetPeer peer, AuthorityRequestHeader header, TResult result)
        {
            AuthorityResultHeader resultHeader = route.ReadResultHeader(result);
            lifecycle.ServerRejected(header.RequestId.ToString(), resultHeader.ReasonCode);
            replayLedger.Complete(peer, header.SessionId, route.RouteId, header.RequestId, result);
            SendCached(peer, header, result);
        }

        private void SendCached(NetPeer peer, AuthorityRequestHeader header, TResult result)
        {
            try
            {
                network.Send(peer, result);
                lifecycle.ReplySent(header.RequestId.ToString(), route.ReadResultHeader(result).Status.ToString());
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Authority route {Route} could not send result {RequestId}", route.RouteId, header.RequestId);
                lifecycle.ReplySendFailed(header.RequestId.ToString(), "send-failed");
            }
        }

        private void CompletePending(
            PendingRequest request,
            AuthorityClientCompletion completion,
            TResult result,
            string reasonCode)
        {
            lock (sync) pending.Remove(request.Header.RequestId);
            var outcome = Complete(request.Ticket, request.Completion,
                new AuthorityClientOutcome<TResult>(completion, result, reasonCode));
            if (outcome.Completion == AuthorityClientCompletion.ReplicaApplyFailed)
                request.ServerPeer?.Disconnect();
        }

        private AuthorityClientOutcome<TResult> Complete(
            AuthorityRequestTicket<TResult> ticket,
            Action<AuthorityClientOutcome<TResult>> completion,
            AuthorityClientOutcome<TResult> outcome)
        {
            if (ticket.IsCompleted) return ticket.Outcome;

            bool accepted = outcome.Completion == AuthorityClientCompletion.Applied;
            if (!TryRunClientCallback(() => route.PresentTerminalOutcome(outcome), "presentation", ticket.RequestId) && accepted)
            {
                lifecycle.ReplicaApplyFailed(ticket.RequestId.ToString(), "presentation-failed");
                outcome = new AuthorityClientOutcome<TResult>(AuthorityClientCompletion.ReplicaApplyFailed,
                    outcome.Result, "presentation-failed");
            }

            ticket.Outcome = outcome;
            ticket.IsCompleted = true;
            if (outcome.Completion == AuthorityClientCompletion.Applied)
                lifecycle.ClientCompleted(ticket.RequestId.ToString(), "accepted");

            TryRunClientCallback(() => completion?.Invoke(outcome), "completion", ticket.RequestId);
            return outcome;
        }

        private bool TryRunClientCallback(Action callback, string callbackKind, long requestId)
        {
            if (callback == null) return true;

            bool succeeded = true;
            try
            {
                Action guarded = () =>
                {
                    try { callback(); }
                    catch (Exception exception)
                    {
                        succeeded = false;
                        Logger.Error(exception,
                            "Authority {CallbackKind} callback failed. Route={Route} RequestId={RequestId}",
                            callbackKind, route.RouteId, requestId);
                    }
                };

                // Unit fixtures do not boot the engine loop. Production callbacks are always
                // marshalled; before the loop exists there is no client UI to mutate.
                if (GameThread.Instance.IsInitialized)
                    GameThread.Run(guarded, blocking: true, label: $"AuthorityRequest.{route.RouteId}.{callbackKind}");
                else
                    guarded();
            }
            catch (Exception exception)
            {
                succeeded = false;
                Logger.Error(exception, "Authority {CallbackKind} callback could not run on the game thread. Route={Route} RequestId={RequestId}",
                    callbackKind, route.RouteId, requestId);
            }

            return succeeded;
        }

        private static TimeSpan Backoff(TimeSpan baseTimeout, int attempt) =>
            TimeSpan.FromTicks(baseTimeout.Ticks * Math.Max(1, attempt + 1));

        private sealed class PendingRequest
        {
            public PendingRequest(
                AuthorityRequestTicket<TResult> ticket,
                TRequest request,
                AuthorityRequestHeader header,
                Action<AuthorityClientOutcome<TResult>> completion,
                DateTime responseDeadline)
            {
                Ticket = ticket;
                Request = request;
                Header = header;
                Completion = completion;
                ResponseDeadline = responseDeadline;
            }

            public AuthorityRequestTicket<TResult> Ticket { get; }
            public TRequest Request { get; }
            public AuthorityRequestHeader Header { get; }
            public Action<AuthorityClientOutcome<TResult>> Completion { get; }
            public int Attempts { get; set; }
            public DateTime ResponseDeadline { get; set; }
            public DateTime ApplyDeadline { get; set; }
            public bool AcceptedResultReceived { get; set; }
            public bool ResyncRequested { get; set; }
            public TResult Result { get; set; }
            public AuthorityResultHeader ResultHeader { get; set; }
            public NetPeer ServerPeer { get; set; }
        }
    }
}
