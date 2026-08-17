using GameInterface.Services.Inventory;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.Inventory;

public sealed class BlockedTradeTeardownTests : IDisposable
{
    public BlockedTradeTeardownTests() => BlockedTradeTeardown.Reset();

    public void Dispose() => BlockedTradeTeardown.Reset();

    [Fact]
    public void NotInProgressUntilEntered()
    {
        Assert.False(BlockedTradeTeardown.InProgress);

        BlockedTradeTeardown.Enter();
        Assert.True(BlockedTradeTeardown.InProgress);

        BlockedTradeTeardown.Exit();
        Assert.False(BlockedTradeTeardown.InProgress);
    }

    [Fact]
    public void NestedEntriesUnwindExactly()
    {
        BlockedTradeTeardown.Enter();
        BlockedTradeTeardown.Enter();
        BlockedTradeTeardown.Exit();

        Assert.True(BlockedTradeTeardown.InProgress);

        BlockedTradeTeardown.Exit();
        Assert.False(BlockedTradeTeardown.InProgress);
    }

    /// <summary>
    /// An unbalanced Exit must not push the depth negative — that would leave the guard reading
    /// "not in progress" one Enter too early on the next accept and let the recursion back in.
    /// </summary>
    [Fact]
    public void ExitWithoutEnterCannotGoNegative()
    {
        BlockedTradeTeardown.Exit();
        BlockedTradeTeardown.Exit();

        BlockedTradeTeardown.Enter();
        Assert.True(BlockedTradeTeardown.InProgress);

        BlockedTradeTeardown.Exit();
        Assert.False(BlockedTradeTeardown.InProgress);
    }

    /// <summary>
    /// Reproduces the shape of the live crash. DoneLogic publishes, the subscriber answers a blocked
    /// completion by closing the screen, and TaleWorlds' CloseScreen calls DoneLogic again
    /// (CloseScreen -> CloseInventoryPresentation -> DoneLogic, confirmed in the shipped IL). Without
    /// the guard this recursed 892 deep on the player's machine and overflowed the stack; with it the
    /// nested call is answered and exactly one completion is published.
    /// </summary>
    [Fact]
    public void BlockedCompletion_PublishesOnceAndTerminates()
    {
        int published = 0;
        int depthReached = 0;
        int depth = 0;
        Action doneLogic = null;

        void CloseScreenWhichReentersDoneLogic() => doneLogic();

        doneLogic = () =>
        {
            depth++;
            depthReached = Math.Max(depthReached, depth);
            try
            {
                // The guard the production prefix applies before doing any work.
                if (BlockedTradeTeardown.InProgress) return;
                if (depth > 50) throw new InvalidOperationException("Runaway re-entry — the guard did not hold.");

                BlockedTradeTeardown.Enter();
                try
                {
                    published++;
                    CloseScreenWhichReentersDoneLogic();
                }
                finally
                {
                    BlockedTradeTeardown.Exit();
                }
            }
            finally
            {
                depth--;
            }
        };

        doneLogic();

        Assert.Equal(1, published);
        Assert.Equal(2, depthReached);
        Assert.False(BlockedTradeTeardown.InProgress);
    }

    /// <summary>
    /// A throw inside the dispatch must not strand the marker, which would make every later accept
    /// take the nested-call branch and silently stop closing the screen.
    /// </summary>
    [Fact]
    public void ThrowDuringDispatchStillClearsTheMarker()
    {
        // Explicit Action: a statement lambda whose body always throws is ambiguous against the
        // Func<Task> overload, which xUnit marks obsolete.
        Action dispatchThatThrows = () =>
        {
            BlockedTradeTeardown.Enter();
            try { throw new InvalidOperationException("subscriber failed"); }
            finally { BlockedTradeTeardown.Exit(); }
        };

        Assert.Throws<InvalidOperationException>(dispatchThatThrows);

        Assert.False(BlockedTradeTeardown.InProgress);
    }

    /// <summary>
    /// The guard only helps if the DoneLogic prefix actually consults it. The prefix itself cannot be
    /// unit tested — it needs a live TaleWorlds InventoryLogic — so assert the wiring at the IL level:
    /// removing the guard or the Enter/Exit pair around the publish silently restores the crash.
    /// </summary>
    [Fact]
    public void DoneLogicPrefix_IsWiredToTheGuard()
    {
        Type patches = typeof(BlockedTradeTeardown).Assembly
            .GetType("GameInterface.Services.Inventory.Patches.InventoryLogicPatches", throwOnError: true);
        MethodInfo prefix = patches.GetMethod("DoneLogicPrefix", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(patches.FullName, "DoneLogicPrefix");

        string[] called = CalledMethodNames(prefix).ToArray();

        Assert.Contains("get_InProgress", called);
        Assert.Contains("Enter", called);
        Assert.Contains("Exit", called);
    }

    private static System.Collections.Generic.IEnumerable<string> CalledMethodNames(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue; // call / callvirt
            int token = BitConverter.ToInt32(il, i + 1);
            string name = null;
            try
            {
                MethodBase target = module.ResolveMethod(token);
                if (target?.DeclaringType == typeof(BlockedTradeTeardown)) name = target.Name;
            }
            catch { }
            if (name != null) yield return name;
        }
    }
}
