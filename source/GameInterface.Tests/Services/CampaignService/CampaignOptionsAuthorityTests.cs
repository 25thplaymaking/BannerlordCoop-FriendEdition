using Common;
using GameInterface.Services.CampaignService.Commands;
using GameInterface.Services.CampaignService.Data;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.CampaignService;

public sealed class CampaignOptionsAuthorityTests
{
    [Fact]
    public void ModConfigRequestRateGate_IsPerPeer()
    {
        long now = 100;
        var gate = new ModConfigRequestGate<string>(() => now, minimumInterval: 10);

        Assert.True(gate.TryAccept("client-a"));
        Assert.False(gate.TryAccept("client-a"));
        Assert.True(gate.TryAccept("client-b"));
        now += 10;
        Assert.True(gate.TryAccept("client-a"));
    }

    [Fact]
    public void CampaignOptionsWireShape_RejectsBirthDeathDisableAndUnknownEnum()
    {
        var lifecycleDisabled = Message(
            CampaignOptions.Difficulty.VeryEasy,
            isLifeDeathCycleDisabled: true);
        var invalidEnum = Message(
            (CampaignOptions.Difficulty)999,
            isLifeDeathCycleDisabled: false);

        Assert.False(lifecycleDisabled.TryValidateWireShape(out var lifecycleFailure));
        Assert.Contains("Birth & Death", lifecycleFailure);
        Assert.False(invalidEnum.TryValidateWireShape(out var enumFailure));
        Assert.Contains("enum", enumFailure);
    }

    [Fact]
    public void OtherOptionsWireShape_RejectsNullAndUnknownDifficulty()
    {
        Assert.False(new NetworkUpdateOtherOptions(null).TryValidateWireShape(out _));
        Assert.False(new NetworkUpdateOtherOptions(new ServerOptions(999)).TryValidateWireShape(out _));
        Assert.True(new NetworkUpdateOtherOptions(
            new ServerOptions((int)CampaignOptions.Difficulty.Realistic)).TryValidateWireShape(out _));
    }

    [Fact]
    public void ServerConsole_CannotRequestBirthDeathDisable()
    {
        bool wasServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = true;
            string result = CampaignOptionsCommands.IsLifeDeathCycleDisabledCommand(
                new List<string> { "true" });
            Assert.Contains("cannot be disabled", result);
        }
        finally
        {
            ModInformation.IsServer = wasServer;
        }
    }

    private static NetworkUpdateCampaignOptions Message(
        CampaignOptions.Difficulty difficulty,
        bool isLifeDeathCycleDisabled) =>
        new(
            false,
            difficulty,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            isLifeDeathCycleDisabled,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            false,
            CampaignOptions.Difficulty.VeryEasy);
}
