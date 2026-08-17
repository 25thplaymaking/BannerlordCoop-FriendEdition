using GameInterface.Services.WorkshopMods.Fourberie;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieFinalizationTests
{
    [Fact]
    public void CampaignMutatingConsoleCommands_AreHostOnly()
    {
        int[] tokens = { 0x060002C8, 0x060002C9, 0x060002CA, 0x060002CC };
        Assert.All(tokens, token => Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
            spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ServerOnly)));
    }

    internal static readonly int[] SourceOwnedHelperTokens =
    {
        0x06000351, 0x06000359, 0x0600035B, 0x06000372, 0x06000374,
        0x06000378, 0x06000381, 0x0600038C, 0x060003A3, 0x060003AD,
        0x060003AF, 0x0600055C, 0x06000590, 0x060005A3,
    };

    [Fact]
    public void SourceOwnedHelpers_AreReachedOnlyThroughExistingAuthoritativeTransactions()
    {
        Assert.Equal(14, SourceOwnedHelperTokens.Distinct().Count());
        Assert.All(SourceOwnedHelperTokens, token => Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation));
    }

    [Fact]
    public void MissionPlumbing_IsClientLocalAndPersistentChildrenAreHostTransactions()
    {
        int[] local =
        {
            0x060003E2, 0x06000464, 0x0600046A, 0x060008C8, 0x060004A2,
            0x06000487, 0x0600048C, 0x060004CC, 0x060004DD,
        };
        Assert.All(local, token => Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
            spec.MetadataToken == token && spec.Kind == FourberiePatchKind.MissionLocal)));

        Assert.True(FourberieAuthorityPatches.MissionLocalPrefix());

        int[] persistentChildren = { 0x0600046E, 0x0600055B, 0x06000563, 0x060005A2, 0x060005B5 };
        Assert.All(persistentChildren, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == FourberiePatchKind.CriminalConsequence)));
    }

    [Fact]
    public void RemainingMenuBuildersAndConditions_AreRollbackProtectedPresentation()
    {
        int[] presentation =
        {
            0x06000342, 0x0600035F, 0x060003C2, 0x060003CF,
            0x06000624, 0x06000625, 0x060004C7,
            0x060007B2, 0x060007BB, 0x060007BD, 0x060007BE, 0x060008EF,
        };
        Assert.All(presentation, token => Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
            spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation)));
    }
}
