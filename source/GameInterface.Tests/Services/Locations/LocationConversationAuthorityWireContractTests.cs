using Common.Messaging;
using GameInterface.Services.Locations.Messages.Conversation;
using ProtoBuf.Meta;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.Locations;

public sealed class LocationConversationAuthorityWireContractTests
{
    [Fact]
    public void EndRequest_RoundTripPreservesCommandRouteOwnerAndLease()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 301, 31);
        var request = RoundTrip(new NetworkLocationConversationEnded("lease-a", header));
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkLocationConversationEnded), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("location.conversation.end", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
        Assert.Equal("lease-a", request.LeaseId);
    }

    [Fact]
    public void EndResult_RoundTripPreservesExactTerminalAndLeaseRevision()
    {
        var header = new AuthorityResultHeader(
            "0123456789abcdef0123456789abcdef", 302, AuthorityResultStatus.Accepted, 32, null);
        var result = RoundTrip(new NetworkLocationConversationEndResult(header, "lease-b", 7));

        Assert.Equal(header.SessionId, result.Header.SessionId);
        Assert.Equal(header.RequestId, result.Header.RequestId);
        Assert.Equal(header.CommittedRevision, result.Header.CommittedRevision);
        Assert.Equal(AuthorityResultStatus.Accepted, result.Header.Status);
        Assert.Equal("lease-b", result.LeaseId);
        Assert.Equal(7, result.LeaseRevision);
    }

    private static T RoundTrip<T>(T value)
    {
        using var stream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(stream, value);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        return (T)RuntimeTypeModel.Default.Deserialize(stream, null, typeof(T));
    }
}
