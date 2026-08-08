using Common.Messaging;
using Common.Network;
using GameInterface.Services.Caravans.Handlers;
using GameInterface.Services.Caravans.Interfaces;
using GameInterface.Services.Caravans.Messages;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.ObjectManager;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Caravans;

public sealed class CaravanNetworkPayloadTests
{
    [Fact]
    public void ExpiredTradeRumorRemoval_NullProtobufMap_IsANoOp()
    {
        Action<MessagePayload<NetworkDeleteExpiredTradeRumorTakenCaravans>> callback = null;
        var broker = new Mock<IMessageBroker>();
        broker
            .Setup(x => x.Subscribe(It.IsAny<Action<MessagePayload<NetworkDeleteExpiredTradeRumorTakenCaravans>>>()))
            .Callback<Action<MessagePayload<NetworkDeleteExpiredTradeRumorTakenCaravans>>>(value => callback = value);

        using var handler = new CaravansCampaignBehaviorHandler(
            broker.Object,
            new Mock<IObjectManager>().Object,
            new Mock<INetwork>().Object,
            new Mock<ISessionCaravansPlayerDataInterface>().Object,
            new Mock<ISessionInteractionsPlayerDataInterface>().Object);

        Assert.NotNull(callback);
        var payload = new MessagePayload<NetworkDeleteExpiredTradeRumorTakenCaravans>(
            this,
            new NetworkDeleteExpiredTradeRumorTakenCaravans(null));

        var exception = Record.Exception(() => callback(payload));

        Assert.Null(exception);
    }
}
