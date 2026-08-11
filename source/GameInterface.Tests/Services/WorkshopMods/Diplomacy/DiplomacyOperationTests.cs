using GameInterface.Services.WorkshopMods.Diplomacy;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Diplomacy;

public sealed class DiplomacyOperationTests
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    [Fact]
    public void GameplayOperationCatalog_CoversEveryAuditedPlayerAction()
    {
        Assert.Equal(
            new[]
            {
                DiplomacyOperation.DonateGold,
                DiplomacyOperation.GrantFief,
                DiplomacyOperation.SendMessenger,
                DiplomacyOperation.MakePeace,
                DiplomacyOperation.DeclareWar,
                DiplomacyOperation.EndAlliance,
                DiplomacyOperation.FormNonAggressionPact,
                DiplomacyOperation.AcceptKeepFief,
                DiplomacyOperation.DeclineKeepFief,
            },
            Enum.GetValues<DiplomacyOperation>());
    }

    [Fact]
    public void Request_RoundTripsEveryAuthorityField()
    {
        var request = Request(
            DiplomacyOperation.GrantFief,
            targetId: "clan.target",
            secondaryTargetId: "settlement.source",
            intValue: 17);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var roundTrip = Serializer.Deserialize<NetworkRequestDiplomacyOperation>(stream);

        Assert.Equal(SessionId, roundTrip.SessionId);
        Assert.Equal(73, roundTrip.RequestId);
        Assert.Equal(11, roundTrip.ExpectedRevision);
        Assert.Equal(DiplomacyOperation.GrantFief, roundTrip.Operation);
        Assert.Equal("clan.target", roundTrip.TargetId);
        Assert.Equal("settlement.source", roundTrip.SecondaryTargetId);
        Assert.Equal(17, roundTrip.IntValue);
    }

    [Theory]
    [InlineData((int)DiplomacyOperation.DonateGold, "clan.target", "", 1, true)]
    [InlineData((int)DiplomacyOperation.GrantFief, "clan.target", "settlement.source", 0, true)]
    [InlineData((int)DiplomacyOperation.SendMessenger, "hero.target", "", 0, true)]
    [InlineData((int)DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.DeclareWar, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.EndAlliance, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.FormNonAggressionPact, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.AcceptKeepFief, "settlement.source", "", 0, true)]
    [InlineData((int)DiplomacyOperation.DeclineKeepFief, "settlement.source", "", 0, true)]
    [InlineData((int)DiplomacyOperation.DonateGold, "clan.target", "", 0, false)]
    [InlineData((int)DiplomacyOperation.GrantFief, "", "settlement.source", 0, false)]
    [InlineData((int)DiplomacyOperation.SendMessenger, "", "", 0, false)]
    [InlineData((int)DiplomacyOperation.MakePeace, "kingdom.a", "", 0, false)]
    [InlineData((int)DiplomacyOperation.AcceptKeepFief, "", "", 0, false)]
    public void Protocol_RequiresTheExactShapeForEachOperation(
        int operationValue,
        string targetId,
        string secondaryTargetId,
        int intValue,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyOperationProtocol.IsRequestShapeValid(
            Request((DiplomacyOperation)operationValue, targetId, secondaryTargetId, intValue)));
    }

    [Fact]
    public void Protocol_RejectsMalformedEnvelopeAndControlCharacters()
    {
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            "not-a-session", 1, 0, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 0, 0, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 1, -1, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 1, 0, DiplomacyOperation.SendMessenger, "hero\nforged", "", 0)));
    }

    [Fact]
    public void Protocol_ValidatesServerResultsAndKeepFiefPrompts()
    {
        Assert.True(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 0, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                (DiplomacyOperationStatus)99, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, -1, "hero.target")));

        Assert.True(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt(SessionId, "settlement.source", 3)));
        Assert.False(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt(SessionId, "", 3)));
        Assert.False(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt("bad-session", "settlement.source", 3)));
    }

    [Fact]
    public void CommandKey_IsStableAndIncludesEveryIntentField()
    {
        var baseline = Request(DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0);
        Assert.Equal(
            DiplomacyOperationProtocol.CommandKey(baseline),
            DiplomacyOperationProtocol.CommandKey(Request(
                DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0)));

        Assert.Equal(4, new[]
        {
            baseline,
            Request(DiplomacyOperation.DeclareWar, "kingdom.a", "kingdom.b", 0),
            Request(DiplomacyOperation.MakePeace, "kingdom.b", "kingdom.a", 0),
            Request(DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 1),
        }.Select(DiplomacyOperationProtocol.CommandKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ReplayLedger_ReplaysExactDuplicateAndRejectsConflictingReuse()
    {
        var ledger = new DiplomacyRequestLedger<object>(capacity: 2);
        var peer = new object();
        var result = new NetworkDiplomacyOperationResult(
            SessionId, 73, DiplomacyOperation.SendMessenger,
            DiplomacyOperationStatus.Accepted, revision: 12, targetId: "hero.target");

        Assert.Equal(DiplomacyReplayDecision.New,
            ledger.Inspect(peer, 73, "same", out _));
        ledger.Record(peer, 73, "same", result);
        Assert.Equal(DiplomacyReplayDecision.Replay,
            ledger.Inspect(peer, 73, "same", out var replay));
        Assert.Same(result, replay);
        Assert.Equal(DiplomacyReplayDecision.Conflict,
            ledger.Inspect(peer, 73, "different", out _));
    }

    [Fact]
    public void ReplayLedger_RejectsEvictedOrOutOfOrderRequestIds()
    {
        var ledger = new DiplomacyRequestLedger<object>(capacity: 2);
        var peer = new object();

        foreach (long requestId in new long[] { 1, 2, 3 })
        {
            var result = new NetworkDiplomacyOperationResult(
                SessionId, requestId, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, requestId, "hero.target");
            Assert.Equal(DiplomacyReplayDecision.New,
                ledger.Inspect(peer, requestId, "command-" + requestId, out _));
            ledger.Record(peer, requestId, "command-" + requestId, result);
        }

        Assert.Equal(DiplomacyReplayDecision.Conflict,
            ledger.Inspect(peer, 1, "command-1", out _));
        Assert.Equal(DiplomacyReplayDecision.Conflict,
            ledger.Inspect(peer, 2, "different", out _));
        Assert.Equal(DiplomacyReplayDecision.New,
            ledger.Inspect(peer, 4, "command-4", out _));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Capability_RequiresOperatorEnablementAndLiveRoute(
        bool optionEnabled,
        bool routeReady,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyCapabilityPolicy.IsEnabled(optionEnabled, routeReady));
    }

    private static NetworkRequestDiplomacyOperation Request(
        DiplomacyOperation operation,
        string targetId = "",
        string secondaryTargetId = "",
        int intValue = 0) => new(
            SessionId,
            requestId: 73,
            expectedRevision: 11,
            operation,
            targetId,
            secondaryTargetId,
            intValue);
}
