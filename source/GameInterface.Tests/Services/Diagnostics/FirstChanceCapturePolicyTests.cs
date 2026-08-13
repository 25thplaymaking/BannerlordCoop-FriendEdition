using GameInterface.Services.Diagnostics;
using System;
using System.Runtime.InteropServices;
using Xunit;

namespace GameInterface.Tests.Services.Diagnostics;

public sealed class FirstChanceCapturePolicyTests
{
    [Theory]
    [InlineData("at GameInterface.Services.MapEvents.BattleSpawnGate.Open()")]
    [InlineData("at Coop.CoopMod.OnGameStart()")]
    [InlineData("at BannerlordPlayerSettlement.Main.RegisterSubModuleObjects(Boolean isSavedCampaign)")]
    [InlineData("at PlayerSettlementFixes.PlayerSettlementFixesLoader.Initialize()")]
    public void CapturesCoopAndPlayerSettlementRuntimeStacks(string stack)
    {
        Assert.True(FirstChanceCapturePolicy.ShouldCapture(stack));
    }

    [Fact]
    public void IgnoresUnrelatedFrameworkExceptions()
    {
        Assert.False(FirstChanceCapturePolicy.ShouldCapture(
            "at System.Reflection.RuntimeAssembly.GetTypes()"));
    }

    [Fact]
    public void CapturesInvalidOperationAfterItCrossesIntoTheSaveLoader()
    {
        Assert.True(FirstChanceCapturePolicy.ShouldCapture(
            new InvalidOperationException("campaign object is not in a valid state"),
            "at TaleWorlds.SaveSystem.Load.LoadContext.ResolveObjects()"));
    }

    [Fact]
    public void CapturesNativeBoundaryEFailWithoutAManagedStack()
    {
        Assert.True(FirstChanceCapturePolicy.ShouldCapture(
            new COMException("engine callback failed", unchecked((int)0x80004005)),
            string.Empty));
    }
}
