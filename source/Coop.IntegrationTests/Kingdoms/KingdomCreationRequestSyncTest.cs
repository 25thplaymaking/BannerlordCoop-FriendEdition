using Common.Messaging;
using Coop.Core.Server.Services.Kingdoms.Messages;
using Coop.IntegrationTests.Environment;
using Coop.IntegrationTests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.Entity;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using TaleWorlds.CampaignSystem;

namespace Coop.IntegrationTests.Kingdoms;

[Collection(KingdomSyncGameThreadCollection.Name)]
public class KingdomCreationRequestSyncTest
{
    internal TestEnvironment TestEnvironment { get; } = new TestEnvironment();

    [Fact]
    public void ClientKingdomCreationRequested_SendsCorrelatedAuthorityRequest()
    {
        var client = TestEnvironment.Clients.First();
        client.Resolve<IControllerIdProvider>().SetControllerId("player1");
        client.Resolve<TestNetworkRouter>().IsMessageRoutingEnabled = false;

        GameThreadTestRunner.Run(() =>
            client.SimulateMessage(this, new KingdomCreationRequested("Real Kingdom", "empire")));

        var request = Assert.Single(client.NetworkSentMessages.GetMessages<NetworkRequestCreateKingdom>());
        Assert.Equal("player1", request.ControllerId);
        Assert.Equal("Real Kingdom", request.KingdomName);
        Assert.Equal("empire", request.CultureId);
        Assert.Null(request.PartyId);
        Assert.Null(request.SettlementId);
        AssertCurrentHeader(client, request.Header);
        Assert.Empty(TestEnvironment.Server.InternalMessages.GetMessages<CreateKingdom>());
    }

    [Fact]
    public void ServerCreateKingdomAuthorityRequest_RejectsUnauthenticatedPeer()
    {
        var client = TestEnvironment.Clients.First();
        var server = TestEnvironment.Server;
        var request = new NetworkRequestCreateKingdom(
            "player1",
            "Real Kingdom",
            "empire",
            "party1",
            null,
            CurrentHeader(server));

        GameThreadTestRunner.Run(() => server.SimulateMessage(client.NetPeer, request));

        var result = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkCreateKingdomResult>());
        Assert.Equal(AuthorityResultStatus.Unauthorized, result.Header.Status);
        Assert.Equal("peer-not-player", result.Header.ReasonCode);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
        Assert.Empty(server.InternalMessages.GetMessages<CreateKingdom>());
    }

    [Fact]
    public void ServerCreateKingdomAuthorityRequest_RejectsAuthenticatedPlayerWithoutClan()
    {
        var client = TestEnvironment.Clients.First();
        var server = TestEnvironment.Server;
        Authenticate(server, client, new Player("player1", "hero1", "party1", "clan1", "character1"));
        var request = new NetworkRequestCreateKingdom(
            "player1",
            "Real Kingdom",
            "empire",
            "party1",
            null,
            CurrentHeader(server));

        GameThreadTestRunner.Run(() => server.SimulateMessage(client.NetPeer, request));

        var result = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkCreateKingdomResult>());
        Assert.Equal(AuthorityResultStatus.Rejected, result.Header.Status);
        Assert.Equal("clan-not-found", result.Header.ReasonCode);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
        Assert.Empty(server.InternalMessages.GetMessages<CreateKingdom>());
    }

    [Fact]
    public void ServerPlayerKingdomCreated_Broadcasts_NetworkNotification()
    {
        var server = TestEnvironment.Server;

        GameThreadTestRunner.Run(() =>
            server.SimulateMessage(
                this,
                new PlayerKingdomCreated("player1", "Kingdom_Created_1", "Real Kingdom", "Clan_Player")));

        var networkMessage = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
        Assert.Equal("player1", networkMessage.ControllerId);
        Assert.Equal("Kingdom_Created_1", networkMessage.KingdomId);
        Assert.Equal("Real Kingdom", networkMessage.KingdomName);
        Assert.Equal("Clan_Player", networkMessage.ClanId);
        Assert.Null(networkMessage.PartyId);
        Assert.Null(networkMessage.SettlementId);

        foreach (var client in TestEnvironment.Clients)
        {
            var localEvent = Assert.Single(client.InternalMessages.GetMessages<PlayerKingdomCreated>());
            Assert.Equal("player1", localEvent.ControllerId);
            Assert.Equal("Kingdom_Created_1", localEvent.KingdomId);
            Assert.Equal("Real Kingdom", localEvent.KingdomName);
            Assert.Equal("Clan_Player", localEvent.ClanId);
        }
    }

    [Fact]
    public void ServerCreateKingdomAuthorityRequest_RejectsStaleClanCulture()
    {
        var client = TestEnvironment.Clients.First();
        var server = TestEnvironment.Server;
        server.CreateRegisteredObject<Clan>("clan1");
        Authenticate(server, client, new Player("player1", "hero1", "party1", "clan1", "character1"));
        var request = new NetworkRequestCreateKingdom(
            "player1",
            "Real Kingdom",
            "empire",
            "party1",
            null,
            CurrentHeader(server));

        GameThreadTestRunner.Run(() => server.SimulateMessage(client.NetPeer, request));

        var result = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkCreateKingdomResult>());
        Assert.Equal(AuthorityResultStatus.Rejected, result.Header.Status);
        Assert.Equal("stale-culture", result.Header.ReasonCode);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
        Assert.Empty(server.InternalMessages.GetMessages<CreateKingdom>());
    }

    private static void Authenticate(
        EnvironmentInstance server,
        EnvironmentInstance client,
        Player player)
    {
        var playerManager = server.Resolve<IPlayerManager>();
        Assert.True(playerManager.AddPlayer(player));
        playerManager.SetPeer(player.ControllerId, client.NetPeer);
    }

    private static AuthorityRequestHeader CurrentHeader(EnvironmentInstance instance)
    {
        Assert.True(instance.Resolve<IModConfigAuthority>().TryGetCurrent(out var snapshot));
        return new AuthorityRequestHeader(
            snapshot.ProtocolVersion,
            snapshot.SessionId,
            requestId: 17,
            snapshot.Revision);
    }

    private static void AssertCurrentHeader(EnvironmentInstance instance, AuthorityRequestHeader header)
    {
        Assert.True(instance.Resolve<IModConfigAuthority>().TryGetCurrent(out var snapshot));
        Assert.True(header.TryValidate(out var failure), failure);
        Assert.Equal(snapshot.ProtocolVersion, header.ProtocolVersion);
        Assert.Equal(snapshot.SessionId, header.SessionId);
        Assert.Equal(snapshot.Revision, header.ExpectedRevision);
        Assert.True(header.RequestId > 0);
    }
}

[CollectionDefinition("Kingdom sync game thread", DisableParallelization = true)]
public class KingdomSyncGameThreadCollection
{
    public const string Name = "Kingdom sync game thread";
}
