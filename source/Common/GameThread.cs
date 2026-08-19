using Common.Logging;
using Common.Util;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Common;

public class GameThread : IUpdateable
{
    private static ILogger Logger = LogManager.GetLogger<GameThread>();

    private static readonly Lazy<GameThread> m_Instance =
        new Lazy<GameThread>(() => new GameThread());

    private readonly Queue<(Action Act, EventWaitHandle Wait, string Label, CancellationToken Cancellation)> m_Queue =
        new Queue<(Action, EventWaitHandle, string, CancellationToken)>();

    private readonly object m_QueueLock = new object();
    private static readonly AsyncLocal<CancellationToken> m_AmbientCancellation =
        new AsyncLocal<CancellationToken>();
    private int m_GameLoopThreadId;

    public int QueueLength
    {
        get
        {
            lock (m_QueueLock)
            {
                return m_Queue.Count;
            }
        }
    }

    public bool IsInitialized => m_GameLoopThreadId != 0;

    /// <summary>
    /// True when the caller is running on the game-loop thread that drains the queue in <see cref="Update"/>.
    /// A blocking caller already on this thread must pump <see cref="Update"/> itself while it waits, or it
    /// stalls the very queue its completion depends on.
    /// </summary>
    public bool IsGameThread => Thread.CurrentThread.ManagedThreadId == m_GameLoopThreadId;

    private GameThread()
    {
    }

    public static GameThread Instance => m_Instance.Value;

    #region Instrumentation

    /// <summary>
    /// When true, <see cref="Update"/> times how long it spends draining the queue each frame and
    /// periodically logs a summary: total drain time, action count and rate, the worst single-frame
    /// hitch, the deepest backlog, and the top contributors by cumulative time. This attributes
    /// game-thread (render-thread) lag to the handlers that cause it. Each queued action is labeled
    /// automatically from its caller (file + method) unless an explicit context is supplied, so no
    /// call site needs to change. Off by default; toggle it at runtime on the process you want to
    /// profile (typically the client) with the <c>coop.debug.gamethread.instrument</c> console command.
    /// </summary>
    public static bool Instrument = false;

    /// <summary>How often the drain summary is written to the log.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    /// <summary>How many of the heaviest labels to list in each summary.</summary>
    private const int TopLabelCount = 10;

    private readonly Stopwatch m_ReportTimer = Stopwatch.StartNew();
    private readonly Dictionary<string, (long Ticks, int Count)> m_PerLabel =
        new Dictionary<string, (long, int)>();
    private int m_WindowFrames;
    private int m_WindowActions;
    private long m_WindowTicks;
    private long m_WorstFrameTicks;
    private int m_WorstFrameActions;
    private int m_WorstBacklog;
    private int m_WorstDeferred;

    private static double ToMs(long ticks) => 1000.0 * ticks / Stopwatch.Frequency;

    #endregion

    #region Drain budget

    /// <summary>
    /// Caps how long <see cref="Update"/> may spend running queued actions in one frame, so a burst
    /// of marshaled work is spread across several frames instead of stopping the game inside one.
    /// </summary>
    /// <remarks>
    /// Without this the pump takes the whole queue every frame and runs all of it, so the client's
    /// frame time is whatever the server happened to send since the last one. That is survivable at
    /// a steady rate and not survivable after a host-side stall: the dedicated host's autosave blocks
    /// its game thread for over four seconds, and everything it could not send during that window
    /// arrives at once. The client then executes the entire backlog in a single frame and stops dead
    /// for about as long as the host did — the freeze players report, with no save indicator on screen
    /// to explain it.
    /// <para>
    /// The queue is drained in order and whatever does not fit is simply left for the next frame, so
    /// nothing is reordered and nothing is dropped — work is only deferred. Two rules bound how long a
    /// deferral can last. The budget scales with the backlog, so a flood is cleared far faster than a
    /// trickle. And an action a caller is blocked on is always run, budget or not: blocking callers
    /// wait on a <see cref="BlockingTimeout"/> deadline, and because the queue is drained in order such
    /// an action can only be at the head when it is reached.
    /// </para>
    /// <para>
    /// Client-only, deliberately. This protects frame rendering, and the headless host has no frames to
    /// protect; deferring work there would only delay the authority replies clients are timing out
    /// against. <see cref="BudgetedDrain"/> turns it off for A/B measurement against the same
    /// instrumentation that identified the problem.
    /// </para>
    /// </remarks>
    public static bool BudgetedDrain = true;

