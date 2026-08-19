using System;

namespace Common.Network.Coalescing;

/// <summary>
/// Decides whether a coalesced update is worth sending yet, so the server can hold updates about
/// things no player can currently observe.
/// </summary>
/// <remarks>
/// The point is message COUNT. Replication volume on a large conversion is
/// <c>world size x rate of change x player count</c> — one live session ran 4,291 parties and peaked
/// at over a million messages in ten seconds — and none of it is filtered by whether a client can
/// actually see the thing being updated. Per-peer reliable queues reached tens of thousands of
/// packets, at which point <c>OverloadedPeerManager</c> correctly stops campaign time and the world
/// visibly freezes.
/// <para>
/// Holding is safe in a way that dropping would not be. Coalesced payloads MERGE — counts sum, sets
/// take the latest value — so an update held across many changes still carries the correct end state
/// when it is finally sent. A held update is late, never lost, and the merge means a party whose
/// roster churned a hundred times while nobody was near it costs one message instead of a hundred.
/// </para>
/// <para>
/// <see cref="MaximumHold"/> bounds the lateness so nothing starves: past it the update goes out
/// regardless of what the gate says. Destroy paths bypass the gate entirely — they use
/// <see cref="ISendCoalescer.FlushInstance"/>, which never consults it, preserving the existing
/// obligation that an object's final state precedes its destroy.
/// </para>
/// </remarks>
public interface ICoalesceGate
{
    /// <summary>
    /// True when this update should go out on this flush. Implementations MUST fail open — anything
    /// they cannot resolve or evaluate has to return true, because a wrongly held update is a
    /// divergence and a wrongly sent one is only a packet.
    /// </summary>
    bool ShouldSendNow(CoalesceKey key);

    /// <summary>
    /// Longest an update may be held. Once a key has waited this long it is sent whatever the gate
    /// says, so a mistake in the gate costs bandwidth rather than correctness.
    /// </summary>
    TimeSpan MaximumHold { get; }
}
