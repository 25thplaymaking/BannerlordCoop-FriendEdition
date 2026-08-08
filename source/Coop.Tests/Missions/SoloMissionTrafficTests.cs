using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using GameInterface.Services.Entity;
using global::Missions;
using Missions.Agents;
using Missions.Agents.Handlers;
using Missions.Services.Network;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Missions;

[Collection("Mission.Current")]
public sealed class SoloMissionTrafficTests
{
    [Fact]
    public void UnreliablePayloadBudget_WithNoRemoteMissionMembers_IsZero()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var config = new Mock<INetworkConfig>();
        var missionContext = new Mock<IMissionContext>();
        config.SetupGet(value => value.IsTunneled).Returns(true);
        missionContext.SetupGet(value => value.ControllersInMission)
            .Returns(Array.Empty<string>());

        using var client = new LiteNetP2PClient(
            config.Object,
            Mock.Of<IRelayNetwork>(),
            missionContext.Object,
            serializer,
            Mock.Of<IMessageBroker>(),
            Mock.Of<IPacketManager>(),
            Mock.Of<IControllerIdProvider>(),
            Mock.Of<ISteamMissionBridge>(),
            new MovementPacketCompressor(serializer));

        Assert.Equal(0, client.GetMaxUnreliablePayloadBytes());
    }

    [Fact]
    public void PollMovement_WithNoViableRecipient_DoesNotScanOwnedAgents()
    {
        using var mission = new Battles.MissionCurrentScope();
        var network = new Mock<IBattleNetwork>();
        var registry = new Mock<INetworkAgentRegistry>();
        var movementBatchSender = new Mock<IMovementBatchSender>();
        network.Setup(value => value.GetMaxUnreliablePayloadBytes()).Returns(0);

        using var handler = new AgentMovementHandler(
            network.Object,
            Mock.Of<IPacketManager>(),
            Mock.Of<IMessageBroker>(),
            registry.Object,
            Mock.Of<IControllerIdProvider>(),
            Mock.Of<IAgentEquipmentApplier>(),
            movementBatchSender.Object,
            Mock.Of<IPuppetMountStateRepairer>(),
            Mock.Of<IAgentVisualActionAccessor>());

        handler.PollMovement(0f);

        registry.Verify(
            value => value.GetAgents(It.IsAny<string>()),
            Times.Never);
        Assert.DoesNotContain(
            movementBatchSender.Invocations,
            invocation => invocation.Method.Name == nameof(IMovementBatchSender.Send));
    }
}
