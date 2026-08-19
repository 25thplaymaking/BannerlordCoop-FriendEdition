using Common;
using System;
using Xunit;

namespace GameInterface.Tests.Utils;

/// <summary>
/// Covers the drain budget that stops a client applying a whole backlog inside one frame.
/// </summary>
/// <remarks>
/// The pump used to take the entire queue every frame and run all of it, so a client's frame time was
/// whatever the host had sent since the last one. That is fine at a steady rate and not fine after the
/// host's autosave blocks its game thread for four-plus seconds: everything it could not send arrives
/// at once and the client stops for about as long as the host did.
/// <para>
/// These assert the policy, which is the part that can be silently wrong. The mechanics it drives —
/// draining in order and leaving the remainder queued — cannot reorder or drop work by construction,
/// because there is one queue and it is dequeued from the head.
/// </para>
/// </remarks>
public class GameThreadDrainBudgetTests
{
    private static T WithServerFlag<T>(bool isServer, Func<T> body)
    {
        bool previous = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = isServer;
            return body();
        }
        finally
        {
            ModInformation.IsServer = previous;
        }
    }

    [Fact]
    public void TheHostIsNeverBudgeted()
    {
        // A headless host has no frames to protect, and deferring there would only delay the authority
        // replies that clients are timing out against.
        Assert.Null(WithServerFlag(true, () => GameThread.GetDrainBudget(50_000)));
    }

    [Fact]
    public void TurningTheBudgetOffRestoresTheOriginalDrainEverything()
    {
        bool previous = GameThread.BudgetedDrain;
        try
        {
            GameThread.BudgetedDrain = false;
            Assert.Null(WithServerFlag(false, () => GameThread.GetDrainBudget(50_000)));
        }
        finally
        {
            GameThread.BudgetedDrain = previous;
        }
    }

    [Fact]
    public void AShallowQueueGetsABudgetThatFitsInsideAFrame()
    {
        TimeSpan? budget = WithServerFlag(false, () => GameThread.GetDrainBudget(0));

        Assert.NotNull(budget);
        Assert.InRange(budget.Value.TotalMilliseconds, 0.1, 16.0);
    }

    [Fact]
    public void TheBudgetGrowsWithTheBacklog()
    {
        // Without this a flood would be metered out at the shallow-queue rate and take far too long to
        // clear, which is its own failure mode: a blocking caller behind the flood would time out.
        var budgets = WithServerFlag(false, () => new[]
        {
            GameThread.GetDrainBudget(0).Value,
            GameThread.GetDrainBudget(500).Value,
            GameThread.GetDrainBudget(2000).Value,
        });

        Assert.True(budgets[0] < budgets[1], "the budget did not grow with a moderate backlog");
        Assert.True(budgets[1] < budgets[2], "the budget did not grow with a deep backlog");
    }

    [Fact]
    public void TheBudgetIsCappedSoAFrameStillEnds()
    {
        var budgets = WithServerFlag(false, () => new[]
        {
            GameThread.GetDrainBudget(2_000).Value,
            GameThread.GetDrainBudget(2_000_000).Value,
        });

        // A backlog a thousand times deeper must not buy a thousand times the frame: past the ramp the
        // budget is flat, so catching up costs more frames rather than one unbounded one.
        Assert.Equal(budgets[0], budgets[1]);
        Assert.InRange(budgets[1].TotalMilliseconds, 16.0, 200.0);
    }

    [Fact]
    public void ANegativeBacklogCannotProduceABudgetBelowTheMinimum()
    {
        var budgets = WithServerFlag(false, () => new[]
        {
            GameThread.GetDrainBudget(-1).Value,
            GameThread.GetDrainBudget(0).Value,
        });

        Assert.Equal(budgets[1], budgets[0]);
    }
}
