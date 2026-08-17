using GameInterface.Services.WorkshopMods.Fourberie;
using System.Linq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberiePresentationAuthorityTests
{
    [Fact]
    public void ExactVmAndMissionHelpers_AreClientOnlyAndNotServerCommands()
    {
        int[] expected =
        {
            0x060001A2, 0x060002C2, 0x060002C3, 0x060002CB, 0x0600033C, 0x0600033E,
            0x060003EB, 0x0600046D, 0x06000474, 0x06000475, 0x0600047A, 0x0600047C,
            0x0600047E, 0x06000482, 0x06000485, 0x06000499, 0x060004C6, 0x06000512,
            0x06000523, 0x06000526, 0x06000529, 0x0600052D,
            0x0600031B, 0x0600031D, 0x0600031E, 0x06000322, 0x06000323, 0x06000329,
            0x06000341, 0x0600034B, 0x0600034E, 0x0600037B, 0x0600037C, 0x0600037E,
            0x0600037F, 0x06000384, 0x06000385, 0x06000386, 0x06000387, 0x060003B7,
            0x060003B9, 0x060003BB, 0x060003C1, 0x060003C3, 0x060007CC, 0x0600080D,
        };

        foreach (int token in expected)
        {
            FourberieMethodSpec route = Assert.Single(FourberieCompatibilityManifest.Methods,
                spec => spec.MetadataToken == token);
            Assert.Equal(FourberiePatchKind.ClientPresentation, route.Kind);
        }
    }

    [Fact]
    public void ExistingMenuAndMutationGuards_HaveOneExactOwner()
    {
        var expected = new[]
        {
            (Type: "Fourberie.FourbBanditBehavior", Method: "BanditOnGaMenOpened", Kind: FourberiePatchKind.ClientPresentation),
            (Type: "Fourberie.FourberieBehavior", Method: "FourbOnGaMenOpened", Kind: FourberiePatchKind.ClientPresentation),
            (Type: "Fourberie.FourberieBehavior", Method: "HideoutDeactivated", Kind: FourberiePatchKind.ServerMutation),
            (Type: "Fourberie.FourberieBehavior", Method: "OnDoneEnslaved", Kind: FourberiePatchKind.EnslavePrisonersConsequence),
        };

        foreach (var item in expected)
        {
            Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.TypeName == item.Type && spec.MethodName == item.Method && spec.Kind == item.Kind));
        }
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public void CanonicalPresentationCleanup_IsAnAuthenticatedAbsoluteTransaction(int operationValue)
    {
        var operation = (FourberieOperation)operationValue;
        var request = new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, operation,
            string.Empty, string.Empty, string.Empty, 0,
            Array.Empty<FourberieTroopSelection>());
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
        Assert.True(FourberieOperationProtocol.IsAbsoluteSetting(operation));
    }

    [Fact]
    public void ExactCanonicalPresentationCallbacks_HaveTypedRoutes()
    {
        Assert.All(new[] { 0x060007B7, 0x060007B8, 0x060007B9 }, token =>
            Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
                spec.MetadataToken == token && spec.Kind == FourberiePatchKind.SchemeRoomOpen));
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x06000314 && spec.Kind == FourberiePatchKind.DominanceCondition);
    }
}
