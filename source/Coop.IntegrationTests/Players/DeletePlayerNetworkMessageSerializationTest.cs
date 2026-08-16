using Common.Messaging;
using GameInterface.Services.Players.Messages;
using ProtoBuf;

namespace Coop.IntegrationTests.Players;

public class DeletePlayerNetworkMessageSerializationTest
{
    [Fact]
    public void NetworkRequestDeletePlayer_RoundTrips()
    {
        var original = new NetworkRequestDeletePlayer(
            new AuthorityRequestHeader(1, "session-player-delete", 17, 4));

        var copy = RoundTrip(original);

        Assert.Equal(1, copy.Header.ProtocolVersion);
        Assert.Equal("session-player-delete", copy.Header.SessionId);
        Assert.Equal(17, copy.Header.RequestId);
        Assert.Equal(4, copy.Header.ExpectedRevision);
    }

    [Fact]
    public void NetworkPlayerRemoved_RoundTrips()
    {
        var original = new NetworkPlayerRemoved("Controller_1", "Hero_Player");

        var copy = RoundTrip(original);

        Assert.Equal("Controller_1", copy.ControllerId);
        Assert.Equal("Hero_Player", copy.HeroId);
    }

    [Fact]
    public void NetworkDeletePlayerDenied_RoundTrips()
    {
        var original = new NetworkDeletePlayerDenied("Cannot delete a player whose party is in a battle or siege.");

        var copy = RoundTrip(original);

        Assert.Equal("Cannot delete a player whose party is in a battle or siege.", copy.Reason);
    }

    private static T RoundTrip<T>(T original)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }
}
