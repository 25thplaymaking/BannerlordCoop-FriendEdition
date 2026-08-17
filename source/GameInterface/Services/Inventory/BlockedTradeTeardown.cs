using System;

namespace GameInterface.Services.Inventory;

/// <summary>
/// Marks the window in which a blocked co-op trade completion is tearing its own screen down.
///
/// Closing the inventory screen is not a leaf operation: TaleWorlds'
/// <c>InventoryScreenHelper.CloseScreen</c> calls <c>InventoryManager.CloseInventoryPresentation</c>,
/// which calls <c>InventoryLogic.DoneLogic</c> again. Coop's <c>DoneLogic</c> prefix publishes
/// <c>TradeAttempted</c>, the message broker dispatches synchronously on the publishing thread, and
/// <c>TradeHandler</c> answers a blocked completion by closing the screen — from the game thread,
/// where <c>GameThread.Run</c> executes inline. So accepting a trade re-entered its own prefix and
/// recursed until the stack ran out: the client died on the spot with exit code 0x800703E9
/// (ERROR_STACK_OVERFLOW), which is uncatchable, so there was no managed exception and no dump —
/// just the window vanishing the instant the player pressed Accept. The crash log shows 892 nested
/// "Blocked legacy client-authored trade completion" lines inside a single millisecond.
///
/// The nested call is legitimate and must be answered, not published again. Depth is per-thread
/// because <c>DoneLogic</c> only ever runs on the game thread and a leaked flag would silently
/// disable trade completion for the rest of the session.
/// </summary>
internal static class BlockedTradeTeardown
{
    [ThreadStatic]
    private static int depth;

    /// <summary>True while this thread is inside a blocked-trade screen teardown.</summary>
    internal static bool InProgress => depth > 0;

    internal static void Enter() => depth++;

    /// <summary>Paired with <see cref="Enter"/> in a finally; never drops below zero.</summary>
    internal static void Exit()
    {
        if (depth > 0) depth--;
    }

    /// <summary>Test seam so one case's leaked depth cannot bleed into the next.</summary>
    internal static void Reset() => depth = 0;
}
