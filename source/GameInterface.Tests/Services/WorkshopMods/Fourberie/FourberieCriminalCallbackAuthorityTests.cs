using Fourberie;
using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieCriminalCallbackAuthorityTests
{
    private static readonly int[] Tokens =
    {
        0x060007C1, 0x060007E8, 0x060007EA,
        0x0600094E, 0x0600096D, 0x0600098E, 0x06000998,
        0x060009A3, 0x060009B1, 0x060009B6, 0x060009B7,
        0x060009C0, 0x060009D4, 0x060009E6,
        0x060009F9, 0x060009FA, 0x060009FE, 0x06000A03,
        0x06000A11, 0x06000A12, 0x06000A13, 0x06000A15,
    };

    [Fact]
    public void LegacyCallbackProtocol_AllowsOnlyPinnedStatelessConsequences()
    {
        Assert.All(Tokens, token => Assert.True(Valid(token)));
        Assert.False(Valid(0x060007D7));
        Assert.False(Valid(0x06000A3D));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(Tokens[0], settlement: string.Empty)));
    }

    [Fact]
    public void Manifest_RoutesEveryPinnedLegacyConsequence()
    {
        Assert.All(Tokens, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == FourberiePatchKind.LegacyCallback)));
    }

    [Fact]
    public void PresentationSnapshot_RestoresPersistedCollectionsAfterUiEvaluation()
    {
        FourberieBehavior.Reset();
        FourberieBehavior._crimeValue[42] = 7;
        FourberiePresentationState snapshot = FourberiePresentationState.Capture(typeof(FourberieBehavior));
        FourberieBehavior._crimeValue[42] = 99;
        FourberieBehavior._territoryList.Add("client-only");

        snapshot.Restore();

        Assert.Equal(7, FourberieBehavior._crimeValue[42]);
        Assert.DoesNotContain("client-only", FourberieBehavior._territoryList);
    }

    private static bool Valid(int token) =>
        FourberieOperationProtocol.IsRequestShapeValid(Request(token, "settlement-current"));

    private static NetworkRequestFourberieOperation Request(int token, string settlement) =>
        new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.CommitLegacyCallback,
            settlement, string.Empty, string.Empty, token,
            Array.Empty<FourberieTroopSelection>(), Array.Empty<FourberieItemSelection>());
}
