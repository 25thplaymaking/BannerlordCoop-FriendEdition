using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Moq;

namespace Common.Tests.Network.Coalescing;

/// <summary>
/// Covers the send gate, which lets the server hold coalesced updates about things no player can see.
/// </summary>
/// <remarks>
/// The whole reason holding is safe rather than reckless is that coalesced payloads MERGE, so an
/// update held across many changes still carries the correct end state. These pin that property, the
/// bound on how long a hold can last, and the fail-open behaviour — a gate is an optimisation, and a
/// wrongly held update would be a desync while a wrongly sent one is only a packet.
/// </remarks>
public class SendCoalescerGateTests
{
    private sealed class TestMessage : IMessage
    {
        public int Value { get; }
        public TestMessage(int value) => Value = value;
    }

    /// <summary>A gate driven by a predicate, so each test states exactly what it is holding.</summary>
    private sealed class PredicateGate : ICoalesceGate
    {
        private readonly Func<CoalesceKey, bool> shouldSend;
        public TimeSpan MaximumHold { get; set; } = TimeSpan.FromHours(1);
        public PredicateGate(Func<CoalesceKey, bool> shouldSend) => this.shouldSend = shouldSend;
        public bool ShouldSendNow(CoalesceKey key) => shouldSend(key);
    }

    private static (SendCoalescer coalescer, INetwork network, List<IMessage> sent) NewFixture()
    {
        var sent = new List<IMessage>();
        var network = new Mock<INetwork>();
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
        return (new SendCoalescer(), network.Object, sent);
    }

    private static int ValueOf(IMessage message) => Assert.IsType<TestMessage>(message).Value;

    private static ICoalescedPayload Sum(int amount) =>
        new SummedPayload<int>(amount, (running, next) => running + next, total => new TestMessage(total));

    [Fact]
    public void AHeldKeyIsNotSent()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(1));

        coalescer.Flush(network, new PredicateGate(_ => false));

        Assert.Empty(sent);
    }

    [Fact]
    public void AHeldKeyKeepsMergingAndSendsTheWholeTotalWhenReleased()
    {
        // The property the whole design rests on: holding costs latency, never correctness.
        var (coalescer, network, sent) = NewFixture();
        var key = new CoalesceKey("roster", "far", "troop");
        var closed = new PredicateGate(_ => false);

        coalescer.Enqueue(key, Sum(5));
        coalescer.Flush(network, closed);
        coalescer.Enqueue(key, Sum(7));
        coalescer.Flush(network, closed);
        coalescer.Enqueue(key, Sum(9));

        coalescer.Flush(network, new PredicateGate(_ => true));

        Assert.Equal(21, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void HoldingOneKeyDoesNotHoldAnother()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(1));
        coalescer.Enqueue(new CoalesceKey("roster", "near", "troop"), Sum(2));

        coalescer.Flush(network, new PredicateGate(key => key.InstanceId == "near"));

        Assert.Equal(2, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void SurvivingKeysKeepTheirRelativeOrder()
    {
        // Skipped keys must not reshuffle what does go out: the reliable stream depends on this order.
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "a", ""), Sum(1));
        coalescer.Enqueue(new CoalesceKey("roster", "hold", ""), Sum(2));
        coalescer.Enqueue(new CoalesceKey("roster", "b", ""), Sum(3));

        coalescer.Flush(network, new PredicateGate(key => key.InstanceId != "hold"));

        Assert.Equal(new[] { 1, 3 }, sent.Select(ValueOf).ToArray());
    }

    [Fact]
    public void AKeyHeldPastTheMaximumIsSentAnyway()
    {
        // Starvation guard: a gate that never opens must not strand an update forever.
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(4));

        var expired = new PredicateGate(_ => false) { MaximumHold = TimeSpan.Zero };
        coalescer.Flush(network, expired);

        Assert.Equal(4, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void AThrowingGateSends()
    {
        // Fail open. A broken gate must cost bandwidth, not correctness.
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(6));

        coalescer.Flush(network, new PredicateGate(_ => throw new InvalidOperationException("gate is broken")));

        Assert.Equal(6, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void ANullGateFlushesEverything()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(8));

        coalescer.Flush(network, null);

        Assert.Equal(8, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void FlushInstanceIgnoresTheGate()
    {
        // Destroy paths call FlushInstance to get an object's final state out ahead of its destroy.
        // A gate must never be able to hold that back.
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(3));

        coalescer.Flush(network, new PredicateGate(_ => false));
        Assert.Empty(sent);

        coalescer.FlushInstance("far", network);

        Assert.Equal(3, ValueOf(Assert.Single(sent)));
    }

    [Fact]
    public void AHeldKeyStillCountsAsPending()
    {
        // HasPending gates whether the server bothers scheduling a flush at all; if holding cleared it,
        // held updates would never be reconsidered.
        var (coalescer, network, _) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(1));

        coalescer.Flush(network, new PredicateGate(_ => false));

        Assert.True(coalescer.HasPending);
    }

    [Fact]
    public void DroppingAHeldInstanceDiscardsIt()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.Enqueue(new CoalesceKey("roster", "far", "troop"), Sum(2));
        coalescer.Flush(network, new PredicateGate(_ => false));

        coalescer.DropInstance("far");
        coalescer.Flush(network, new PredicateGate(_ => true));

        Assert.Empty(sent);
        Assert.False(coalescer.HasPending);
    }
}
