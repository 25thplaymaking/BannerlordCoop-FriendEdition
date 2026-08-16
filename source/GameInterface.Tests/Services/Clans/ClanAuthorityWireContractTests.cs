using Common.Messaging;
using GameInterface.Services.Clans.Messages;
using ProtoBuf.Meta;
using System;
using System.IO;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.Clans;

public sealed class ClanAuthorityWireContractTests
{
    [Fact]
    public void AutoRecruitRequest_RoundTripPreservesCommandRouteOwnerAndValue()
    {
        var header = Header(101);
        var request = RoundTrip(new ChangeAutoRecruitForSettlement("settlement-a", true, header));

        AssertRoute<ChangeAutoRecruitForSettlement>("clan.autorecruit.set");
        AssertHeader(header, request.Header);
        Assert.Equal("settlement-a", request.HomeSettlementId);
        Assert.True(request.Value);
    }

    [Fact]
    public void PartyBehaviorRequest_RoundTripPreservesCommandRouteOwnerAndObjective()
    {
        var header = Header(102);
        var request = RoundTrip(new UpdatePartyBehaviorOnSelection(
            "party-a", MobileParty.PartyObjective.Defensive, header));

        AssertRoute<UpdatePartyBehaviorOnSelection>("clan.party.behavior.set");
        AssertHeader(header, request.Header);
        Assert.Equal("party-a", request.MobilePartyId);
        Assert.Equal(MobileParty.PartyObjective.Defensive, request.PartyObjective);
    }

    [Fact]
    public void PartyCreateRequest_RoundTripPreservesCommandRouteOwnerAndLeader()
    {
        var header = Header(103);
        var request = RoundTrip(new CreateNewClanParty("hero-a", header));

        AssertRoute<CreateNewClanParty>("clan.party.create");
        AssertHeader(header, request.Header);
        Assert.Equal("hero-a", request.NewLeaderId);
    }

    [Fact]
    public void PartyLeaderRequest_RoundTripDoesNotSwapPartyAndLeaderOwnership()
    {
        var header = Header(104);
        var request = RoundTrip(new ChangeClanPartyLeader("hero-b", "party-b", header));

        AssertRoute<ChangeClanPartyLeader>("clan.party.leader.set");
        AssertHeader(header, request.Header);
        Assert.Equal("hero-b", request.NewLeaderId);
        Assert.Equal("party-b", request.SelectedPartyId);
    }

    private static AuthorityRequestHeader Header(long requestId) =>
        new(1, "0123456789abcdef0123456789abcdef", requestId, 17);

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
