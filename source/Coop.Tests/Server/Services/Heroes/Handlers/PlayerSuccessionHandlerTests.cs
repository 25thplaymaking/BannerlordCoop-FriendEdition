using Common.Tests.Utils;
using Common.Util;
using Coop.Core.Client.Services.Heroes.Messages;
using Coop.Core.Server.Services.Heroes.Handlers;
using Common.Network;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace Coop.Tests.Server.Services.Heroes.Handlers;

public class PlayerSuccessionHandlerTests
{
    private readonly TestMessageBroker messageBroker = new();
    private readonly Mock<INetwork> network = new();
    private readonly Mock<IObjectManager> objectManager = new();
    private readonly Mock<IPlayerManager> playerManager = new();
    private readonly Mock<IPlayerPartyRestorer> playerPartyRestorer = new();
    private readonly PlayerSuccessionHandler handler;

    public PlayerSuccessionHandlerTests()
    {
        handler = new PlayerSuccessionHandler(
            messageBroker,
            network.Object,
            objectManager.Object,
            playerManager.Object,
            playerPartyRestorer.Object);
    }

    private (Hero victim, Hero heir, Player registered, Player restored) SetupSuccession()
    {
        var victim = ObjectHelper.SkipConstructor<Hero>();
        var heir = ObjectHelper.SkipConstructor<Hero>();
        var registered = new Player("ctrl", "victimHero", "party0", "clan1", "char0");
        var restored = new Player("ctrl", "heirHero", "party1", "clan1", "char1");

        var resolved = registered;
        playerManager
            .Setup(manager => manager.TryGetPlayer("ctrl", out resolved))
            .Returns(true);

        var heirId = "heirHero";
        objectManager.Setup(o => o.TryGetIdWithLogging(heir, out heirId)).Returns(true);
        var clanId = "clan1";
        objectManager.Setup(o => o.TryGetIdWithLogging(heir.Clan, out clanId)).Returns(true);
        var charId = "char1";
        objectManager.Setup(o => o.TryGetIdWithLogging(heir.CharacterObject, out charId)).Returns(true);

        var restoredOut = restored;
        playerPartyRestorer
            .Setup(restorer => restorer.TryRestore(
                It.Is<Player>(p => p.ControllerId == "ctrl" && p.HeroId == "heirHero"),
                out restoredOut))
            .Returns(true);

        playerManager.Setup(manager => manager.ReplacePlayer(registered, restored)).Returns(true);

        return (victim, heir, registered, restored);
    }

    [Fact]
    public void PlayerHeroDied_ReplacesRegistrationAndBroadcasts()
    {
        var (victim, heir, registered, restored) = SetupSuccession();
        // Owner offline: the registration still moves to the heir, and no targeted switch is sent.
        NetPeer peer = null;
        playerManager.Setup(manager => manager.TryGetPeer("ctrl", out peer)).Returns(false);

        messageBroker.Publish(this, new PlayerHeroDied(victim, heir, "ctrl"));

        playerManager.Verify(manager => manager.ReplacePlayer(registered, restored), Times.Once);
        network.Verify(n => n.SendAll(
            It.Is<NetworkPlayerRegistrationUpdated>(m => m.Player == restored)), Times.Once);
        network.Verify(n => n.Send(
            It.IsAny<NetPeer>(), It.IsAny<NetworkPlayerHeirSucceeded>()), Times.Never);
    }

    [Fact]
    public void PlayerHeroDied_UnknownController_DoesNothing()
    {
        var victim = ObjectHelper.SkipConstructor<Hero>();
        var heir = ObjectHelper.SkipConstructor<Hero>();
        playerManager
            .Setup(manager => manager.TryGetPlayer("ctrl", out It.Ref<Player>.IsAny))
            .Returns(false);

        messageBroker.Publish(this, new PlayerHeroDied(victim, heir, "ctrl"));

        playerManager.Verify(
            manager => manager.ReplacePlayer(It.IsAny<Player>(), It.IsAny<Player>()), Times.Never);
        network.VerifyNoOtherCalls();
    }
}
