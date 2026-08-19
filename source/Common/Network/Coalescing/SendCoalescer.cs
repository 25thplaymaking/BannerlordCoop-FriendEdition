using System;
using System.Collections.Generic;

namespace Common.Network.Coalescing;

/// <inheritdoc cref="ISendCoalescer"/>
public sealed class SendCoalescer : ISendCoalescer
{
    private readonly Dictionary<CoalesceKey, ICoalescedPayload> pending = new();
    private readonly List<CoalesceKey> order = new();
    private readonly object gate = new();

    // When each still-pending key was first enqueued, so a gated flush can bound how long it holds
    // an update. Kept alongside `order` and cleared with it.
    private readonly Dictionary<CoalesceKey, DateTime> firstEnqueuedUtc = new();

    public bool HasPending
    {
        get
        {
            lock (gate)
            {
                return pending.Count > 0;
            }
        }
    }

    public void Enqueue(CoalesceKey key, ICoalescedPayload payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        lock (gate)
        {
            if (pending.TryGetValue(key, out var existing))
            {
                pending[key] = existing.Merge(payload);
                return;
            }

            pending.Add(key, payload);
            order.Add(key);
            firstEnqueuedUtc[key] = DateTime.UtcNow;
        }
    }

    public void Flush(INetwork network) => Flush(network, null);

    public void Flush(INetwork network, ICoalesceGate sendGate)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));

        List<ICoalescedPayload> toSend;
        lock (gate)
        {
            if (pending.Count == 0) return;

            if (sendGate == null)
            {
                toSend = new List<ICoalescedPayload>(pending.Count);
                for (int i = 0; i < order.Count; i++) toSend.Add(pending[order[i]]);

                pending.Clear();
                order.Clear();
                firstEnqueuedUtc.Clear();
            }
            else
            {
                // Held keys stay in `order` in their original relative position, so what does go out
                // this flush keeps the enqueue order the reliable stream depends on.
                DateTime now = DateTime.UtcNow;
                TimeSpan maximumHold = sendGate.MaximumHold;

                toSend = new List<ICoalescedPayload>(order.Count);
                List<CoalesceKey> held = new List<CoalesceKey>();

                for (int i = 0; i < order.Count; i++)
                {
                    CoalesceKey key = order[i];

                    bool expired = firstEnqueuedUtc.TryGetValue(key, out DateTime since) &&
                        now - since >= maximumHold;

                    if (!expired && !SendNowSafely(sendGate, key))
                    {
                        held.Add(key);
                        continue;
                    }

                    toSend.Add(pending[key]);
                    pending.Remove(key);
                    firstEnqueuedUtc.Remove(key);
                }

                order.Clear();
                order.AddRange(held);
            }
        }

        foreach (var payload in toSend)
        {
            network.SendAll(payload.ToMessage());
        }
    }

    /// <summary>
    /// Asks the gate, treating any failure as "send it". A gate is an optimisation; a throwing gate
    /// must not be able to strand an update and desync a client.
    /// </summary>
    private static bool SendNowSafely(ICoalesceGate sendGate, CoalesceKey key)
    {
        try
        {
            return sendGate.ShouldSendNow(key);
        }
        catch
        {
            return true;
        }
    }

    public void FlushInstance(string instanceId, INetwork network)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));

        List<ICoalescedPayload> toSend = ExtractInstance(instanceId);
        if (toSend == null) return;

        foreach (var payload in toSend)
        {
            network.SendAll(payload.ToMessage());
        }
    }

    public void DropInstance(string instanceId)
    {
        ExtractInstance(instanceId);
    }

    // Removes and returns every pending payload for the instance, or null if none. The caller decides
    // whether to send them (FlushInstance) or discard them (DropInstance).
    private List<ICoalescedPayload> ExtractInstance(string instanceId)
    {
        lock (gate)
        {
            List<ICoalescedPayload> payloads = null;
            for (int i = 0; i < order.Count;)
            {
                var key = order[i];
                if (string.Equals(key.InstanceId, instanceId, StringComparison.Ordinal))
                {
                    (payloads ??= new List<ICoalescedPayload>()).Add(pending[key]);
                    pending.Remove(key);
                    firstEnqueuedUtc.Remove(key);
                    order.RemoveAt(i);
                    continue;
                }

                i++;
            }

            return payloads;
        }
    }
}
