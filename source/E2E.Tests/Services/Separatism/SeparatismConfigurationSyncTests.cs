using Common.Messaging;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Separatism;

public sealed class SeparatismConfigurationSyncTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;

    public SeparatismConfigurationSyncTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void CampaignReady_BroadcastsHostSeparatismConfigurationToEveryClient()
    {
        // Invoke the config handler directly: publishing CampaignReady would also execute unrelated
        // engine-backed difficulty/UI handlers and turn this networking test into an engine smoke test.
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));

        var sent = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkLoadModConfig>());
        Assert.True(sent.ModOptions.Separatism.Enabled);
        Assert.True(sent.ModOptions.Separatism.LordRebellionsEnabled);
        Assert.True(sent.ModOptions.Separatism.NationalRebellionsEnabled);

        foreach (var client in TestEnvironment.Clients)
        {
            var received = Assert.Single(client.InternalMessages.GetMessages<NetworkLoadModConfig>());
            Assert.Equal(sent.ModOptions.Separatism, received.ModOptions.Separatism);
        }
    }

    [Fact]
    public void LateClientRequest_ReceivesTheCurrentHostSeparatismConfiguration()
    {
        Server.Call(() => ModConfigProvider.LoadModConfig(new ModOptionsData
        {
            Separatism = new SeparatismOptionsData
            {
                ChaosStartEnabled = false,
                DailyLordRebellionChance = 0.42f,
                SettlementRebellionsEnabled = true,
            },
        }));

        var client = TestEnvironment.Clients.First();
        Server.SimulateMessage(client.NetPeer, new NetworkRequestServerModConfig());

        var sent = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkLoadModConfig>());
        Assert.False(sent.ModOptions.Separatism.ChaosStartEnabled);
        Assert.Equal(0.42f, sent.ModOptions.Separatism.DailyLordRebellionChance);
        Assert.True(sent.ModOptions.Separatism.SettlementRebellionsEnabled);
    }
}
