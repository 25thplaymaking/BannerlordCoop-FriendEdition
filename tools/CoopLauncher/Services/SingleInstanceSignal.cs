using System;
using System.Threading;

namespace CoopLauncher.Services;

/// <summary>
/// Lets a second launcher hand focus to the one already running instead of complaining at the player.
/// </summary>
/// <remarks>
/// Only one launcher may run at a time — two racing the same install left players with a half-replaced
/// module. But refusing a second start with an error box is the wrong way to enforce it, because the
/// commonest second start is not the player being careless:
/// <list type="bullet">
/// <item>The game crashes, and the co-op crash collector relaunches the launcher through
/// <c>COOP_LAUNCHER_PATH</c> — often well after the player has already reopened it themselves.</item>
/// <item>The launcher stayed open behind the game, so the player cannot see it and starts another.</item>
/// </list>
/// In both cases the player wants the launcher in front of them, and an error dialog is noise. The
/// first instance waits on a named event; a second sets it, and exits without saying anything.
/// </remarks>
public sealed class SingleInstanceSignal : IDisposable
{
    private const string EventName = @"Local\CalradiaCoop.Launcher.Activate";

    private readonly EventWaitHandle handle;
    private readonly CancellationTokenSource cancellation = new();
    private Thread? listener;

    private SingleInstanceSignal(EventWaitHandle handle) => this.handle = handle;

    /// <summary>Opens the signal for the instance that owns the launcher.</summary>
    public static SingleInstanceSignal CreateOwner()
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return new SingleInstanceSignal(handle);
    }

    /// <summary>
    /// Asks the running launcher to show itself. False when there is nothing listening, in which case
    /// the caller has no one to defer to and should carry on starting.
    /// </summary>
    public static bool TryRequestActivation()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle? existing)) return false;
            using (existing) existing.Set();
            return true;
        }
        catch
        {
            // A signal that cannot be raised is not worth failing a start over.
            return false;
        }
    }

    /// <summary>Runs <paramref name="activate"/> whenever another launcher asks for the window.</summary>
    public void ListenForActivation(Action activate)
    {
        listener = new Thread(() =>
        {
            var waits = new WaitHandle[] { handle, cancellation.Token.WaitHandle };
            while (WaitHandle.WaitAny(waits) == 0)
            {
                try { activate(); }
                catch { /* a failed activation must not kill the listener */ }
            }
        })
        {
            IsBackground = true,
            Name = "launcher-activation-listener",
        };
        listener.Start();
    }

    public void Dispose()
    {
        try { cancellation.Cancel(); } catch { }
        cancellation.Dispose();
        handle.Dispose();
    }
}
