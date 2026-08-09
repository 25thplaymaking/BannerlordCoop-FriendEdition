using Common.Messaging;
using Common.PacketHandlers;
using Common.Serialization;
using Coop.Core.Server;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Coop.Tests.Server;

public class ServerInboundPayloadProcessorTests
{
    [Fact]
    public void DeserializeFailure_IsolatesOnlyMalformedPeer_AndLaterValidPeerIsHandled()
    {
        var serializer = new DelegateSerializer(data =>
        {
            if (data[0] == 0)
                throw new SerializationException("Malformed protobuf payload");

            return new ProbeMessage();
        });
        var handledPeers = new List<string>();
        var isolatedPeers = new List<(string Peer, int PayloadBytes, Exception Failure)>();
        var processor = CreateProcessor(serializer, handledPeers, isolatedPeers);

        Assert.False(processor.Process("malformed-peer", new byte[] { 0 }));
        Assert.True(processor.Process("valid-peer", new byte[] { 1 }));

        var isolated = Assert.Single(isolatedPeers);
        Assert.Equal("malformed-peer", isolated.Peer);
        Assert.Equal(1, isolated.PayloadBytes);
        Assert.IsType<SerializationException>(isolated.Failure);
        Assert.Equal(new[] { "valid-peer" }, handledPeers);
    }

    [Fact]
    public void DispatchFailure_IsolatesOnlyOffendingPeer_AndLaterValidPeerIsHandled()
    {
        var serializer = new DelegateSerializer(_ => new ProbeMessage());
        var handledPeers = new List<string>();
        var isolatedPeers = new List<(string Peer, int PayloadBytes, Exception Failure)>();
        var processor = new ServerInboundPayloadProcessor<string>(
            serializer,
            (_, _) => throw new InvalidOperationException("Unexpected packet"),
            (peer, _) =>
            {
                if (peer == "throwing-peer")
                    throw new InvalidOperationException("Handler rejected message");

                handledPeers.Add(peer);
            },
            (peer, size, failure) => isolatedPeers.Add((peer, size, failure)));

        Assert.False(processor.Process("throwing-peer", new byte[] { 1, 2 }));
        Assert.True(processor.Process("later-valid-peer", new byte[] { 3 }));

        var isolated = Assert.Single(isolatedPeers);
        Assert.Equal("throwing-peer", isolated.Peer);
        Assert.Equal(2, isolated.PayloadBytes);
        Assert.IsType<InvalidOperationException>(isolated.Failure);
        Assert.Equal(new[] { "later-valid-peer" }, handledPeers);
    }

    [Fact]
    public void UnsupportedDeserializedType_IsIsolated()
    {
        var isolatedPeers = new List<(string Peer, int PayloadBytes, Exception Failure)>();
        var processor = CreateProcessor(
            new DelegateSerializer(_ => new object()),
            new List<string>(),
            isolatedPeers);

        Assert.False(processor.Process("unsupported-peer", new byte[] { 1 }));

        var isolated = Assert.Single(isolatedPeers);
        Assert.Equal("unsupported-peer", isolated.Peer);
        Assert.IsType<System.IO.InvalidDataException>(isolated.Failure);
    }

    private static ServerInboundPayloadProcessor<string> CreateProcessor(
        ICommonSerializer serializer,
        List<string> handledPeers,
        List<(string Peer, int PayloadBytes, Exception Failure)> isolatedPeers) =>
        new ServerInboundPayloadProcessor<string>(
            serializer,
            (_, _) => throw new InvalidOperationException("Unexpected packet"),
            (peer, _) => handledPeers.Add(peer),
            (peer, size, failure) => isolatedPeers.Add((peer, size, failure)));

    private sealed class ProbeMessage : IMessage
    {
    }

    private sealed class DelegateSerializer : ICommonSerializer
    {
        private readonly Func<byte[], object> deserialize;

        public DelegateSerializer(Func<byte[], object> deserialize)
        {
            this.deserialize = deserialize;
        }

        public T Deserialize<T>(byte[] data) => (T)Deserialize(data);

        public object Deserialize(byte[] data) => deserialize(data);

        public byte[] Serialize(object obj) => throw new NotSupportedException();
    }
}
