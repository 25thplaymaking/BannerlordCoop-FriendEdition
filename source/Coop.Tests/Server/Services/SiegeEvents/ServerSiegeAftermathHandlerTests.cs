using Common.Tests.Utils;
using Coop.Core.Server.Services.SiegeEvents.Handlers;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using Coop.Tests.Mocks;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Players;
using GameInterface.Configuration;
using GameInterface.Services.SiegeEvents.Interfaces;
using Moq;
using System.Linq;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace Coop.Tests.Server.Services.SiegeEvents;

public sealed class ServerSiegeAftermathHandlerTests
{
    [Fact]
    public void EnteringPeer_ReceivesPendingAftermathPromptSnapshot()
    {
        var broker = new TestMessageBroker();
        var network = new TestNetwork();
        var peer = network.CreatePeer();
        var settlement = CreateUninitialized<Settlement>();
        var leaderParty = CreateUninitialized<MobileParty>();
        var objectManager = new Mock<IObjectManager>();
        var playerManager = new Mock<IPlayerManager>();
        var configAuthority = new Mock<IModConfigAuthority>();
        var siegeEventInterface = new Mock<ISiegeEventInterface>();
        string settlementId = "settlement-1";
        string leaderPartyId = "leader-party-1";
        objectManager.Setup(manager => manager.TryGetIdWithLogging(settlement, out settlementId)).Returns(true);
        objectManager.Setup(manager => manager.TryGetIdWithLogging(leaderParty, out leaderPartyId)).Returns(true);
        siegeEventInterface.Setup(service => service.GetPendingSiegeAftermathPrompts())
            .Returns(new[] { new PendingSiegeAftermathPrompt(leaderParty, settlement) });
        using var router = new AuthorityRequestRouter(broker, network, playerManager.Object);
        using var handler = new ServerSiegeAftermathHandler(
            broker, network, objectManager.Object, playerManager.Object, siegeEventInterface.Object,
            configAuthority.Object, router);

        handler.SendPendingAftermathPrompts(peer);

        var prompt = Assert.Single(network.GetPeerMessages(peer).OfType<NetworkPromptSiegeAftermathChoice>());
        Assert.Equal(settlementId, prompt.SettlementId);
        Assert.Equal(leaderPartyId, prompt.LeaderPartyId);
    }

#pragma warning disable SYSLIB0050 // Identity-only test doubles; no native constructors are invoked.
    private static T CreateUninitialized<T>() where T : class =>
        (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
}
