using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Separatism;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Separatism;

public sealed class SeparatismTransitionTests
{
    [Fact]
    public void DailyClanTransition_StopsAfterTheFirstCommittedMutation()
    {
        int anarchyAttempts = 0;

        SeparatismCampaignService.RunDailyClanTransitions(
            tryLordOrFloatingTitle: () => true,
            tryAnarchy: () => anarchyAttempts++);

        Assert.Equal(0, anarchyAttempts);
    }

    [Fact]
    public void DailyClanTransition_TriesAnarchyWhenTheFirstPathMakesNoChange()
    {
        int anarchyAttempts = 0;

        SeparatismCampaignService.RunDailyClanTransitions(
            tryLordOrFloatingTitle: () => false,
            tryAnarchy: () => anarchyAttempts++);

        Assert.Equal(1, anarchyAttempts);
    }

    [Fact]
    public void MissingPreCampaignConfig_FallsBackWithoutThrowing()
    {
        var config = new Mock<IModConfig>();
        config.SetupGet(value => value.Data).Returns((ModConfigData)null!);
        var service = new SeparatismCampaignService(
            Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(),
            Mock.Of<IObjectManager>(),
            Mock.Of<IKingdomMembershipState>(),
            Mock.Of<IPlayerManager>(),
            config.Object);

        var exception = Record.Exception(service.OnNewGameCreated);

        Assert.Null(exception);
    }
}
