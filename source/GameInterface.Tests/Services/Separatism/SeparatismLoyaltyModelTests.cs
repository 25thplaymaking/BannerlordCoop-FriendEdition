using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.Separatism;
using System;
using TaleWorlds.CampaignSystem.GameComponents;
using Xunit;

namespace GameInterface.Tests.Services.Separatism;

public sealed class SeparatismLoyaltyModelTests : IDisposable
{
    private readonly ModOptions originalOptions = ModConfigProvider.ModOptions;

    public void Dispose() => ModConfigProvider.ModOptions = originalOptions;

    [Fact]
    public void EnabledModel_UsesTheHostStartAndRecoveryThresholds()
    {
        ModConfigProvider.ModOptions = new ModOptions(new ModOptionsData
        {
            Separatism = new SeparatismOptionsData
            {
                Enabled = true,
                SettlementRebellionStartLoyaltyThreshold = 17,
                SettlementRebellionEndLoyaltyThreshold = 43,
            },
        });

        var model = ObjectHelper.SkipConstructor<SeparatismSettlementLoyaltyModel>();

        Assert.Equal(17, model.RebellionStartLoyaltyThreshold);
        Assert.Equal(43, model.RebelliousStateStartLoyaltyThreshold);
    }

    [Fact]
    public void DisabledModel_PreservesTheNativeThresholds()
    {
        ModConfigProvider.ModOptions = new ModOptions(new ModOptionsData
        {
            Separatism = new SeparatismOptionsData { Enabled = false },
        });

        var native = ObjectHelper.SkipConstructor<DefaultSettlementLoyaltyModel>();
        var model = ObjectHelper.SkipConstructor<SeparatismSettlementLoyaltyModel>();

        Assert.Equal(native.RebellionStartLoyaltyThreshold, model.RebellionStartLoyaltyThreshold);
        Assert.Equal(native.RebelliousStateStartLoyaltyThreshold, model.RebelliousStateStartLoyaltyThreshold);
    }
}
