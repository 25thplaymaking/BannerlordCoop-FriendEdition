using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Moq;
using ProtoBuf;
using System;
using System.IO;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public class CaptureDefeatedEnemyTests
{
    [Theory]
    [InlineData(true, BattleState.AttackerVictory)]
    [InlineData(false, BattleState.DefenderVictory)]
    public void EligibleNpcFieldBattle_ConcludesForRequestingPlayerSide(
        bool playerIsAttacker,
        BattleState expected)
    {
        var state = ValidState(playerIsAttacker);

        Assert.True(CaptureDefeatedEnemyValidator.TryGetWinner(state, out var winner));
        Assert.Equal(expected, winner);
    }

    [Theory]
    [InlineData(false, false, false, true, 1, false, false, false)]
    [InlineData(true, true, false, true, 1, false, false, false)]
    [InlineData(true, false, true, true, 1, false, false, false)]
    [InlineData(true, false, false, false, 1, false, false, false)]
    [InlineData(true, false, false, true, 0, false, false, false)]
    [InlineData(true, false, false, true, 1, true, false, false)]
    [InlineData(true, false, false, true, 1, false, true, false)]
    [InlineData(true, false, false, true, 1, false, false, true)]
    public void UnsafeOrUnfinishedBattle_IsRejected(
        bool isFieldBattle,
        bool hasSettlement,
        bool isFinalized,
        bool friendlyHasHealthyMember,
        int enemyPartyCount,
        bool enemyHasSettlement,
        bool enemyHasPlayerParty,
        bool enemyHasHealthyMember)
    {
        var state = new CaptureDefeatedEnemyState(
            isFieldBattle,
            hasSettlement,
            isFinalized,
            BattleState.None,
            playerIsAttacker: true,
            playerIsDefender: false,
            friendlyHasHealthyMember,
            enemyPartyCount,
            enemyHasSettlement,
            enemyHasPlayerParty,
            enemyHasHealthyMember);

        Assert.False(CaptureDefeatedEnemyValidator.TryGetWinner(state, out _));
    }

    [Fact]
    public void AlreadyConcludedBattle_IsRejected()
    {
        var state = ValidState(
            playerIsAttacker: true,
            battleState: BattleState.AttackerVictory);

        Assert.False(CaptureDefeatedEnemyValidator.TryGetWinner(state, out _));
    }

    [Fact]
    public void Request_SerializesOnlyAuthoritativeMapEventIdentity()
    {
        var original = new NetworkCaptureDefeatedEnemy("MapEvent_Created_42");
        using var stream = new MemoryStream();

        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var returned = Serializer.Deserialize<NetworkCaptureDefeatedEnemy>(stream);

        Assert.Equal(original.MapEventId, returned.MapEventId);
    }

    [Fact]
    public void ClientAttempt_SendsTypedMapEventRequest()
    {
        Action<MessagePayload<CaptureDefeatedEnemyAttempted>> subscriber = null;
        var broker = new Mock<IMessageBroker>();
        broker
            .Setup(value => value.Subscribe(
                It.IsAny<Action<MessagePayload<CaptureDefeatedEnemyAttempted>>>()!))
            .Callback<Action<MessagePayload<CaptureDefeatedEnemyAttempted>>>(value => subscriber = value);

        var network = new Mock<INetwork>();
        IMessage sent = null;
        network
            .Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sent = message);
        var objectManager = new Mock<IObjectManager>();
        var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
        var playerParty = ObjectHelper.SkipConstructor<MobileParty>();
        string mapEventId = "MapEvent_Created_42";
        string playerPartyId = "MobileParty_Player_7";
        objectManager
            .Setup(value => value.TryGetIdWithLogging(mapEvent, out mapEventId))
            .Returns(true);
        objectManager
            .Setup(value => value.TryGetIdWithLogging(playerParty, out playerPartyId))
            .Returns(true);

        bool previousServer = ModInformation.IsServer;
        ModInformation.IsServer = false;
        try
        {
            using var handler = new MapEventHandler(
                broker.Object,
                network.Object,
                objectManager.Object,
                new Mock<IMapEventLogger>().Object,
                new Mock<IBattleHostRegistry>().Object,
                new Mock<IPlayerManager>().Object);

            Assert.NotNull(subscriber);
            subscriber(new MessagePayload<CaptureDefeatedEnemyAttempted>(
                this,
                new CaptureDefeatedEnemyAttempted(mapEvent, playerParty)));

            var request = Assert.IsType<NetworkCaptureDefeatedEnemy>(sent);
            Assert.Equal(mapEventId, request.MapEventId);
            network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Once);
        }
        finally
        {
            ModInformation.IsServer = previousServer;
        }
    }

    private static CaptureDefeatedEnemyState ValidState(
        bool playerIsAttacker,
        BattleState battleState = BattleState.None) => new(
        isFieldBattle: true,
        hasSettlement: false,
        isFinalized: false,
        battleState,
        playerIsAttacker,
        playerIsDefender: !playerIsAttacker,
        friendlyHasHealthyMember: true,
        enemyPartyCount: 1,
        enemyHasSettlement: false,
        enemyHasPlayerParty: false,
        enemyHasHealthyMember: false);
}
