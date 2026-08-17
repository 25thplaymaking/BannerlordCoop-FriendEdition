using Autofac;
using Common.Messaging;
using GameInterface;
using GameInterface.Services.AuthorityRequests;
using System;
using Xunit;

namespace GameInterface.Tests.Services.AuthorityRequests;

[Collection(ModInformationRoleCollection.Name)]
public sealed class AuthorityRouterPumpTests : IDisposable
{
    public void Dispose() => ContainerProvider.Clear();

    private sealed class CountingRouter : IAuthorityRequestRouter
    {
        public int Updates { get; private set; }
        public int Priority => 0;
        public void Update(TimeSpan frameTime) => Updates++;
        public IAuthorityRouteHandle<TIntent, TResult> Register<TIntent, TRequest, TResult>(
            AuthorityRoute<TIntent, TRequest, TResult> route)
            where TRequest : IMessage
            where TResult : IMessage => throw new NotSupportedException();
        public bool IsRegistered(string routeId, AuthorityRouteKind kind) => false;
        public void Dispose() { }
    }

    private static ILifetimeScope ContainerFor(CountingRouter router)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(router).As<IAuthorityRequestRouter>();
        return builder.Build();
    }

    /// <summary>
    /// Joining a session rebuilds the container, so the router resolved when the module loaded is not
    /// the one the session registers routes on. CoopMod used to add that early instance straight into
    /// the update list, which left the live router unpolled — and Poll is what applies an accepted
    /// reply, so every fire-and-forget Submit stalled in AcceptedResultReceived for the whole session
    /// (entering a settlement, time speed, siege and kingdom actions all silently did nothing).
    /// The pump must follow the current container.
    /// </summary>
    [Fact]
    public void PumpFollowsTheContainerAcrossARebuild()
    {
        var beforeJoin = new CountingRouter();
        var afterJoin = new CountingRouter();
        var pump = new AuthorityRouterPump();

        using var first = ContainerFor(beforeJoin);
        ContainerProvider.SetContainer(first);
        pump.Update(TimeSpan.Zero);

        Assert.Equal(1, beforeJoin.Updates);
        Assert.Equal(0, afterJoin.Updates);

        // The session start disposes the old container and installs a new one.
        using var second = ContainerFor(afterJoin);
        ContainerProvider.SetContainer(second);
        pump.Update(TimeSpan.Zero);

        Assert.Equal(1, afterJoin.Updates);
        Assert.Equal(1, beforeJoin.Updates);   // the stale router must stop receiving updates
    }

    /// <summary>Between sessions there is no container; the pump must be a no-op, not a throw.</summary>
    [Fact]
    public void PumpIsQuietWithoutAContainer()
    {
        ContainerProvider.Clear();

        new AuthorityRouterPump().Update(TimeSpan.Zero);
    }
}
