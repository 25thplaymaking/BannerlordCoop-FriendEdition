using Common;
using Common.Network;
using Common.Network.Messages;
using Common.Tests.Utils;
using Coop.Tests.Mocks;
using Coop.Core.Server.Services.Players.Handlers;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Save.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Players.Messages;
using GameInterface.Services.SiegeEvents.Interfaces;
using HarmonyLib;
using Moq;
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;
using Xunit;

namespace Coop.Tests.Server.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public class PlayerPartyVisibilityHandlerTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly Campaign? previousCampaign;
    private readonly Type sandBoxViewSubModuleType;
    private readonly object? previousSandBoxViewSubModule;

    public PlayerPartyVisibilityHandlerTests()
    {
        ModInformation.IsServer = true;
        previousCampaign = Campaign.Current;

        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        campaign.CampaignEventDispatcher = new CampaignEventDispatcher(Array.Empty<CampaignEventReceiver>());
        Campaign.Current = campaign;

        sandBoxViewSubModuleType = Type.GetType(
            "SandBox.View.SandBoxViewSubModule, SandBox.View",
            throwOnError: true)!;
        var visualManagerType = Type.GetType(
            "SandBox.View.SandBoxViewVisualManager, SandBox.View",
            throwOnError: true)!;
        var instanceField = AccessTools.Field(sandBoxViewSubModuleType, "_instance");
        previousSandBoxViewSubModule = instanceField.GetValue(null);

        var subModule = FormatterServices.GetUninitializedObject(sandBoxViewSubModuleType);
        AccessTools.Field(sandBoxViewSubModuleType, "_sandBoxViewVisualManager")
            .SetValue(subModule, Activator.CreateInstance(visualManagerType));
        instanceField.SetValue(null, subModule);
    }

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
        Campaign.Current = previousCampaign;
        AccessTools.Field(sandBoxViewSubModuleType, "_instance")
            .SetValue(null, previousSandBoxViewSubModule);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedPlayerRegistrationsRestored_PersistsOfflinePartyAsInactiveAndHidden(bool isActive)
    {
        var player = CreatePlayer();
        var party = CreateParty(isActive);
        var playerManager = new Mock<IPlayerManager>();
        playerManager.SetupGet(manager => manager.Players).Returns(new[] { player });
        playerManager.Setup(manager => manager.IsConnected(player)).Returns(false);

        var objectManager = new Mock<IObjectManager>();
        objectManager
            .Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party))
            .Returns(true);

        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            Mock.Of<INetwork>(),
            Mock.Of<ISiegeEventInterface>(),
            Mock.Of<IPlayerClanMembershipService>());

        broker.Publish(this, new SavedPlayerRegistrationsRestored());

        Assert.False(party.IsActive);
        Assert.False(party.IsVisible);
    }

    [Fact]
    public void SavedPlayerRegistrationsRestored_LeavesConnectedPartyActive()
    {
        var player = CreatePlayer();
        var party = CreateParty();
        var playerManager = new Mock<IPlayerManager>();
        playerManager.SetupGet(manager => manager.Players).Returns(new[] { player });
        playerManager.Setup(manager => manager.IsConnected(player)).Returns(true);

        var objectManager = new Mock<IObjectManager>();
        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            Mock.Of<INetwork>(),
            Mock.Of<ISiegeEventInterface>(),
            Mock.Of<IPlayerClanMembershipService>());

        broker.Publish(this, new SavedPlayerRegistrationsRestored());

        Assert.True(party.IsActive);
        objectManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void SavedPlayerRegistrationsRestored_LeavesSiegeBeforeParking()
    {
        var player = CreatePlayer();
        var party = CreateParty();
        party._besiegerCamp = (BesiegerCamp)FormatterServices.GetUninitializedObject(typeof(BesiegerCamp));

        var playerManager = new Mock<IPlayerManager>();
        playerManager.SetupGet(manager => manager.Players).Returns(new[] { player });
        playerManager.Setup(manager => manager.IsConnected(player)).Returns(false);

        var objectManager = new Mock<IObjectManager>();
        objectManager
            .Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party))
            .Returns(true);

        var siegeEventInterface = new Mock<ISiegeEventInterface>();
        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            Mock.Of<INetwork>(),
            siegeEventInterface.Object,
            Mock.Of<IPlayerClanMembershipService>());

        broker.Publish(this, new SavedPlayerRegistrationsRestored());

        siegeEventInterface.Verify(value => value.BreakSiegeForPartyOnly(party), Times.Once);
        Assert.False(party.IsActive);
    }

    [Fact]
    public void PlayerDisconnected_EmbeddedMember_DoesNotParkLeaderParty()
    {
        var peer = new TestNetwork().CreatePeer();
        var player = new Player(
            "Member", "Hero_Member", "Party_Leader", "Clan_Joined", "Character_Member",
            "Clan_Personal", PlayerClanMembershipMode.Embedded);
        var party = CreateParty();
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(peer, out player)).Returns(true);
        var objectManager = new Mock<IObjectManager>();
        objectManager.Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party)).Returns(true);
        var membership = new Mock<IPlayerClanMembershipService>();
        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            Mock.Of<INetwork>(),
            Mock.Of<ISiegeEventInterface>(),
            membership.Object);

        broker.Publish(this, new PlayerDisconnected(peer, default));

        Assert.True(party.IsActive);
        Assert.True(party.IsVisible);
        playerManager.Verify(manager => manager.ClearPeer(peer), Times.Once);
        membership.VerifyNoOtherCalls();
    }

    [Fact]
    public void PlayerDisconnected_ClanLeader_SeparatesEmbeddedMembersBeforeParking()
    {
        var peer = new TestNetwork().CreatePeer();
        var leader = new Player("Leader", "Hero_Leader", "Party_Leader", "Clan_Joined", "Character_Leader");
        var member = new Player(
            "Member", "Hero_Member", "Party_Leader", "Clan_Joined", "Character_Member",
            "Clan_Personal", PlayerClanMembershipMode.Embedded);
        var separated = new Player(
            "Member", "Hero_Member", "Party_Member", "Clan_Joined", "Character_Member",
            "Clan_Personal", PlayerClanMembershipMode.IndependentParty, true);
        var party = CreateParty();
        var playerManager = new Mock<IPlayerManager>();
        playerManager.SetupGet(manager => manager.Players).Returns(new[] { leader, member });
        playerManager.Setup(manager => manager.TryGetPlayer(peer, out leader)).Returns(true);
        var objectManager = new Mock<IObjectManager>();
        objectManager.Setup(manager => manager.TryGetObjectWithLogging(leader.MobilePartyId, out party)).Returns(true);
        var membership = new Mock<IPlayerClanMembershipService>();
        membership.Setup(service => service.TrySeparate(member, true, out separated)).Returns(true);
        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            Mock.Of<INetwork>(),
            Mock.Of<ISiegeEventInterface>(),
            membership.Object);

        broker.Publish(this, new PlayerDisconnected(peer, default));

        Assert.True(SpinWait.SpinUntil(
            () => !party.IsActive && !party.IsVisible,
            TimeSpan.FromSeconds(5)));
        Assert.Single(membership.Invocations, invocation =>
            invocation.Method.Name == nameof(IPlayerClanMembershipService.TrySeparate));
        Assert.False(party.IsActive);
        Assert.False(party.IsVisible);
    }

    [Fact]
    public void PlayerCampaignEntered_EmergencyMember_NotifiesAndClearsFlag()
    {
        var peer = new TestNetwork().CreatePeer();
        var member = new Player(
            "Member", "Hero_Member", "Party_Member", "Clan_Joined", "Character_Member",
            "Clan_Personal", PlayerClanMembershipMode.IndependentParty, true);
        var leader = new Player("Leader", "Hero_Leader", "Party_Leader", "Clan_Joined", "Character_Leader");
        var party = CreateParty();
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(peer, out member)).Returns(true);
        playerManager.Setup(manager => manager.IsConnected(leader)).Returns(true);
        playerManager.Setup(manager => manager.TryGetPeer(member.ControllerId, out peer)).Returns(true);
        playerManager.Setup(manager => manager.ReplacePlayer(member, It.IsAny<Player>())).Returns(true);
        var objectManager = new Mock<IObjectManager>();
        objectManager.Setup(manager => manager.TryGetObjectWithLogging(member.MobilePartyId, out party)).Returns(true);
        var membership = new Mock<IPlayerClanMembershipService>();
        membership.Setup(service => service.TryGetClanLeader(member, out leader)).Returns(true);
        var network = new Mock<INetwork>();
        var broker = new TestMessageBroker();
        using var handler = new PlayerPartyVisibilityHandler(
            broker,
            playerManager.Object,
            objectManager.Object,
            network.Object,
            Mock.Of<ISiegeEventInterface>(),
            membership.Object);

        broker.Publish(this, new PlayerCampaignEntered(peer));

        Assert.True(SpinWait.SpinUntil(
            () => network.Invocations.Any(invocation => invocation.Method.Name == nameof(INetwork.Send)),
            TimeSpan.FromSeconds(5)));
        network.Verify(value => value.Send(peer, It.IsAny<ClanLeaderReturnedNotification>()), Times.Once);
        playerManager.Verify(manager => manager.ReplacePlayer(
            member,
            It.Is<Player>(replacement => !replacement.EmergencyDetached)), Times.Once);
    }

    private static Player CreatePlayer() =>
        new Player("PlayerOne", "Hero_One", "Party_One", "Clan_One", "Character_One");

    private static MobileParty CreateParty(bool isActive = true)
    {
        var party = (MobileParty)FormatterServices.GetUninitializedObject(typeof(MobileParty));
        party.Party = (PartyBase)FormatterServices.GetUninitializedObject(typeof(PartyBase));
        party.Party.MobileParty = party;
        party.IsActive = isActive;
        party._isVisible = true;
        return party;
    }
}
