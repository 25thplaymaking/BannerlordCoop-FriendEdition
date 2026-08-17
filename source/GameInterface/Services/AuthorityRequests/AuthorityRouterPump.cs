using Common;
using System;

namespace GameInterface.Services.AuthorityRequests;

/// <summary>
/// Drives <see cref="IAuthorityRequestRouter.Update"/> against whichever router the CURRENT container
/// owns, rather than one instance captured at mod initialisation.
///
/// Joining a session calls <c>DestroyContainer()</c> and builds a fresh container
/// (<c>CoopartiveMultiplayerExperience.StartAsClientCore</c>/<c>StartAsServerCore</c>), so the router
/// resolved when the module loaded is not the router the session's handlers register their routes on.
/// Adding that early instance straight into the update list left the live router unpolled, and
/// <c>Poll</c> is what applies an accepted reply: every fire-and-forget <c>Submit</c> sat in
/// <c>AcceptedResultReceived</c> forever, never reaching <c>ReplicaApplied</c>, never even tripping its
/// own apply timeout. Entering a settlement, changing time speed, siege and kingdom actions all went
/// silently nowhere — the request was sent and answered, and the client simply never applied it. Only
/// routes that call <see cref="IAuthorityRouteHandle{TIntent,TResult}.SubmitBlocking"/> worked, because
/// that pumps <c>Poll</c> itself.
///
/// Resolving per frame is deliberate: the container is rebuilt on every session start, and a cached
/// reference cannot survive that.
/// </summary>
public sealed class AuthorityRouterPump : IUpdateable
{
    /// <summary>Matches the router's own priority so ordering is unchanged by the indirection.</summary>
    public int Priority => UpdatePriority.MainLoop.GameThread - 1;

    public void Update(TimeSpan frameTime)
    {
        if (!ContainerProvider.TryResolve<IAuthorityRequestRouter>(out var router)) return;

        router.Update(frameTime);
    }
}
