using GameInterface.Services.WorkshopMods.Fourberie;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

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
}
