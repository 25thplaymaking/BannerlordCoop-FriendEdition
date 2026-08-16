using Common.Messaging;
using GameInterface.Services.Heroes.Messages.RomanceFlow;
using System;
using Xunit;
using Romance = TaleWorlds.CampaignSystem.Romance;

namespace GameInterface.Tests.Services.Heroes;

public sealed class RomanceAuthorityRouteContractTests
{
    [Fact]
    public void TransitionRequest_DeclaresTypedCommandAndRetainsHeader()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 71, 4);
        var request = new NetworkRequestRomanceStateChange(
            "target-hero", Romance.RomanceLevelEnum.CourtshipStarted, 0, 0f, 0f, null, header);
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestRomanceStateChange), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("romance.transition", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
    }

    [Fact]
    public void SnapshotRequest_DeclaresBootstrapQueryAndRetainsHeader()
    {
        var header = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 72, 5);
        var request = new NetworkRequestRomanceStateSync(header);
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestRomanceStateSync), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal("romance.snapshot", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.BootstrapQuery, attribute.Kind);
        Assert.Equal(header.SessionId, request.Header.SessionId);
        Assert.Equal(header.RequestId, request.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, request.Header.ExpectedRevision);
    }

    [Fact]
    public void TransitionResult_RetainsExactTerminalCorrelation()
    {
        var request = new AuthorityRequestHeader(1, "0123456789abcdef0123456789abcdef", 73, 6);
        var result = new NetworkRomanceStateChangeResult(
            request, "actor", "target", Romance.RomanceLevelEnum.CourtshipStarted, AuthorityResultStatus.Accepted);

        Assert.Equal(request.SessionId, result.Header.SessionId);
        Assert.Equal(request.RequestId, result.Header.RequestId);
        Assert.Equal(request.ExpectedRevision, result.Header.CommittedRevision);
        Assert.Equal(AuthorityResultStatus.Accepted, result.Header.Status);
    }
}
