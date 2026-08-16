using Common.Messaging;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public sealed class BattleJoinLeaveAuthorityTests
{
    [Fact]
    public void JoinProof_RequiresExactSessionRequestEventPartyAndSide()
    {
        var header = new AuthorityRequestHeader(1, "session-a", 17, 4);
        var result = new NetworkJoinBattleReply(header, AuthorityResultStatus.Accepted,
            "event-a", "party-a", (int)BattleSideEnum.Defender, null);

        Assert.True(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-a", 17, "event-a", "party-a", (int)BattleSideEnum.Defender), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-old", 17, "event-a", "party-a", (int)BattleSideEnum.Defender), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-a", 16, "event-a", "party-a", (int)BattleSideEnum.Defender), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-a", 17, "event-old", "party-a", (int)BattleSideEnum.Defender), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-a", 17, "event-a", "party-old", (int)BattleSideEnum.Defender), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedJoinProof(
            new BattleJoinProof("session-a", 17, "event-a", "party-a", (int)BattleSideEnum.Attacker), result));
    }

    [Fact]
    public void LeaveProof_RequiresExactSessionRequestEventPartyAndSiegeState()
    {
        var header = new AuthorityRequestHeader(1, "session-a", 18, 4);
        var result = new NetworkLeaveBattleResult(header, AuthorityResultStatus.Accepted,
            "party-a", "event-a", true, false, null);

        Assert.True(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-a", 18, "event-a", "party-a", true), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-old", 18, "event-a", "party-a", true), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-a", 17, "event-a", "party-a", true), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-a", 18, "event-old", "party-a", true), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-a", 18, "event-a", "party-old", true), result));
        Assert.False(BattleJoinLeaveHandler.IsExpectedLeaveProof(
            new BattleLeaveProof("session-a", 18, "event-a", "party-a", false), result));
    }
}
