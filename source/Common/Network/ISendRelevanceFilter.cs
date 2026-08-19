using Common.Messaging;

namespace Common.Network;

/// <summary>
/// Decides, at the single point every message send passes through, whether a message is worth putting
/// on the wire right now — and takes ownership of the ones it holds back.
/// </summary>
/// <remarks>
/// Replication cost is <c>world size x rate of change x player count</c>, and nothing in the send path
/// asked whether a client could observe the change. A live session ran 4,291 parties, peaked at over a
/// million messages in ten seconds, and drove per-peer reliable queues to 69,037 packets — at which
/// point the overload throttle correctly stops campaign time and the world visibly freezes.
/// <para>
/// A filter returning false has ACCEPTED the message, not discarded it. It is responsible for sending
/// it later from <see cref="FlushDue"/>, which the network update calls every poll. Implementations
/// must fail open: anything they cannot classify goes out immediately, because a wrongly held message
/// is a desync and a wrongly sent one is only a packet.
/// </para>
/// </remarks>
public interface ISendRelevanceFilter
{
    /// <summary>
    /// True to send <paramref name="message"/> now. False means the filter has buffered it and will
    /// send it from a later <see cref="FlushDue"/>.
    /// </summary>
    bool ShouldSendNow(IMessage message);

    /// <summary>
    /// Sends everything the filter is holding that has become relevant or waited long enough. Called
    /// once per network update.
    /// </summary>
    void FlushDue(INetwork network);
}
