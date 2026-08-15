using GameInterface.Services.WorkshopMods.Fourberie;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieBanditAuthorityTests
{
    [Fact]
    public void ExactBanditPresentationSurface_ExcludesEverySelectionConsequence()
    {
        int[] local =
        {
            0x06000013, 0x06000014, 0x06000278, 0x06000291, 0x06000292,
            0x0600029A, 0x060002B8, 0x060002B9, 0x060002BA, 0x060002BD,
        };
        Assert.All(local, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation)));

        int[] consequences =
        {
            0x06000260, 0x06000272, 0x06000280, 0x06000282, 0x06000285,
            0x060002C0, 0x06000745, 0x0600074C, 0x0600074E, 0x0600075E,
            0x06000766, 0x06000776, 0x0600077C, 0x0600077E, 0x06000780,
            0x06000782, 0x06000791, 0x06000795,
        };
        Assert.All(consequences, token => Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation));
    }
}
