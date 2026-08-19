using GameInterface.Services.MobilePartyAIs.Patches;
using Xunit;

namespace GameInterface.Tests.Services.MobilePartyAIs;

/// <summary>
/// The AI think batch is the only thing deciding how often a party re-evaluates its behaviour on
/// the host, so its scaling is worth pinning.
/// </summary>
public class PartiesThinkPatchTests
{
    [Fact]
    public void SmallWorldsKeepTheOriginalBatch()
    {
        // Native Calradia sits well under the floor, and behaved correctly on the flat 100.
        Assert.Equal(100, PartiesThinkPatch.GetUpdatesPerTick(600));
        Assert.Equal(100, PartiesThinkPatch.GetUpdatesPerTick(1));
    }

    [Fact]
    public void ALargeWorldSweepsInABoundedNumberOfBatches()
    {
        // Europe 1100 runs ~4113 parties. On the old flat 100 a party thought once every ~4.1s;
        // scaling keeps a full sweep inside 20 batches (~2s) instead.
        int updates = PartiesThinkPatch.GetUpdatesPerTick(4113);

        Assert.True(updates > 100, "a 4113-party world must not be stuck on the small-world batch");
        Assert.True(4113 <= updates * 20, "a full sweep must complete within the sweep budget");
    }

    [Fact]
    public void APathologicalPartyCountCannotRunAwayWithTheGameThread()
    {
        Assert.Equal(400, PartiesThinkPatch.GetUpdatesPerTick(1_000_000));
    }

    [Fact]
    public void AnEmptyWorldDoesNotReturnAZeroOrNegativeBatch()
    {
        Assert.Equal(100, PartiesThinkPatch.GetUpdatesPerTick(0));
        Assert.Equal(100, PartiesThinkPatch.GetUpdatesPerTick(-5));
    }
}
