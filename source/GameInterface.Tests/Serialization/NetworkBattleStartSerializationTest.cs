using GameInterface.Services.MapEvents.Messages.Start;
using Common.Messaging;
using ProtoBuf.Meta;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Serialization;

public class NetworkBattleStartSerializationTest
{
    [Fact]
    public void Request_RoundTrip_PreservesFields()
    {
        var original = new NetworkBattleStartRequest("req-1", 1, "mapEvent-7", "attacker-42");

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            RuntimeTypeModel.Default.Serialize(ms, original);
            bytes = ms.ToArray();
        }

        Assert.NotEmpty(bytes);

        NetworkBattleStartRequest result;
        using (var ms = new MemoryStream(bytes))
        {
            result = (NetworkBattleStartRequest)RuntimeTypeModel.Default.Deserialize(ms, null, typeof(NetworkBattleStartRequest));
        }

        Assert.Equal("req-1", result.RequestId);
        Assert.Equal(1, result.Mode);
        Assert.Equal("mapEvent-7", result.MapEventId);
        Assert.Equal("attacker-42", result.AttackerPartyId);
    }

    [Fact]
    public void Reply_RoundTrip_PreservesFields()
    {
        var original = new NetworkBattleStartReply("req-1", true);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            RuntimeTypeModel.Default.Serialize(ms, original);
            bytes = ms.ToArray();
        }

        Assert.NotEmpty(bytes);

        NetworkBattleStartReply result;
        using (var ms = new MemoryStream(bytes))
        {
            result = (NetworkBattleStartReply)RuntimeTypeModel.Default.Deserialize(ms, null, typeof(NetworkBattleStartReply));
        }

        Assert.Equal("req-1", result.RequestId);
        Assert.True(result.Accepted);
    }

    [Fact]
    public void AuthorityAdapters_RoundTripWithoutRenumberingLegacyFields()
    {
        var header = new AuthorityRequestHeader(1, "session", 17, 4);
        var request = new NetworkBattleStartRequest(header, 0, "mapEvent-7", "attacker-42");
        var reply = new NetworkBattleStartReply(header, AuthorityResultStatus.Accepted, 0, "mapEvent-7", null);

        Assert.Equal(17, request.Header.RequestId);
        Assert.Equal("session", request.Header.SessionId);
        Assert.Equal("17", request.RequestId);
        Assert.Equal(17, reply.Header.RequestId);
        Assert.Equal(AuthorityResultStatus.Accepted, reply.Header.Status);
        Assert.Equal("mapEvent-7", reply.MapEventId);
    }
}
