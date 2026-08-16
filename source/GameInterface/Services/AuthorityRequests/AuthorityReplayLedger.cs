using LiteNetLib;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.AuthorityRequests;

internal enum AuthorityReplayDecision
{
    New,
    InFlight,
    Completed,
    Conflict,
    OverCapacity,
}

internal readonly struct AuthorityReplayInspection<TResult>
{
    public AuthorityReplayInspection(AuthorityReplayDecision decision, TResult result, bool suppressReply = false)
    {
        Decision = decision;
        Result = result;
        SuppressReply = suppressReply;
    }

    public AuthorityReplayDecision Decision { get; }
    public TResult Result { get; }
    public bool SuppressReply { get; }
}

/// <summary>Bounded, per-peer idempotency store for one typed authority route.</summary>
internal sealed class AuthorityReplayLedger<TResult>
{
    private readonly int capacityPerPeer;
    private readonly Dictionary<ReplayKey, Entry> entries = new Dictionary<ReplayKey, Entry>();
    private long sequence;

    public AuthorityReplayLedger(int capacityPerPeer = 256)
    {
        if (capacityPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(capacityPerPeer));
        this.capacityPerPeer = capacityPerPeer;
    }

    public AuthorityReplayInspection<TResult> Inspect(
        NetPeer peer,
        string sessionId,
        string routeId,
        long requestId,
        string commandKey)
    {
        var key = new ReplayKey(peer, sessionId, routeId, requestId);
        if (!entries.TryGetValue(key, out var entry))
        {
            if (!MakeRoom(peer))
                return new AuthorityReplayInspection<TResult>(AuthorityReplayDecision.OverCapacity, default);

            entries[key] = new Entry(commandKey, ++sequence);
            return new AuthorityReplayInspection<TResult>(AuthorityReplayDecision.New, default);
        }

        entry.LastAccess = ++sequence;
        if (!string.Equals(entry.CommandKey, commandKey, StringComparison.Ordinal))
            return new AuthorityReplayInspection<TResult>(AuthorityReplayDecision.Conflict, default);

        return new AuthorityReplayInspection<TResult>(
            entry.Completed ? AuthorityReplayDecision.Completed : AuthorityReplayDecision.InFlight,
            entry.Result, entry.SuppressReply);
    }

    public void Complete(NetPeer peer, string sessionId, string routeId, long requestId, TResult result,
        bool suppressReply = false)
    {
        var key = new ReplayKey(peer, sessionId, routeId, requestId);
        if (!entries.TryGetValue(key, out var entry)) return;

        entry.Result = result;
        entry.SuppressReply = suppressReply;
        entry.Completed = true;
        entry.LastAccess = ++sequence;
    }

    public void Clear(NetPeer peer)
    {
        if (peer == null) return;
        var remove = new List<ReplayKey>();
        foreach (var entry in entries)
            if (ReferenceEquals(entry.Key.Peer, peer)) remove.Add(entry.Key);
        foreach (var key in remove) entries.Remove(key);
    }

    public void Clear()
    {
        entries.Clear();
    }

    private bool MakeRoom(NetPeer peer)
    {
        int count = 0;
        foreach (var entry in entries)
        {
            if (ReferenceEquals(entry.Key.Peer, peer)) count++;
        }

        if (count < capacityPerPeer) return true;

        ReplayKey oldest = default;
        long oldestAccess = long.MaxValue;
        foreach (var entry in entries)
        {
            if (!ReferenceEquals(entry.Key.Peer, peer) || !entry.Value.Completed ||
                entry.Value.LastAccess >= oldestAccess) continue;

            oldest = entry.Key;
            oldestAccess = entry.Value.LastAccess;
        }

        if (oldestAccess == long.MaxValue) return false;
        entries.Remove(oldest);
        return true;
    }

    private readonly struct ReplayKey : IEquatable<ReplayKey>
    {
        public ReplayKey(NetPeer peer, string sessionId, string routeId, long requestId)
        {
            Peer = peer;
            SessionId = sessionId;
            RouteId = routeId;
            RequestId = requestId;
        }

        public NetPeer Peer { get; }
        public string SessionId { get; }
        public string RouteId { get; }
        public long RequestId { get; }

        public bool Equals(ReplayKey other) => ReferenceEquals(Peer, other.Peer) &&
            RequestId == other.RequestId &&
            string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
            string.Equals(RouteId, other.RouteId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ReplayKey other && Equals(other);
        public override int GetHashCode() => (Peer?.GetHashCode() ?? 0) ^ RequestId.GetHashCode() ^
            (SessionId?.GetHashCode() ?? 0) ^ (RouteId?.GetHashCode() ?? 0);
    }

    private sealed class Entry
    {
        public Entry(string commandKey, long lastAccess)
        {
            CommandKey = commandKey;
            LastAccess = lastAccess;
        }

        public string CommandKey { get; }
        public TResult Result { get; set; }
        public bool SuppressReply { get; set; }
        public bool Completed { get; set; }
        public long LastAccess { get; set; }
    }
}
