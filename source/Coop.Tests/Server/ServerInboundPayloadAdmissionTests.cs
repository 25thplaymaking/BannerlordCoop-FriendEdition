using Coop.Core.Server;
using Xunit;

namespace Coop.Tests.Server;

public class ServerInboundPayloadAdmissionTests
{
    private const int LargestObservedHeroBearingPayloadBytes = 4_115_263;

    [Fact]
    public void IsAllowed_SmallestPayload_IsAccepted()
    {
        Assert.True(ServerInboundPayloadAdmission.IsAllowed(1));
    }

    [Fact]
    public void IsAllowed_LargestObservedHeroBearingPayload_IsAccepted()
    {
        Assert.True(ServerInboundPayloadAdmission.IsAllowed(
            LargestObservedHeroBearingPayloadBytes));
    }

    [Fact]
    public void IsAllowed_ExactMaximum_IsAccepted()
    {
        Assert.True(ServerInboundPayloadAdmission.IsAllowed(
            ServerInboundPayloadAdmission.MaximumPayloadBytes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void IsAllowed_InvalidPayloadSizes_AreRejected(int payloadBytes)
    {
        Assert.False(ServerInboundPayloadAdmission.IsAllowed(payloadBytes));
    }

    [Fact]
    public void IsAllowed_OneByteOverMaximum_IsRejected()
    {
        Assert.False(ServerInboundPayloadAdmission.IsAllowed(
            ServerInboundPayloadAdmission.MaximumPayloadBytes + 1));
    }
}
