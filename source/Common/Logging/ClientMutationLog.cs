using Serilog;
using System.Collections.Concurrent;

namespace Common.Logging;

/// <summary>
/// Bounded reporting for "a client locally created/updated a server-owned object".
///
/// This is a normal, expected condition: the lifetime and AutoSync patches suppress the local write
/// and let the server author it. Logging every occurrence at Error made it the loudest thing in the
/// file — one session produced 48,712 lines at roughly 200/second, a 7.7 MB log in ten minutes, and
/// the 892 lines that actually explained a client-killing stack overflow were buried underneath it.
/// A signal that fires constantly is not a signal.
///
/// Keep the first few reports per subject so a genuinely new offender is still visible, say so once
/// when a subject starts repeating, then stay quiet. Warning rather than Error: nothing here is a
/// failure, and reserving Error for real faults is what makes the log searchable.
/// </summary>
public static class ClientMutationLog
{
    /// <summary>Reports kept per (action, subject) before the subject goes quiet.</summary>
    internal const int PerSubjectLimit = 3;

    private static readonly ConcurrentDictionary<string, int> Seen = new ConcurrentDictionary<string, int>();

    /// <param name="logger">The calling patch's logger, so the source context stays accurate.</param>
    /// <param name="action">"created" or "updated".</param>
    /// <param name="subject">Type or member the client tried to author.</param>
    public static void Report(ILogger logger, string action, object subject)
    {
        if (logger == null) return;

        string key = action + ":" + (subject?.ToString() ?? "<null>");
        int count = Seen.AddOrUpdate(key, 1, (_, previous) => previous + 1);

        if (count <= PerSubjectLimit)
        {
            logger.Warning("Client {Action} managed {Subject}", action, subject);
            return;
        }

        if (count == PerSubjectLimit + 1)
        {
            logger.Warning(
                "Client {Action} managed {Subject} repeatedly; further reports for it are suppressed this session",
                action, subject);
        }
    }

    /// <summary>Test seam; also lets a new session start from a clean slate.</summary>
    public static void Reset() => Seen.Clear();
}
