using GameInterface.Services.UI.Patches;
using System;
using Xunit;

namespace GameInterface.Tests.Services.UI;

public sealed class MapNavigationReadinessPatchesTests
{
    [Fact]
    public void NullReferenceDuringReadiness_IsSuppressed()
    {
        Assert.Null(MapNavigationReadinessPatches.FilterTransientReadinessException(
            new NullReferenceException("map screen is not wired yet")));
    }

    [Fact]
    public void NonReadinessFailure_IsPropagated()
    {
        var failure = new InvalidOperationException("real map navigation failure");

        Assert.Same(
            failure,
            MapNavigationReadinessPatches.FilterTransientReadinessException(failure));
    }
}
