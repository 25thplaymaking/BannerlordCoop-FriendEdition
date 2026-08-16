using Common.Messaging;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MapEventSides.Messages;
using ProtoBuf.Meta;
using System;
using System.IO;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Serialization;

public class NetworkBattleJoinSerializationTest
{
    [Fact]
    public void Request_RoundTrip_PreservesFields()
    {
        var header = new AuthorityRequestHeader(7, "session-1", 41, 12);
        var result = RoundTrip(new NetworkRequestJoinBattle(
            header, "map-event-2", "party-3", BattleSideEnum.Defender));

        Assert.Equal("map-event-2", result.MapEventId);
        Assert.Equal("party-3", result.PartyId);
        Assert.Equal(BattleSideEnum.Defender, result.Side);
        Assert.Equal(7, result.Header.ProtocolVersion);
        Assert.Equal("session-1", result.Header.SessionId);
        Assert.Equal(41, result.Header.RequestId);
        Assert.Equal(12, result.Header.ExpectedRevision);
    }

    [Fact]
    public void Reply_RoundTrip_PreservesFields()
    {
        var header = new AuthorityRequestHeader(7, "session-1", 41, 12);
        var result = RoundTrip(new NetworkJoinBattleReply(header, AuthorityResultStatus.Accepted,
            "map-event-2", "party-3", (int)BattleSideEnum.Defender, null));

        Assert.Equal("41", result.RequestId);
        Assert.Equal("map-event-2", result.MapEventId);
        Assert.Equal("party-3", result.PartyId);
        Assert.True(result.Accepted);
        Assert.Equal(AuthorityResultStatus.Accepted, result.Header.Status);
        Assert.Equal("session-1", result.Header.SessionId);
        Assert.Equal(41, result.Header.RequestId);
        Assert.Equal(0, result.Header.CommittedRevision);
        Assert.Equal((int)BattleSideEnum.Defender, result.Side);
    }

    [Fact]
    public void LeaveRequestAndResult_RoundTrip_PreserveAuthorityCorrelation()
    {
        var header = new AuthorityRequestHeader(7, "session-1", 42, 12);
        var request = RoundTrip(new NetworkRequestLeaveBattle(header, "party-3", "map-event-2", false));
        var result = RoundTrip(new NetworkLeaveBattleResult(header, AuthorityResultStatus.Accepted,
            "party-3", "map-event-2", true, false, null));

        Assert.Equal("party-3", request.PartyId);
        Assert.Equal("map-event-2", request.MapEventId);
        Assert.False(request.FinishLocalMenus);
        Assert.Equal(42, request.Header.RequestId);
        Assert.Equal("session-1", request.Header.SessionId);
        Assert.Equal(AuthorityResultStatus.Accepted, result.Header.Status);
        Assert.Equal(42, result.Header.RequestId);
        Assert.Equal("party-3", result.PartyId);
        Assert.Equal("map-event-2", result.MapEventId);
        Assert.True(result.LeaveSiege);
        Assert.False(result.FinishLocalMenus);
    }

    [Fact]
    public void JoinAndLeaveRequests_DeclareTypedAuthorityRoutes()
    {
        Assert.Equal("battle.join", ((AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestJoinBattle), typeof(AuthorityRouteAttribute))).RouteId);
        Assert.Equal("battle.leave", ((AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestLeaveBattle), typeof(AuthorityRouteAttribute))).RouteId);
    }

    [Fact]
    public void CanonicalStateProofs_RoundTripExactCorrelation()
    {
        var join = RoundTrip(new NetworkAddBattleParty("side-1", "event-party-2", "session-1", 51,
            "map-event-3", "party-4", (int)BattleSideEnum.Attacker));
        var leave = RoundTrip(new NetworkPartyLeftBattle("party-4", true, false, "session-1", 52,
            "map-event-3"));

        Assert.Equal("session-1", join.SessionId);
        Assert.Equal(51, join.AuthorityRequestId);
        Assert.Equal("map-event-3", join.MapEventId);
        Assert.Equal("party-4", join.PartyId);
        Assert.Equal((int)BattleSideEnum.Attacker, join.Side);
        Assert.Equal("session-1", leave.SessionId);
        Assert.Equal(52, leave.AuthorityRequestId);
        Assert.Equal("map-event-3", leave.MapEventId);
        Assert.Equal("party-4", leave.PartyId);
        Assert.True(leave.LeaveSiege);
        Assert.False(leave.FinishLocalMenus);
    }

    private static T RoundTrip<T>(T value)
    {
        using var stream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(stream, value);
        stream.Position = 0;
        return (T)RuntimeTypeModel.Default.Deserialize(stream, null, typeof(T));
    }
}
