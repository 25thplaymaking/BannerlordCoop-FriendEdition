using Common.Messaging;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.WorkshopMods.Core;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopCapabilityE2ETests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;
    private EnvironmentInstance[] Clients => TestEnvironment.Clients.ToArray();

    public WorkshopCapabilityE2ETests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
        foreach (EnvironmentInstance client in Clients)
        {
            client.Call(() => Assert.True(
                client.Resolve<IModConfigAuthority>().TryBindTrustedServer(Server.NetPeer, out var failure),
                failure));
        }
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void ServerBroadcast_ConvergesEveryCapabilityRegistryAfterBothBarriers()
    {
        PrimeServerCapabilities();

        NetworkWorkshopCapabilities sent =
            Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkWorkshopCapabilities>());
        Assert.True(WorkshopCapabilityCodec.TryValidate(sent.Snapshot, out var failure), failure);
        Server.Call(() => Assert.Equal(
            WorkshopCapabilityApplyResult.AlreadyCurrent,
            Server.Resolve<IWorkshopCapabilityRegistry>().Apply(sent.Snapshot)));
        foreach (EnvironmentInstance client in Clients)
        {
            client.Call(() => Assert.Equal(
                WorkshopCapabilityApplyResult.AlreadyCurrent,
                client.Resolve<IWorkshopCapabilityRegistry>().Apply(sent.Snapshot)));
        }
    }

    [Fact]
    public void MappedLateRequest_ReceivesCurrentCapabilitySnapshot()
    {
        PrimeServerCapabilities();
        NetworkWorkshopCapabilities current =
            Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkWorkshopCapabilities>());
        Server.NetworkSentMessages.Clear();
        EnvironmentInstance client = Clients[0];
        Server.Call(() =>
        {
            var players = Server.Resolve<IPlayerManager>();
            Assert.True(players.AddPlayer(new Player(
                "capability-client",
                "capability-hero",
                "capability-party",
                "capability-clan",
                "capability-character")));
            players.SetPeer("capability-client", client.NetPeer);
        });

        Server.SimulateMessage(
            client.NetPeer,
            new NetworkRequestWorkshopCapabilities(new AuthorityRequestHeader(
                current.Snapshot.ProtocolVersion,
                current.Snapshot.SessionId,
                requestId: 1,
                current.Snapshot.Revision)));

        NetworkWorkshopCapabilityQueryResult response =
            Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkWorkshopCapabilityQueryResult>());
        Assert.Equal(AuthorityResultStatus.Accepted, response.Status);
        Assert.Equal(current.Snapshot.Sha256, response.Snapshot.Sha256);
    }

    [Fact]
    public void LocallyPublishedWireSnapshot_CannotReplaceTrustedServerState()
    {
        PrimeServerCapabilities();
        EnvironmentInstance client = Clients[0];
        WorkshopCapabilitySnapshot accepted =
            Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkWorkshopCapabilities>()).Snapshot;
        var conflict = new WorkshopCapabilitySnapshot(
            accepted.SessionId,
            accepted.Revision,
            new[] { new WorkshopCapability("Forged", "Local", true, string.Empty) });

        client.Call(() => client.Resolve<IMessageBroker>().Publish(
            this,
            new NetworkWorkshopCapabilities(conflict)));

        client.Call(() => Assert.Equal(
            WorkshopCapabilityApplyResult.AlreadyCurrent,
            client.Resolve<IWorkshopCapabilityRegistry>().Apply(accepted)));
    }

    private void PrimeServerCapabilities()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        Server.Call(() => Server.Resolve<WorkshopCapabilityHandler>().HandleCampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
    }
}
