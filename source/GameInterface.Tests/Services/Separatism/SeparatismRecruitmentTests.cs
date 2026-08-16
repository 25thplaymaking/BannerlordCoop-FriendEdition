using Common.Messaging;
using GameInterface.Services.Separatism;
using ProtoBuf;
using Xunit;

namespace GameInterface.Tests.Services.Separatism;

public sealed class SeparatismRecruitmentTests
{
    [Fact]
    public void FallenClanOffer_RequiresTheCapabilityAndEveryOriginalCondition()
    {
        Assert.True(SeparatismConversationPolicy.CanOfferFallenClanRecruitment(true, true, true, true, true, true, true, true));
        for (int omitted = 0; omitted < 8; omitted++)
        {
            var conditions = new[] { true, true, true, true, true, true, true, true };
            conditions[omitted] = false;
            Assert.False(SeparatismConversationPolicy.CanOfferFallenClanRecruitment(
                conditions[0], conditions[1], conditions[2], conditions[3], conditions[4], conditions[5],
                conditions[6], conditions[7]));
        }
    }

    [Fact]
    public void RecruitmentRequest_RoundTripsHeaderDigestAndEverySemanticPrecondition()
    {
        var header = new AuthorityRequestHeader(3, "9d80f6d4ae2a4f7db912829fd7a3a284", 17, 8);
        var request = new NetworkRequestSeparatismRecruitment(header, 1234, "kingdom-1", "fallen-clan", "fallen-ruler");
        NetworkRequestSeparatismRecruitment copy = Serializer.DeepClone(request);

        Assert.Equal(header.ProtocolVersion, copy.Header.ProtocolVersion);
        Assert.Equal(header.SessionId, copy.Header.SessionId);
        Assert.Equal(header.RequestId, copy.Header.RequestId);
        Assert.Equal(header.ExpectedRevision, copy.Header.ExpectedRevision);
        Assert.Equal(1234, copy.ExpectedFactionChangeTicks);
        Assert.Equal("kingdom-1", copy.ExpectedKingdomId);
        Assert.Equal("fallen-clan", copy.TargetClanId);
        Assert.Equal("fallen-ruler", copy.TargetHeroId);
        Assert.Equal(request.CommandDigest, copy.CommandDigest);
        Assert.Null(SeparatismRecruitmentProtocol.ValidateRequest(copy));
    }

    [Fact]
    public void RecruitmentRequest_UsesDistinctDigestAndStructuralKeyForSemanticChanges()
    {
        var header = new AuthorityRequestHeader(1, "session-1", 9, 7);
        var request = new NetworkRequestSeparatismRecruitment(header, 5, "kingdom", "clan", "hero");
        var changed = new NetworkRequestSeparatismRecruitment(header, 5, "kingdom", "clan", "other-hero");

        Assert.NotEqual(SeparatismRecruitmentProtocol.StructuralKey(request),
            SeparatismRecruitmentProtocol.StructuralKey(changed));
        Assert.NotEqual(request.CommandDigest, changed.CommandDigest);
    }

    [Fact]
    public void RecruitmentResult_RoundTripsExactCommitProof()
    {
        var result = new NetworkSeparatismRecruitmentResult(
            new AuthorityResultHeader("9d80f6d4ae2a4f7db912829fd7a3a284", 17, AuthorityResultStatus.Accepted, 8, null),
            "v1|digest", "kingdom-1", "kingdom-1", "fallen-clan", "fallen-ruler", 1234, 5678);
        NetworkSeparatismRecruitmentResult copy = Serializer.DeepClone(result);

        Assert.Equal(result.Header.RequestId, copy.Header.RequestId);
        Assert.Equal(result.CommandDigest, copy.CommandDigest);
        Assert.Equal(result.ExpectedKingdomId, copy.CommittedKingdomId);
        Assert.Equal(result.TargetClanId, copy.TargetClanId);
        Assert.Equal(result.TargetHeroId, copy.TargetHeroId);
        Assert.Equal(result.CommittedFactionChangeTicks, copy.CommittedFactionChangeTicks);
        Assert.True(SeparatismRecruitmentProtocol.IsResultShapeValid(copy));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Capability_IsDisabledUnlessBothTheOptionAndRouteAreReady(bool optionEnabled, bool routeReady) =>
        Assert.False(SeparatismCapabilityPolicy.AllowRecruitment(optionEnabled, routeReady));

    [Fact]
    public void Capability_IsEnabledWhenTheOptionAndRouteAreReady() =>
        Assert.True(SeparatismCapabilityPolicy.AllowRecruitment(optionEnabled: true, routeReady: true));
}
