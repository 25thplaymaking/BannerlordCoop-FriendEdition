using Common.Messaging;
using Coop.Core.Server.Services.Kingdoms.Messages;
using Coop.IntegrationTests.Environment;
using Coop.IntegrationTests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.Entity;
using GameInterface.Services.Kingdoms.Data;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using TaleWorlds.CampaignSystem.Election;

namespace Coop.IntegrationTests.Kingdoms;

/// <summary>
/// Integration coverage for the authenticated kingdom decision vote authority route.
/// </summary>
[Collection(KingdomSyncGameThreadCollection.Name)]
public class KingdomDecisionVoteSyncTest
{
    internal TestEnvironment TestEnvironment { get; } = new TestEnvironment();

    [Fact]
    public void ClientKingdomDecisionVoteRequested_SendsCorrelatedAuthorityRequest()
    {
        var client = TestEnvironment.Clients.First();
        client.Resolve<IControllerIdProvider>().SetControllerId("player1");
        client.Resolve<TestNetworkRouter>().IsMessageRoutingEnabled = false;
        var voteData = new KingdomDecisionVoteData(
            "kingdom1",
            0,
            1,
            (int)Supporter.SupportWeights.FullyPush,
            false);

        GameThreadTestRunner.Run(() =>
            client.SimulateMessage(this, new KingdomDecisionVoteRequested(voteData)));

        var request = Assert.Single(client.NetworkSentMessages.GetMessages<NetworkRequestKingdomDecisionVote>());
        Assert.Equal("player1", request.ControllerId);
        Assert.Same(voteData, request.VoteData);
        AssertCurrentHeader(client, request.Header);
        Assert.Empty(TestEnvironment.Server.InternalMessages.GetMessages<ChangeKingdomDecisionVote>());
    }

    [Fact]
    public void ServerKingdomDecisionVoteAuthorityRequest_RejectsUnauthenticatedPeer()
    {
        var client = TestEnvironment.Clients.First();
        var server = TestEnvironment.Server;
        var request = Request(server);

        GameThreadTestRunner.Run(() => server.SimulateMessage(client.NetPeer, request));

        var result = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkKingdomDecisionVoteResult>());
        Assert.Equal(AuthorityResultStatus.Unauthorized, result.Header.Status);
        Assert.Equal("peer-not-player", result.Header.ReasonCode);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
        Assert.Empty(server.InternalMessages.GetMessages<ChangeKingdomDecisionVote>());
    }

    [Fact]
    public void ServerKingdomDecisionVoteAuthorityRequest_RejectsAuthenticatedPlayerWithoutKingdom()
    {
        var client = TestEnvironment.Clients.First();
        var server = TestEnvironment.Server;
        var playerManager = server.Resolve<IPlayerManager>();
        var player = new Player("player1", "hero1", "party1", "clan1", "character1");
        Assert.True(playerManager.AddPlayer(player));
        playerManager.SetPeer(player.ControllerId, client.NetPeer);
        var request = Request(server);

        GameThreadTestRunner.Run(() => server.SimulateMessage(client.NetPeer, request));

        var result = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkKingdomDecisionVoteResult>());
        Assert.Equal(AuthorityResultStatus.Rejected, result.Header.Status);
        Assert.Equal("kingdom-not-found", result.Header.ReasonCode);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
        Assert.Empty(server.InternalMessages.GetMessages<ChangeKingdomDecisionVote>());
    }

    private static NetworkRequestKingdomDecisionVote Request(EnvironmentInstance server) =>
        new(
            "player1",
            new KingdomDecisionVoteData(
                "kingdom1",
                0,
                1,
                (int)Supporter.SupportWeights.StronglyFavor,
                false),
            CurrentHeader(server));

    private static AuthorityRequestHeader CurrentHeader(EnvironmentInstance instance)
    {
        Assert.True(instance.Resolve<IModConfigAuthority>().TryGetCurrent(out var snapshot));
        return new AuthorityRequestHeader(
            snapshot.ProtocolVersion,
            snapshot.SessionId,
            requestId: 23,
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
