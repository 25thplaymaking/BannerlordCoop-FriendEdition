using Common.Network;
using E2E.Tests.Services.MapEvents;
using GameInterface.Services.WorkshopMods.Diplomacy;
using LiteNetLib;
using System;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Transport-level fail-closed coverage for the unified Diplomacy command route. The E2E fixture
/// intentionally does not load the digest-pinned workshop assembly, so accepted gameplay is
/// covered by pinned-manifest and executor tests while this fixture proves an unavailable or
/// malformed route cannot fall through to local campaign mutation.
/// </summary>
public sealed class DiplomacyOperationRoutingTests : MapEventTestBase
{
    public DiplomacyOperationRoutingTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void Server_DisconnectsMalformedOperationEnvelope()
    {
        var client = Clients.First();

        client.Call(() => client.Resolve<INetwork>().SendAll(
            new NetworkRequestDiplomacyOperation(
                "not-a-session",
                requestId: 1,
                expectedRevision: 0,
                DiplomacyOperation.SendMessenger,
                "hero.target",
                string.Empty,
                intValue: 0)));

        Assert.Equal(ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
    }

    [Fact]
    public void Server_DisconnectsGameplayRequestWhenPinnedRouteIsUnavailable()
    {
        var client = Clients.First();

        client.Call(() => client.Resolve<INetwork>().SendAll(
            new NetworkRequestDiplomacyOperation(
                Guid.NewGuid().ToString("N"),
                requestId: 1,
                expectedRevision: 0,
                DiplomacyOperation.SendMessenger,
                "hero.target",
                string.Empty,
                intValue: 0)));

        Assert.Equal(ConnectionState.ShutdownRequested, client.NetPeer.ConnectionState);
    }
}
