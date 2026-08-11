using GameInterface.Services.WorkshopMods.Fourberie;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieManifestTests
{
    private sealed class ShapeFixture
    {
        private static void Target(ref int value, bool enabled, List<string> names)
        {
        }
    }

    [Theory]
    [InlineData("v1.4.7.5", "FD1C02158817FAE5B90E3C121DA474096CAA368CB35495D83CE81EA49D860C71", true)]
    [InlineData("v1.4.7.4", "FD1C02158817FAE5B90E3C121DA474096CAA368CB35495D83CE81EA49D860C71", false)]
    [InlineData("v1.4.7.5", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void IdentityGate_RequiresExactCreatorBinary(string version, string hash, bool expected)
    {
        Assert.Equal(expected, FourberieCompatibilityManifest.IsSupportedIdentity(version, hash));
    }

    [Fact]
    public void MethodShapeGate_MatchesReturnByRefAndGenericArgumentsExactly()
    {
        var spec = new FourberieMethodSpec(
            typeof(ShapeFixture).FullName,
            "Target",
            "System.Void",
            FourberiePatchKind.ServerOnly,
            "System.Int32&",
            "System.Boolean",
            "System.Collections.Generic.List`1[System.String]");

        Assert.NotNull(spec.Resolve(typeof(ShapeFixture).Assembly));
    }

    [Theory]
    [InlineData("Fourberie.CriminalVM", "AgentsEnlistRoutine")]
    [InlineData("Fourberie.FourbBanditBehavior", "FourbRecruitBandit")]
    [InlineData("Fourberie.HelperSubInsuScam", "SpawnBandits")]
    [InlineData("Fourberie.Main", "OnMissionBehaviorInitialize")]
    public void UnsafeFeatureEntryPoints_AreExplicitlyFailClosed(string type, string method)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind == FourberiePatchKind.UnsupportedPlayerAction);
    }

    [Theory]
    [InlineData("Fourberie.FourberieBehavior", "AddGameMenus")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "SHAddGameMenus")]
    [InlineData("Fourberie.FourbEscapeBehavior", "FourbEscapMenu")]
    [InlineData("Fourberie.FourbFightClubBehavior", "PitAddGameMenus")]
    [InlineData("Fourberie.FourbBanditBehavior", "BanditAddMenu")]
    [InlineData("Fourberie.FourbRecruitableBehavior", "AddGameMenus")]
    [InlineData("Fourberie.FourbContactMenu", "AddContactMenusF")]
    [InlineData("Fourberie.FourbContractBehavior", "AddGameMenus")]
    [InlineData("Fourberie.HomesSteadsAddOn", "MenuHomeSteads")]
    [InlineData("Fourberie.Main", "OnApplicationTick")]
    public void PresentationEntryPoints_AreClientOnly(string type, string method)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind == FourberiePatchKind.ClientPresentation);
    }

    [Theory]
    [InlineData("Fourberie.Main", "InitializeCampaignBehaviors", "BehaviorsWithoutModels")]
    [InlineData("Fourberie.Main", "OnGameInitializationFinished", "RefreshHeroDicoOnly")]
    public void ReplacedInitializationEntryPoints_UseTheirAuditedAdapters(
        string type,
        string method,
        string expectedKind)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind.ToString() == expectedKind);
    }
}
