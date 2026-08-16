using Common.Messaging;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Common.Tests.Utils;
using TaleWorlds.CampaignSystem;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Separatism;

public sealed class SeparatismConfigurationSyncTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;

    public SeparatismConfigurationSyncTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
        foreach (var client in TestEnvironment.Clients)
        {
            client.Call(() => Assert.True(
                client.Resolve<IModConfigAuthority>().TryBindTrustedServer(
                    Server.NetPeer,
                    out var failure),
                failure));
        }
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
        Assert.True(sent.Snapshot.ModOptions.Separatism.Enabled);
        Assert.True(sent.Snapshot.ModOptions.Separatism.LordRebellionsEnabled);
        Assert.True(sent.Snapshot.ModOptions.Separatism.NationalRebellionsEnabled);
        Assert.True(sent.Snapshot.BirthAndDeathEnabled);
        Assert.True(ModConfigSnapshotCodec.TryValidate(sent.Snapshot, out var failure), failure);

        foreach (var client in TestEnvironment.Clients)
        {
            var received = Assert.Single(client.InternalMessages.GetMessages<NetworkLoadModConfig>());
            Assert.Equal(sent.Snapshot.ModOptions.Separatism, received.Snapshot.ModOptions.Separatism);
        }
    }

    [Fact]
    public void LateClientRequest_ReceivesTheCurrentHostSeparatismConfiguration()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        Server.NetworkSentMessages.Clear();

        var client = TestEnvironment.Clients.First();
        Server.Call(() =>
        {
            var players = Server.Resolve<IPlayerManager>();
            Assert.True(players.AddPlayer(new Player(
                "config-client",
                "config-hero",
                "config-party",
                "config-clan",
                "config-character")));
            players.SetPeer("config-client", client.NetPeer);
        });
        ModConfigSnapshot accepted = null;
        Server.Call(() => Assert.True(
            Server.Resolve<IModConfigAuthority>().TryGetCurrent(out accepted)));
        Server.SimulateMessage(client.NetPeer, new NetworkRequestServerModConfig(HeaderFor(accepted, 1)));

        var sent = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkModConfigQueryResult>());
        Assert.Equal(AuthorityResultStatus.Accepted, sent.Status);
        Assert.True(sent.Snapshot.ModOptions.Separatism.ChaosStartEnabled);
        Assert.Equal(1f, sent.Snapshot.ModOptions.Separatism.DailyLordRebellionChance);
        Assert.False(sent.Snapshot.ModOptions.Separatism.SettlementRebellionsEnabled);
    }

    [Fact]
    public void HostOptionAttempt_CannotDisableBirthAndDeathAfterConfigAttestation()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            CampaignOptions.IsLifeDeathCycleDisabled = true;
            Assert.True(CampaignOptions.IsLifeDeathCycleDisabled);
            Server.Resolve<TestMessageBroker>().Publish(this, new UpdateCampaignOptions());
            Assert.False(CampaignOptions.IsLifeDeathCycleDisabled);
        });

        var sent = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkUpdateCampaignOptions>());
        Assert.False(sent.IsLifeDeathCycleDisabled);
    }

    [Fact]
    public void ClientCannotOverwriteServerBirthAndDeathThroughCampaignOptionsWireMessage()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        var client = TestEnvironment.Clients.First();
        var forged = CampaignOptionsMessage(isLifeDeathCycleDisabled: true);

        Server.Call(() =>
        {
            CampaignOptions.IsLifeDeathCycleDisabled = false;
        });
        Server.SimulateMessage(client.NetPeer, forged);

        Server.Call(() => Assert.False(CampaignOptions.IsLifeDeathCycleDisabled));
        Assert.Equal(LiteNetLib.ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
    }

    [Fact]
    public void ClientCannotTriggerServerOwnedCampaignOptionsBroadcast()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        Server.NetworkSentMessages.Clear();
        var client = TestEnvironment.Clients.First();

        Server.SimulateMessage(client.NetPeer, new UpdateCampaignOptions());

        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkUpdateCampaignOptions>());
        Assert.Equal(LiteNetLib.ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
    }

    [Fact]
    public void UnmappedClientRequest_IsIgnoredWithoutDisconnectAndServedOnceMapped()
    {
        // A joining client fires this request when character creation finishes, before the server
        // has unpacked its hero transfer and mapped the peer. Disconnecting here killed every
        // first join, so the unmapped request must be dropped while the connection survives.
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        Server.NetworkSentMessages.Clear();
        var client = TestEnvironment.Clients.First();
        ModConfigSnapshot accepted = null;
        Server.Call(() => Assert.True(
            Server.Resolve<IModConfigAuthority>().TryGetCurrent(out accepted)));

        Server.SimulateMessage(client.NetPeer, new NetworkRequestServerModConfig(HeaderFor(accepted, 1)));

        var unauthorized = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkModConfigQueryResult>());
        Assert.Equal(AuthorityResultStatus.Unauthorized, unauthorized.Status);
        Assert.NotEqual(LiteNetLib.ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);

        Server.Call(() =>
        {
            var players = Server.Resolve<IPlayerManager>();
            Assert.True(players.AddPlayer(new Player(
                "joining-client",
                "joining-hero",
                "joining-party",
                "joining-clan",
                "joining-character")));
            players.SetPeer("joining-client", client.NetPeer);
        });
        Server.SimulateMessage(client.NetPeer, new NetworkRequestServerModConfig(HeaderFor(accepted, 1)));

        Assert.Contains(Server.NetworkSentMessages.GetMessages<NetworkModConfigQueryResult>(),
            result => result.Status == AuthorityResultStatus.Accepted);
        Assert.NotEqual(LiteNetLib.ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
    }

    [Fact]
    public void ClientCannotPushModConfigEnvelopeOntoServer()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        var client = TestEnvironment.Clients.First();
        ModConfigSnapshot accepted = null;
        Server.Call(() => Assert.True(
            Server.Resolve<IModConfigAuthority>().TryGetCurrent(out accepted)));

        Server.SimulateMessage(client.NetPeer, new NetworkLoadModConfig(accepted));

        Assert.Equal(LiteNetLib.ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
        Server.Call(() =>
        {
            Assert.True(Server.Resolve<IModConfigAuthority>().TryGetCurrent(out var current));
            Assert.Equal(accepted.Sha256, current.Sha256);
        });
    }

    [Fact]
    public void ClientCampaignOptions_AcceptOnlyPinnedServerTransport()
    {
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
        var clients = TestEnvironment.Clients.ToArray();
        var client = clients[0];
        var forgedPeer = clients[1].NetPeer;
        var realistic = CampaignOptionsMessage(
            isLifeDeathCycleDisabled: false,
            primaryDifficulty: CampaignOptions.Difficulty.Realistic);

        client.Call(() => CampaignOptions.PlayerTroopsReceivedDamage = CampaignOptions.Difficulty.Easy);
        client.SimulateMessage(null, realistic);
        client.Call(() => Assert.Equal(
            CampaignOptions.Difficulty.Easy,
            CampaignOptions.PlayerTroopsReceivedDamage));

        client.SimulateMessage(Server.NetPeer, realistic);
        client.Call(() => Assert.Equal(
            CampaignOptions.Difficulty.Realistic,
            CampaignOptions.PlayerTroopsReceivedDamage));

        var veryEasy = CampaignOptionsMessage(
            isLifeDeathCycleDisabled: false,
            primaryDifficulty: CampaignOptions.Difficulty.VeryEasy);
        client.SimulateMessage(forgedPeer, veryEasy);
        client.Call(() => Assert.Equal(
            CampaignOptions.Difficulty.Realistic,
            CampaignOptions.PlayerTroopsReceivedDamage));
        Assert.Equal(LiteNetLib.ConnectionState.ShutdownRequested, forgedPeer.ConnectionState);
    }

    private static NetworkUpdateCampaignOptions CampaignOptionsMessage(
        bool isLifeDeathCycleDisabled,
        CampaignOptions.Difficulty primaryDifficulty = CampaignOptions.Difficulty.VeryEasy) =>
        new(
            false,
            primaryDifficulty,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            isLifeDeathCycleDisabled,
            CampaignOptions.Difficulty.VeryEasy,
            CampaignOptions.Difficulty.VeryEasy,
            false,
            CampaignOptions.Difficulty.VeryEasy);

    private static AuthorityRequestHeader HeaderFor(ModConfigSnapshot snapshot, long requestId) =>
        new(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
}
