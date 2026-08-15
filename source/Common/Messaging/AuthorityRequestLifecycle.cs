using Serilog;
using System;
using System.Collections.Concurrent;

namespace Common.Messaging;

/// <summary>
/// The observable stages of a client-originated, server-authoritative request.
/// Each runtime records its local portion of the exchange; the shared request id
/// correlates the client and server log entries.
/// </summary>
public enum AuthorityRequestPhase
{
    ClientRequested,
    ClientSent,
    ServerReceived,
    ServerValidated,
    ServerResolved,
    ClientReplyReceived,
    ClientApplied,
    ClientRejected,
    ClientTimedOut,
    ClientUnresolved,
}

/// <summary>A bounded diagnostic record for one authority request.</summary>
public readonly struct AuthorityRequestSnapshot
{
    public AuthorityRequestSnapshot(
        string route,
        string requestId,
        AuthorityRequestPhase phase,
        string outcome,
        DateTime startedUtc,
        DateTime updatedUtc)
    {
        Route = route;
        RequestId = requestId;
        Phase = phase;
        Outcome = outcome;
        StartedUtc = startedUtc;
        UpdatedUtc = updatedUtc;
    }

    public string Route { get; }
    public string RequestId { get; }
    public AuthorityRequestPhase Phase { get; }
    public string Outcome { get; }
    public DateTime StartedUtc { get; }
    public DateTime UpdatedUtc { get; }

    public bool IsTerminal => AuthorityRequestLifecycle.IsTerminal(Phase);
}

/// <summary>
/// Records a single authority-route lifecycle with structured, correlation-id based logging.
/// It deliberately does not dispatch gameplay work: request validation remains with each
/// route's server handler, while this makes a missing reply or client application observable.
/// </summary>
public sealed class AuthorityRequestLifecycle
{
    private const int DefaultCapacity = 128;

    private readonly string route;
    private readonly ILogger logger;
    private readonly int capacity;
    private readonly ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>();
    private readonly ConcurrentQueue<string> insertionOrder = new ConcurrentQueue<string>();

    public AuthorityRequestLifecycle(string route, ILogger logger = null, int capacity = DefaultCapacity)
    {
        if (string.IsNullOrWhiteSpace(route)) throw new ArgumentException("A route name is required.", nameof(route));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));

        this.route = route;
        this.logger = logger;
        this.capacity = capacity;
    }

    public void BeginClient(string requestId) => Begin(requestId, AuthorityRequestPhase.ClientRequested, null);

    public void BeginServer(string requestId) => Begin(requestId, AuthorityRequestPhase.ServerReceived, null);

    public void ClientSent(string requestId) => Record(requestId, AuthorityRequestPhase.ClientSent, null);

    public void ServerValidated(string requestId) => Record(requestId, AuthorityRequestPhase.ServerValidated, null);

    public void ServerResolved(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ServerResolved, outcome);

    public void ClientReplyReceived(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ClientReplyReceived, outcome);

    public void ClientApplied(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ClientApplied, outcome);

    public void ClientRejected(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ClientRejected, outcome);

    public void ClientTimedOut(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ClientTimedOut, outcome);

    public void ClientUnresolved(string requestId, string outcome) =>
        Record(requestId, AuthorityRequestPhase.ClientUnresolved, outcome);

    public bool TryGetSnapshot(string requestId, out AuthorityRequestSnapshot snapshot)
    {
        if (entries.TryGetValue(requestId, out var entry))
        {
            snapshot = entry.Snapshot(route, requestId);
            return true;
        }

        snapshot = default;
        return false;
    }

    private void Begin(string requestId, AuthorityRequestPhase phase, string outcome)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            logger?.Warning("AuthorityRoute ignored {Phase} without a request id. Route={Route}", phase, route);
            return;
        }

        var entry = new Entry(phase, outcome);
        if (!entries.TryAdd(requestId, entry))
        {
            logger?.Warning("AuthorityRoute duplicate request id. Route={Route} RequestId={RequestId}", route, requestId);
            return;
        }

        insertionOrder.Enqueue(requestId);
        TrimCompletedEntries();
        Write(phase, requestId, outcome, entry.StartedUtc);
    }

    private void Record(string requestId, AuthorityRequestPhase phase, string outcome)
    {
        if (!entries.TryGetValue(requestId, out var entry))
        {
            logger?.Warning(
                "AuthorityRoute observed {Phase} for an unknown request. Route={Route} RequestId={RequestId} Outcome={Outcome}",
                phase,
                route,
                requestId,
                outcome);
            return;
        }

        if (!entry.TryUpdate(phase, outcome, out var startedUtc))
        {
            logger?.Warning(
                "AuthorityRoute ignored {Phase} after terminal state. Route={Route} RequestId={RequestId} Outcome={Outcome}",
                phase,
                route,
                requestId,
                outcome);
            return;
        }

        Write(phase, requestId, outcome, startedUtc);
    }

    private void TrimCompletedEntries()
    {
        while (entries.Count > capacity && insertionOrder.TryDequeue(out var requestId))
        {
            if (!entries.TryRemove(requestId, out var entry)) continue;

            if (!entry.IsTerminal)
            {
                logger?.Warning(
                    "AuthorityRoute evicted an incomplete request after reaching diagnostic capacity. Route={Route} RequestId={RequestId} Phase={Phase}",
                    route,
                    requestId,
                    entry.Phase);
            }
        }
    }

    internal static bool IsTerminal(AuthorityRequestPhase phase) => phase == AuthorityRequestPhase.ServerResolved ||
        phase == AuthorityRequestPhase.ClientApplied ||
        phase == AuthorityRequestPhase.ClientRejected ||
        phase == AuthorityRequestPhase.ClientTimedOut ||
        phase == AuthorityRequestPhase.ClientUnresolved;

    private void Write(AuthorityRequestPhase phase, string requestId, string outcome, DateTime startedUtc)
    {
        var elapsedMilliseconds = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
        logger?.Information(
            "AuthorityRoute {Phase}. Route={Route} RequestId={RequestId} Outcome={Outcome} ElapsedMs={ElapsedMs}",
            phase,
            route,
            requestId,
            outcome,
            elapsedMilliseconds);
    }

    private sealed class Entry
    {
        private readonly object sync = new object();

        public Entry(AuthorityRequestPhase phase, string outcome)
        {
            Phase = phase;
            Outcome = outcome;
            StartedUtc = UpdatedUtc = DateTime.UtcNow;
        }

        public AuthorityRequestPhase Phase { get; private set; }
        public string Outcome { get; private set; }
        public DateTime StartedUtc { get; }
        public DateTime UpdatedUtc { get; private set; }
        public bool IsTerminal => AuthorityRequestLifecycle.IsTerminal(Phase);

        public bool TryUpdate(AuthorityRequestPhase phase, string outcome, out DateTime startedUtc)
        {
            lock (sync)
            {
                startedUtc = StartedUtc;
                if (IsTerminal) return false;

                Phase = phase;
                Outcome = outcome;
                UpdatedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public AuthorityRequestSnapshot Snapshot(string route, string requestId)
        {
            lock (sync)
            {
                return new AuthorityRequestSnapshot(route, requestId, Phase, Outcome, StartedUtc, UpdatedUtc);
            }
        }
    }
}
