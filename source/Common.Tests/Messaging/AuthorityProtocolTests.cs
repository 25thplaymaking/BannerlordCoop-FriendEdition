using Common.Messaging;

namespace Common.Tests.Messaging;

public class AuthorityProtocolTests
{
    [Fact]
    public void RequestHeader_ValidatesCanonicalSessionSequenceAndRevision()
    {
        var header = new AuthorityRequestHeader(1, "session-7", 42, 9);

        Assert.True(header.TryValidate(out var failure));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(0, "session-7", 42, 9)]
    [InlineData(1, "", 42, 9)]
    [InlineData(1, "session-7", 0, 9)]
    [InlineData(1, "session-7", 42, -1)]
    public void RequestHeader_RejectsMalformedValues(int protocolVersion, string sessionId, long requestId, long expectedRevision)
    {
        var header = new AuthorityRequestHeader(protocolVersion, sessionId, requestId, expectedRevision);

        Assert.False(header.TryValidate(out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Theory]
    [InlineData(AuthorityResultStatus.Accepted, "")]
    [InlineData(AuthorityResultStatus.Rejected, "party-not-controlled")]
    [InlineData(AuthorityResultStatus.Unavailable, "server-not-ready")]
    public void ResultHeader_AcceptsBoundedMachineReasonCodes(AuthorityResultStatus status, string reasonCode)
    {
        var header = new AuthorityResultHeader("session-7", 42, status, 9, reasonCode);

        Assert.True(header.TryValidate(out var failure));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("human readable reason")]
    [InlineData("reason/with/slash")]
    [InlineData("reason\nwith-newline")]
    public void ResultHeader_RejectsUnboundedOrPresentationReasonCodes(string reasonCode)
    {
        var header = new AuthorityResultHeader("session-7", 42, AuthorityResultStatus.Rejected, 9, reasonCode);

        Assert.False(header.TryValidate(out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void RouteAttribute_RequiresAStableRouteId()
    {
        Assert.Throws<ArgumentException>(() => new AuthorityRouteAttribute("", AuthorityRouteKind.Command));
        Assert.Throws<ArgumentException>(() => new AuthorityRouteAttribute("map event", AuthorityRouteKind.Command));

        var attribute = new AuthorityRouteAttribute("map-event.create", AuthorityRouteKind.Command);

        Assert.Equal("map-event.create", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
    }

    [Fact]
    public void HeaderValidation_CannotRepresentAcceptedAsAFailure()
    {
        Assert.True(AuthorityHeaderValidation.Valid.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.Accepted, "not-a-failure"));

        var unavailable = AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "server-not-ready");
        Assert.False(unavailable.IsValid);
        Assert.Equal(AuthorityResultStatus.Unavailable, unavailable.Status);
    }
}
