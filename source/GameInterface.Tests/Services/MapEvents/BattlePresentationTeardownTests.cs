using GameInterface.Services.MapEvents;
using System;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public class BattlePresentationTeardownTests
{
    [Fact]
    public void PrepareOnce_SignalsResultsBeforeGraphTeardownExactlyOnce()
    {
        var identity = new object();
        int signals = 0;

        bool first = BattlePresentationTeardown.TryPrepareOnce(
            identity,
            eligible: true,
            () => signals++);
        bool second = BattlePresentationTeardown.TryPrepareOnce(
            identity,
            eligible: true,
            () => signals++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, signals);
    }

    [Fact]
    public void IneligibleAttempt_DoesNotConsumeLaterEligibleSignal()
    {
        var identity = new object();
        int signals = 0;

        Assert.False(BattlePresentationTeardown.TryPrepareOnce(
            identity,
            eligible: false,
            () => signals++));
        Assert.True(BattlePresentationTeardown.TryPrepareOnce(
            identity,
            eligible: true,
            () => signals++));

        Assert.Equal(1, signals);
    }

    [Fact]
    public void FailedSignal_CanBeRetriedBeforeDestruction()
    {
        var identity = new object();
        int attempts = 0;

        Assert.Throws<InvalidOperationException>(() =>
            BattlePresentationTeardown.TryPrepareOnce(
                identity,
                eligible: true,
                () =>
                {
                    attempts++;
                    throw new InvalidOperationException("observer not ready");
                }));

        Assert.True(BattlePresentationTeardown.TryPrepareOnce(
            identity,
            eligible: true,
            () => attempts++));
        Assert.Equal(2, attempts);
    }
}
