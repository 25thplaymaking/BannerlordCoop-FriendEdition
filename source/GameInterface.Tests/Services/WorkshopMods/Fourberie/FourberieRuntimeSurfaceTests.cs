using System;
using GameInterface.Services.WorkshopMods.Fourberie;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieRuntimeSurfaceTests
{
    private static readonly string[] CompleteModelSet =
    {
        "Fourberie.FModelDamage",
        "Fourberie.FModelDeath",
        "Fourberie.FModelClanFinance",
        "Fourberie.FModelCrime",
        "Fourberie.FModelLoyalty",
        "Fourberie.FModelSecurity",
        "Fourberie.FModelMobileFood",
        "Fourberie.FModelAccess",
        "Fourberie.FModelDonation",
        "Fourberie.FModelDiplo",
        "Fourberie.FModelPower",
        "Fourberie.FModelMapSpeed",
        "Fourberie.FModelPrice",
        "Fourberie.FModelPartyTransition",
    };

    [Fact]
    public void CompleteModelManagerSurface_IsAcceptedIncludingMissionDamageModel()
    {
        Assert.True(FourberieRuntimeSurface.TryAssertExactActiveModelNames(
            CompleteModelSet,
            out var failure), failure);
    }

    [Fact]
    public void PartialModelManagerSurface_IsRejected()
    {
        var partial = CompleteModelSet[..^1];

        Assert.False(FourberieRuntimeSurface.TryAssertExactActiveModelNames(partial, out var failure));
        Assert.Contains("FModelPartyTransition", failure, StringComparison.Ordinal);
    }
}
