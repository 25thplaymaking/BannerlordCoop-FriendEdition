using Common.Messaging;
using Common.Tests.Utils;
using Coop.Core.Server.Services.Time.Handlers;
using Coop.Core.Server.Services.Time.Messages;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Heroes.Messages;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Time;

public class TimeHandlerTests
{
    [Fact]
    public void Dispose_RemovesAllHandlers()
    {
        var broker = new TestMessageBroker();
        var timeControlInterface = new Mock<ITimeControlInterface>();
        var handler = new TimeHandler(broker, timeControlInterface.Object);

        Assert.True(broker.GetTotalSubscribers() > 0);

        handler.Dispose();

        Assert.Equal(0, broker.GetTotalSubscribers());
    }

    [Fact]
    public void BareNetworkTimeRequest_WithoutRouter_IsNotSubscribed()
    {
        var broker = new TestMessageBroker();
        var timeControlInterface = new Mock<ITimeControlInterface>();
        var handler = new TimeHandler(broker, timeControlInterface.Object);

        Assert.Equal(-1, broker.GetSubscriberCountForType<NetworkRequestTimeSpeedChange>());
        broker.Publish(this, new NetworkRequestTimeSpeedChange(TimeControlEnum.Play_2x));

        timeControlInterface.Verify(
            m => m.ServerSetTimeControl(It.IsAny<TimeControlEnum>()),
            Times.Never);
    }

    [Fact]
    public void TimeSpeedChangedAttempt_IsApplied()
    {
        var broker = new TestMessageBroker();
        var timeControlInterface = new Mock<ITimeControlInterface>();
        var handler = new TimeHandler(broker, timeControlInterface.Object);

        handler.Handle_TimeSpeedChanged(
            new MessagePayload<TimeSpeedChangedAttempted>(
                this,
                new TimeSpeedChangedAttempted(TimeControlEnum.Pause)));

        timeControlInterface.Verify(
            m => m.ServerSetTimeControl(TimeControlEnum.Pause),
            Times.Once);
    }
}
