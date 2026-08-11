using GameInterface.Services.Separatism;
using ProtoBuf;
using Xunit;

namespace GameInterface.Tests.Services.Separatism;

public sealed class SeparatismRecruitmentTests
{
    [Fact]
    public void FallenClanOffer_RequiresTheCapabilityAndEveryOriginalCondition()
    {
        Assert.True(SeparatismConversationPolicy.CanOfferFallenClanRecruitment(
            capabilityEnabled: true,
            actorHasKingdom: true,
            actorIsKingdomRuler: true,
            targetExists: true,
            targetHasNoKingdom: true,
            targetIsNotMinorFaction: true,
            targetIsFactionLeader: true,
            factionsAreAtPeace: true));

        for (int omitted = 0; omitted < 8; omitted++)
        {
            var conditions = new[] { true, true, true, true, true, true, true, true };
            conditions[omitted] = false;

            Assert.False(SeparatismConversationPolicy.CanOfferFallenClanRecruitment(
                conditions[0], conditions[1], conditions[2], conditions[3],
                conditions[4], conditions[5], conditions[6], conditions[7]));
        }
    }

    [Fact]
    public void RecruitmentRequest_RoundTripsEveryAuthorityPrecondition()
    {
        var request = new NetworkRequestSeparatismRecruitment(
            sessionId: "9d80f6d4ae2a4f7db912829fd7a3a284",
            requestId: 17,
            expectedRevision: 1234,
            expectedKingdomId: "kingdom-1",
            targetClanId: "fallen-clan",
            targetHeroId: "fallen-ruler");

        NetworkRequestSeparatismRecruitment copy = Serializer.DeepClone(request);

        Assert.Equal(request.SessionId, copy.SessionId);
        Assert.Equal(request.RequestId, copy.RequestId);
        Assert.Equal(request.ExpectedRevision, copy.ExpectedRevision);
        Assert.Equal(request.ExpectedKingdomId, copy.ExpectedKingdomId);
        Assert.Equal(request.TargetClanId, copy.TargetClanId);
        Assert.Equal(request.TargetHeroId, copy.TargetHeroId);
        Assert.True(SeparatismRecruitmentProtocol.IsRequestShapeValid(copy));
    }

    [Fact]
    public void RecruitmentResult_RoundTripsTheCommittedRevision()
    {
        var result = new NetworkSeparatismRecruitmentResult(
            sessionId: "9d80f6d4ae2a4f7db912829fd7a3a284",
            requestId: 17,
            status: SeparatismRecruitmentStatus.Accepted,
            targetClanId: "fallen-clan",
            revision: 5678);

        NetworkSeparatismRecruitmentResult copy = Serializer.DeepClone(result);

        Assert.Equal(result.SessionId, copy.SessionId);
        Assert.Equal(result.RequestId, copy.RequestId);
        Assert.Equal(result.Status, copy.Status);
        Assert.Equal(result.TargetClanId, copy.TargetClanId);
        Assert.Equal(result.Revision, copy.Revision);
        Assert.True(SeparatismRecruitmentProtocol.IsResultShapeValid(copy));
    }

    [Fact]
    public void RequestLedger_ReplaysExactDuplicates_AndRejectsConflictsOrOlderWork()
    {
        var ledger = new SeparatismRequestReplayLedger<string>(capacityPerController: 2);
        var request = Request(9, "fallen-clan");
        var result = Result(9, "fallen-clan");

        Assert.Equal(SeparatismReplayDecision.New, ledger.Inspect("controller", request, out _));
        ledger.Record("controller", request, result);

        Assert.Equal(SeparatismReplayDecision.Replay, ledger.Inspect("controller", request, out var replay));
        Assert.Equal(SeparatismRecruitmentStatus.Accepted, replay.Status);
        Assert.Equal(
            SeparatismReplayDecision.Conflict,
            ledger.Inspect("controller", Request(9, "different-clan"), out _));
        Assert.Equal(
            SeparatismReplayDecision.Stale,
            ledger.Inspect("controller", Request(8, "older-clan"), out _));
        Assert.Equal(
            SeparatismReplayDecision.New,
            ledger.Inspect("other-connection", Request(1, "fallen-clan"), out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Capability_IsDisabledUnlessBothTheOptionAndRouteAreReady(
        bool optionEnabled,
        bool routeReady)
    {
        Assert.False(SeparatismCapabilityPolicy.AllowRecruitment(optionEnabled, routeReady));
    }

    [Fact]
    public void Capability_IsEnabledWhenTheOptionAndRouteAreReady()
    {
        Assert.True(SeparatismCapabilityPolicy.AllowRecruitment(
            optionEnabled: true,
            routeReady: true));
    }

    private static NetworkRequestSeparatismRecruitment Request(long id, string clanId) =>
        new(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            id,
            1234,
            "kingdom-1",
            clanId,
            "fallen-ruler");

    private static NetworkSeparatismRecruitmentResult Result(long id, string clanId) =>
        new(
            "9d80f6d4ae2a4f7db912829fd7a3a284",
            id,
            SeparatismRecruitmentStatus.Accepted,
            clanId,
            5678);
}