    /// <summary>Budget for a frame with a shallow queue: small enough to disappear into a 16 ms frame.</summary>
    private static readonly TimeSpan MinimumDrainBudget = TimeSpan.FromMilliseconds(6);

    /// <summary>
    /// Ceiling for a frame with a deep queue. Deliberately well above the frame budget: once the client
    /// is this far behind, clearing the backlog quickly matters more than a smooth frame, and a visibly
    /// slow second beats a four-second freeze. It still yields often enough for the game to keep drawing.
    /// </summary>
    private static readonly TimeSpan MaximumDrainBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>Backlog at which the budget reaches <see cref="MaximumDrainBudget"/>, ramping linearly.</summary>
    private const int BacklogAtMaximumBudget = 2000;

    /// <summary>
    /// How long a frame may spend draining at the given backlog, or null for "no limit" — the host, and
    /// any client that has turned the budget off, drain exactly as they always did.
    /// </summary>
    /// <remarks>
    /// The ramp is what keeps a deferral bounded. A shallow queue gets a budget small enough to vanish
    /// into a frame; a queue deep enough to represent a host stall gets one large enough to clear it in
    /// a second or so rather than trickling it out over a minute, which is what would actually put a
    /// blocking caller past its <see cref="BlockingTimeout"/>.
    /// </remarks>
    public static TimeSpan? GetDrainBudget(int backlog)
    {
        if (!BudgetedDrain || ModInformation.IsServer) return null;

        double ramp = Math.Min(1.0, (double)Math.Max(0, backlog) / BacklogAtMaximumBudget);
        double milliseconds = MinimumDrainBudget.TotalMilliseconds +
            ramp * (MaximumDrainBudget.TotalMilliseconds - MinimumDrainBudget.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Ticks this frame may spend, or -1 for "no limit".</summary>
    private static long GetDrainBudgetTicks(int backlog)
    {
        TimeSpan? budget = GetDrainBudget(backlog);
        if (!budget.HasValue) return -1;

        return (long)(budget.Value.TotalMilliseconds * Stopwatch.Frequency / 1000.0);
    }

    #endregion

    public void Update(TimeSpan frameTime)
    {
        if (Thread.CurrentThread.ManagedThreadId != Instance.m_GameLoopThreadId)
        {
            throw new ArgumentException("Wrong thread!");
        }

        int backlog;
        lock (Instance.m_QueueLock)
        {
            backlog = m_Queue.Count;
        }

        long budgetTicks = GetDrainBudgetTicks(backlog);
        long frameStart = Stopwatch.GetTimestamp();
        int ranThisFrame = 0;

        while (true)
        {
            (Action Act, EventWaitHandle Wait, string Label, CancellationToken Cancellation) task;

            lock (Instance.m_QueueLock)
            {
                if (m_Queue.Count == 0) break;

                // Decided on the head, before it is taken, so the action stays queued in order when
                // it is deferred. Two exemptions keep the queue moving: the first action of a frame
                // always runs, so a single expensive action can never stall the pump forever, and an
                // action with a waiter always runs, because a caller is blocked on it against a
                // deadline and it can only be seen here once everything ahead of it has run.
                if (ranThisFrame > 0 &&
                    budgetTicks >= 0 &&
                    m_Queue.Peek().Wait == null &&
                    Stopwatch.GetTimestamp() - frameStart >= budgetTicks)
                {
                    break;
                }

                task = m_Queue.Dequeue();
            }

            if (!Instrument)
            {
                RunQueuedTask(task);
                ranThisFrame++;
                continue;
            }

            long actionStart = Stopwatch.GetTimestamp();
            RunQueuedTask(task);
            long actionTicks = Stopwatch.GetTimestamp() - actionStart;
            ranThisFrame++;

            string label = task.Label ?? "(unlabeled)";
            m_PerLabel.TryGetValue(label, out (long Ticks, int Count) agg);
            m_PerLabel[label] = (agg.Ticks + actionTicks, agg.Count + 1);
        }

        if (!Instrument) return;

        long frameTicks = Stopwatch.GetTimestamp() - frameStart;

        m_WindowFrames++;
        m_WindowActions += ranThisFrame;
        m_WindowTicks += frameTicks;
        if (frameTicks > m_WorstFrameTicks)
        {
            m_WorstFrameTicks = frameTicks;
            m_WorstFrameActions = ranThisFrame;
        }
        if (backlog > m_WorstBacklog)
        {
            m_WorstBacklog = backlog;
        }
        int deferred = backlog - ranThisFrame;
        if (deferred > m_WorstDeferred)
        {
            m_WorstDeferred = deferred;
        }

        if (m_ReportTimer.Elapsed >= ReportInterval)
        {
            ReportAndReset();
        }
    }

    private void ReportAndReset()
    {
        double seconds = m_ReportTimer.Elapsed.TotalSeconds;

        // Skip the noisy log when the game thread did no marshaled work this window.
        if (m_WindowActions > 0)
        {
            string top = string.Join(", ", m_PerLabel
                .OrderByDescending(kv => kv.Value.Ticks)
                .Take(TopLabelCount)
                .Select(kv => $"{kv.Key}={ToMs(kv.Value.Ticks):0.0}ms/{kv.Value.Count}"));

            Logger.Information(
                "[GameThread] {Frames} frames | {Actions} actions ({Rate:0}/s) | drain {Drain:0.0}ms " +
                "({PerFrame:0.00}ms/frame) | worst frame {Worst:0.0}ms/{WorstActions} actions | " +
                "max backlog {Backlog} | max deferred {Deferred} | top: {Top}",
                m_WindowFrames,
                m_WindowActions,
                m_WindowActions / seconds,
                ToMs(m_WindowTicks),
                ToMs(m_WindowTicks) / Math.Max(1, m_WindowFrames),
                ToMs(m_WorstFrameTicks),
                m_WorstFrameActions,
                m_WorstBacklog,
                m_WorstDeferred,
                top);
        }

        m_PerLabel.Clear();
        m_WindowFrames = 0;
        m_WindowActions = 0;
        m_WindowTicks = 0;
        m_WorstFrameTicks = 0;
        m_WorstFrameActions = 0;
        m_WorstBacklog = 0;
        m_WorstDeferred = 0;
        m_ReportTimer.Restart();
    }

    public int Priority { get; } = UpdatePriority.MainLoop.GameThread;

    /// <summary>
    /// Maximum time a blocking <see cref="Run(Action, bool, string, string, string)"/> call waits for the
    /// game loop to process the queued action before failing. Turns a silent deadlock into a loud error
    /// when the game loop is not pumping (or was never initialized, as in test environments).
    /// </summary>
    public static readonly TimeSpan BlockingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a given action on the game thread
    /// </summary>
    /// <param name="action">Action to run on game thread</param>
    /// <param name="blocking">Flag to pause code execution,
    /// True blocks execution until task is complete,
    /// False queues and returns</param>
    /// <param name="label">Optional name used to attribute drain time in the instrumentation summary.
    /// Defaults to the calling file and method, so call sites do not need to pass anything.</param>
    /// <exception cref="TimeoutException">
    /// Thrown for blocking calls when the action was not processed within <see cref="BlockingTimeout"/>.
    /// </exception>
    public static void Run(Action action, bool blocking = false, string label = null,
        [CallerFilePath] string callerFile = null,
        [CallerMemberName] string callerMember = null)
    {
        CancellationToken cancellation = m_AmbientCancellation.Value;
        if (cancellation.IsCancellationRequested)
        {
            if (blocking)
            {
                throw new OperationCanceledException(
                    $"The game-thread session ended before the blocking {nameof(Run)} action was queued.");
            }
            // This return is one of the few places marshalled work can vanish without a trace;
            // name the dropped action so a lost state-apply is diagnosable from the log.
            Logger.Warning("Dropping game-thread action {Label}: the session was cancelled before it was queued",
                label ?? BuildLabel(callerFile, callerMember));
            return;
        }

        if (Thread.CurrentThread.ManagedThreadId == Instance.m_GameLoopThreadId)
        {
            action();
        }
        else
        {
            EventWaitHandle ewh = blocking ?
                new EventWaitHandle(false, EventResetMode.ManualReset) :
                null;

            string resolved = label ?? BuildLabel(callerFile, callerMember);
            lock (Instance.m_QueueLock)
            {
                Instance.m_Queue.Enqueue((action, ewh, resolved, cancellation));
            }

            if (ewh == null) return;

            int waitResult = !cancellation.CanBeCanceled
                ? (ewh.WaitOne(BlockingTimeout) ? 0 : WaitHandle.WaitTimeout)
                : WaitHandle.WaitAny(
                    new[] { ewh, cancellation.WaitHandle },
                    BlockingTimeout);
            if (waitResult == WaitHandle.WaitTimeout)
            {
                throw new TimeoutException(
                    $"A blocking {nameof(Run)} action was not processed by the game loop " +
                    $"within {BlockingTimeout.TotalSeconds:0} seconds. The game loop thread is not pumping " +
                    $"{nameof(GameThread)}.{nameof(Update)} (initialized: {Instance.IsInitialized}).");
            }
            if (waitResult == 1)
            {
                throw new OperationCanceledException(
                    $"The game-thread session ended before the blocking {nameof(Run)} action completed.");
            }
        }
    }

    /// <summary>
    /// Runs a given action on the game thread, logging any exception the action throws instead of
    /// letting it propagate. The guard is wrapped around the action itself, so it travels onto the
    /// game thread and catches the failure where the action actually runs (inside <see cref="Update"/>).
    /// This keeps a single failing action from killing the pump and deadlocking blocking callers
    /// waiting on the queue.
    /// </summary>
    /// <param name="action">Action to run on game thread</param>
    /// <param name="blocking">Flag to pause code execution,
    /// True blocks execution until task is complete,
    /// False queues and returns</param>
    /// <param name="context">Optional description of the action, attached to the error log to
    /// identify which caller's action failed, and used to attribute drain time in the instrumentation
    /// summary. Defaults to the calling file and method.</param>
    public static void RunSafe(Action action, bool blocking = false, string context = null,
        [CallerFilePath] string callerFile = null,
        [CallerMemberName] string callerMember = null)
    {
        string label = context ?? BuildLabel(callerFile, callerMember);
        Run(WrapSafe(action, context), blocking, label);
    }

    /// <summary>
    /// Queues an action for a later <see cref="Update"/> even when called from the game-loop thread.
    /// Use this when running inline would mutate state currently being iterated by the engine.
    /// </summary>
    public static void EnqueueSafe(Action action, string context = null,
        [CallerFilePath] string callerFile = null,
        [CallerMemberName] string callerMember = null)
    {
        CancellationToken cancellation = m_AmbientCancellation.Value;
        if (cancellation.IsCancellationRequested) return;

        string label = context ?? BuildLabel(callerFile, callerMember);
        lock (Instance.m_QueueLock)
        {
            Instance.m_Queue.Enqueue((WrapSafe(action, context), null, label, cancellation));
        }
    }

    /// <summary>
    /// Blocks until <paramref name="condition"/> returns true or <paramref name="deadline"/> passes, and
    /// reports which happened, draining <see cref="Update"/> each iteration so the work the condition depends
    /// on — and the blocking <see cref="Run"/> handlers the network thread is waiting on — keeps making
    /// progress; a bare wait on the game-loop thread would stall the very queue it is waiting on, a
    /// self-inflicted deadlock that only breaks at the deadline. Must be called on the game-loop thread,
    /// which owns the pump.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called off the game-loop thread.</exception>
    public static bool WaitWhilePumping(Func<bool> condition, DateTime deadline)
    {
        if (!Instance.IsGameThread)
            throw new InvalidOperationException(
                $"{nameof(WaitWhilePumping)} must be called on the game-loop thread; it drains the queue while it waits.");

        while (true)
        {
            // Drain with the mod's patches live. The queued actions are ordinary game-loop work and must not
            // inherit an AllowedThread allowance the caller happens to hold — that would silence the
            // replication patches the actions rely on. The normal game-loop pump runs them with no allowance,
            // so suspend any ambient one here to match it.
            using (AllowedThread.Suspend())
            {
                // A single failing queued action must not abort the wait (which would also leave that action's
                // own blocking caller waiting out its full timeout); log and keep pumping, mirroring RunSafe.
                // Without this guard the throw would escape into whatever the waiter is doing — e.g. mid
                // battle-start construction.
                try
                {
                    Instance.Update(TimeSpan.Zero);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "A queued action threw while pumping the game thread during a blocking wait");
                }
            }

            if (condition())
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(5);
        }
    }

    private static string BuildLabel(string callerFile, string callerMember)
    {
        if (string.IsNullOrEmpty(callerFile))
        {
            return callerMember ?? "(unknown)";
        }
        return $"{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}";
    }

    private static Action WrapSafe(Action action, string context) => () =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to run action on the game thread: {Context}", context ?? "(none)");
        }
    };

    public void MarkGameThread()
    {
        m_GameLoopThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>
    /// The currently registered game-loop thread id (0 when unmarked). Pair with
    /// <see cref="RestoreGameThread"/> so a scope that re-marks the game thread (e.g. a test harness
    /// running a call on a worker thread) can put the previous registration back instead of leaving
    /// the mark on a thread that may never pump the queue again.
    /// </summary>
    public int GameThreadId => m_GameLoopThreadId;

    /// <summary>
    /// Restores a registration previously read from <see cref="GameThreadId"/>.
    /// </summary>
    public void RestoreGameThread(int threadId)
    {
        m_GameLoopThreadId = threadId;
    }

    /// <summary>
    /// Discards every queued action without running it, releasing any blocked callers waiting on
    /// them. For test harnesses at environment boundaries: an action queued by a previous test
    /// would otherwise execute inside a later environment's pump against a torn-down container.
    /// </summary>
    public void DiscardQueuedActions()
    {
        List<(Action Act, EventWaitHandle Wait, string Label, CancellationToken Cancellation)> discarded;
        lock (m_QueueLock)
        {
            discarded = new List<(Action, EventWaitHandle, string, CancellationToken)>(m_Queue);
            m_Queue.Clear();
        }

        if (discarded.Count > 0)
        {
            // A non-empty queue here means marshalled work was enqueued but never pumped — for a
            // test harness that is a silently lost state-apply, so name every dropped action.
            Logger.Warning("Discarding {Count} queued game-thread action(s) that no pump ever ran: {Labels}",
                discarded.Count,
                string.Join(", ", discarded.Select(task => task.Label ?? "(unlabeled)")));
        }

        foreach (var task in discarded)
        {
            task.Wait?.Set();
        }
    }

    /// <summary>
    /// Clears the game-loop thread registration. A thread that was marked via
    /// <see cref="MarkGameThread"/> must call this before it exits: .NET recycles managed thread
    /// ids, so a registration left behind by a dead thread can silently promote an unrelated
    /// future thread to "game thread", flipping <see cref="Run(Action, bool, string, string, string)"/>
    /// from queueing to inline execution.
    /// </summary>
    public void UnmarkGameThread()
    {
        m_GameLoopThreadId = 0;
    }

    public static IDisposable ActivateCancellation(CancellationToken cancellation) =>
        new CancellationScope(cancellation);

    private static void RunQueuedTask(
        (Action Act, EventWaitHandle Wait, string Label, CancellationToken Cancellation) task)
    {
        try
        {
            if (task.Cancellation.IsCancellationRequested) return;

            using (ActivateCancellation(task.Cancellation))
            {
                task.Act?.Invoke();
            }
        }
        finally
        {
            task.Wait?.Set();
        }
    }

    private sealed class CancellationScope : IDisposable
    {
        private readonly CancellationToken previous;

        public CancellationScope(CancellationToken cancellation)
        {
            previous = m_AmbientCancellation.Value;
            m_AmbientCancellation.Value = cancellation;
        }

        public void Dispose()
        {
            m_AmbientCancellation.Value = previous;
        }
    }
}
