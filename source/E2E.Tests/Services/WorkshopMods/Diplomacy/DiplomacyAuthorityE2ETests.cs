using Common.Messaging;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.WorkshopMods.Diplomacy;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Diplomacy;

public sealed class DiplomacyAuthorityE2ETests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;
    private IEnumerable<EnvironmentInstance> Clients => TestEnvironment.Clients;

    public DiplomacyAuthorityE2ETests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void SharedCampaignMutation_IsAllowedOnServer_AndBlockedOnEveryClient()
    {
        Server.Call(() => Assert.True(DiplomacyCompatibilityPolicy.ShouldRunSharedMutation()));
        foreach (var client in Clients)
            client.Call(() => Assert.False(DiplomacyCompatibilityPolicy.ShouldRunSharedMutation()));
    }

    [Fact]
    public void FriendSeparatism_PreventsDiplomacyRebellion_OnServerAndClients()
    {
        Server.Call(() => Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint()));
        foreach (var client in Clients)
            client.Call(() => Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint()));
    }

    [Fact]
    public void LateJoiningClient_RequestsDiplomacySnapshotOnlyAfterHostConfigBarrier()
    {
        var client = Clients.First();
        int before = client.NetworkSentMessages.GetMessages<NetworkRequestDiplomacySnapshot>().Count();
        var accepted = new ModConfigSnapshot(
            new string('e', ModConfigSnapshot.SessionIdLength),
            revision: 1,
            new ModOptions(new ModOptionsData()),
            birthAndDeathEnabled: true);

        client.Call(() => client.Resolve<DiplomacyCompatibilityHandler>().HandleCampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));

        Assert.Equal(
            before,
            client.NetworkSentMessages.GetMessages<NetworkRequestDiplomacySnapshot>().Count());

        // Even a structurally valid accepted-event copy is ignored when it carries a network
        // origin and has not been committed by the client's config authority.
        client.SimulateMessage(
            Server.NetPeer,
            new HostModConfigAccepted(accepted));

        Assert.Equal(
            before,
            client.NetworkSentMessages.GetMessages<NetworkRequestDiplomacySnapshot>().Count());

        client.Call(() =>
        {
            var authority = client.Resolve<IModConfigAuthority>();
            Assert.True(authority.AcceptClientSnapshot(accepted).Succeeded);
            client.Resolve<IMessageBroker>().Publish(this, new HostModConfigAccepted(accepted));
        });

        Assert.Equal(
            before + 1,
            client.NetworkSentMessages.GetMessages<NetworkRequestDiplomacySnapshot>().Count());
    }
}
