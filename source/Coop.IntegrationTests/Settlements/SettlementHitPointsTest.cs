using Coop.Core.Server.Services.Settlements.Messages;
using Coop.IntegrationTests.Environment;
using Coop.IntegrationTests.Environment.Instance;
using GameInterface.Services.Settlements.Messages;

using TaleWorlds.CampaignSystem.Settlements;
namespace Coop.IntegrationTests.Settlements;
public class SettlementHitPointsTest
{
    internal TestEnvironment TestEnvironment { get; } = new TestEnvironment();

    /// <summary>
    /// Used to Test that client recieves SettlementHitPoints messsages.
    /// </summary>
    [Fact]
    public void ServerSettlementHitPointsChanged_Publishes_AllClients()
    {
        // Arrange
        var settlement = TestEnvironment.Server.CreateRegisteredObject<Settlement>("settlement1");
        foreach (var client in TestEnvironment.Clients)
        {
            client.CreateRegisteredObject<Settlement>("settlement1");
        }

        var triggerMessage = new SettlementChangedSettlementHitPoints(settlement, 100f);

        var server = TestEnvironment.Server;

        // Act
        server.SimulateMessage(this, triggerMessage);

        // Assert
        // Verify the server sends a single message to it's game interface
        var sent = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkChangeSettlementHitPoints>());
        Assert.Equal(100f, sent.SettlementHitPoints);

        // Verify the all clients send a single message to their game interfaces
        foreach (EnvironmentInstance client in TestEnvironment.Clients)
        {
            var received = Assert.Single(client.InternalMessages.GetMessages<ChangeSettlementHitPoints>());
            Assert.Equal(100f, received.SettlementHitPoints);
        }
    }
}
