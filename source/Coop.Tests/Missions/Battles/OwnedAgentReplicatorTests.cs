using Common.Messaging;
using GameInterface.Services.ObjectManager;
using global::Missions;
using global::Missions.Battles;
using Missions.Messages;
using Missions.Services.Network;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Coop.Tests.Missions.Battles;

public sealed class OwnedAgentReplicatorTests
{
    [Fact]
    public void DeploymentCommit_WithNoRemoteMissionMembers_DoesNotScanTheOwnedArmy()
    {
        var network = new Mock<IBattleNetwork>();
        var broker = new Mock<IMessageBroker>();
        var objectManager = new Mock<IObjectManager>();
        var component = new Mock<ICoopMissionComponent>();
        var registry = new Mock<INetworkAgentRegistry>();
        var session = new Mock<IBattleSession>();
        var casualties = new Mock<ICasualtyAttributionMap>();
        var deployment = new Mock<IBattleDeploymentCoordinator>();
        var codec = new Mock<IBattleAgentSpawnBatchCodec>();
        var missionContext = new Mock<IMissionContext>();

        component.SetupGet(value => value.AgentRegistry).Returns(registry.Object);
        session.SetupGet(value => value.OwnControllerId).Returns("us");
        missionContext.SetupGet(value => value.ControllersInMission)
            .Returns(Array.Empty<string>());

        using var replicator = new OwnedAgentReplicator(
            network.Object,
            broker.Object,
            objectManager.Object,
            component.Object,
            session.Object,
            casualties.Object,
            deployment.Object,
            codec.Object,
            missionContext.Object);

        replicator.BroadcastOwnDeployedTroops();

        registry.Verify(value => value.GetAgents(It.IsAny<string>()), Times.Never);
        codec.Verify(
            value => value.Encode(
                It.IsAny<IReadOnlyList<BattleAgentSpawnData>>(),
                It.IsAny<SpawnBatchPurpose>()),
            Times.Never);
    }
}
