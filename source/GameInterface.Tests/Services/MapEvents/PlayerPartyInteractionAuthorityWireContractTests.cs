using Common.Messaging;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.MapEvents.Messages.Conversation;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf.Meta;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public sealed class PlayerPartyInteractionAuthorityWireContractTests
{
    [Fact]
    public void ShownRequest_RoundTripPreservesCommandRouteOwnerAndInteractionRevision()
    {
        var header = Header(401);
        var request = RoundTrip(new RequestPlayerPartyInteractionShown(header, "interaction-a", 11));

        AssertRoute<RequestPlayerPartyInteractionShown>("player-interaction.shown");
        AssertHeader(header, request.Header);
        Assert.Equal("interaction-a", request.InteractionSessionId);
        Assert.Equal(11, request.ExpectedInteractionRevision);
    }

    [Fact]
    public void TradeOfferRequest_RoundTripPreservesCommandRouteOwnerAndWholeOffer()
    {
        var header = Header(402);
        var request = RoundTrip(new RequestPlayerPartyTradeOffer(
            header,
            "interaction-b",
            expectedInteractionRevision: 12,
            new[] { new ItemRosterElementData(new ItemObjectData("item-a", null, true), 3) },
            new[] { new TroopRosterElementData("troop-a", 4, 1, 6) },
            offeredGold: 125,
            new[] { "fief-a" },
            new[] { new TroopRosterElementData("prisoner-a", 1, 0, 0) },
            offeredPeace: true));

        AssertRoute<RequestPlayerPartyTradeOffer>("player-interaction.trade-offer");
        AssertHeader(header, request.Header);
        Assert.Equal("interaction-b", request.InteractionSessionId);
        Assert.Equal(12, request.ExpectedInteractionRevision);
        Assert.Equal("item-a", Assert.Single(request.OfferedItems).ItemObjectData.ItemObjectId);
        Assert.Equal("troop-a", Assert.Single(request.OfferedTroops).CharacterId);
        Assert.Equal(125, request.OfferedGold);
        Assert.Equal("fief-a", Assert.Single(request.OfferedFiefs));
        Assert.Equal("prisoner-a", Assert.Single(request.OfferedPrisoners).CharacterId);
        Assert.True(request.OfferedPeace);
    }

    [Fact]
    public void TradeAcceptRequest_RoundTripPreservesCommandRouteOwnerAndExplicitDecision()
    {
        var header = Header(403);
        var request = RoundTrip(new RequestPlayerPartyTradeAccept(header, "interaction-c", 13, accepted: false));

        AssertRoute<RequestPlayerPartyTradeAccept>("player-interaction.trade-accept");
        AssertHeader(header, request.Header);
        Assert.Equal("interaction-c", request.InteractionSessionId);
        Assert.Equal(13, request.ExpectedInteractionRevision);
        Assert.False(request.Accepted);
    }

    private static AuthorityRequestHeader Header(long requestId) =>
        new(1, "0123456789abcdef0123456789abcdef", requestId, 41);

    private static void AssertRoute<TRequest>(string expectedRoute)
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(TRequest), typeof(AuthorityRouteAttribute));

        Assert.NotNull(attribute);
        Assert.Equal(expectedRoute, attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);
    }

    private static void AssertHeader(AuthorityRequestHeader expected, AuthorityRequestHeader actual)
    {
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.ExpectedRevision, actual.ExpectedRevision);
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
