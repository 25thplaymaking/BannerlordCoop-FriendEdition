using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieFightClubOutcomeTests
{
    [Fact]
    public void ResultCodec_RoundTripsEveryCareerInputWithoutFreeFormValues()
    {
        var expected = new FourberieFightClubResult(
            fightType: 4,
            round: 12,
            knockouts: 7,
            trialFightType: 3,
            training: false,
            gangTrial: false,
            patronTrial: true,
            handToHand: true);

        int encoded = FourberieFightClubResultCodec.Encode(expected);
        Assert.True(FourberieFightClubResultCodec.IsValid(encoded));
        FourberieFightClubResult actual = FourberieFightClubResultCodec.Decode(encoded);
        Assert.Equal(expected.FightType, actual.FightType);
        Assert.Equal(expected.Round, actual.Round);
        Assert.Equal(expected.Knockouts, actual.Knockouts);
        Assert.Equal(expected.TrialFightType, actual.TrialFightType);
        Assert.Equal(expected.PatronTrial, actual.PatronTrial);
        Assert.Equal(expected.HandToHand, actual.HandToHand);
    }

    [Fact]
    public void ResultCodec_RejectsImpossibleModesAndUnboundedCounters()
    {
        Assert.False(FourberieFightClubResultCodec.IsValid(0));
        Assert.False(FourberieFightClubResultCodec.IsValid(
            FourberieFightClubResultCodec.Encode(new FourberieFightClubResult(2, 1, 0, 0, false, false, false, false))));
        Assert.False(FourberieFightClubResultCodec.IsValid(
            FourberieFightClubResultCodec.Encode(new FourberieFightClubResult(1, 31, 0, 0, false, false, false, false))));
        Assert.False(FourberieFightClubResultCodec.IsValid(
            FourberieFightClubResultCodec.Encode(new FourberieFightClubResult(1, 1, 0, 0, true, true, false, false))));
    }

    [Fact]
    public void ProtocolAndManifest_PinOneAuthenticatedCareerCommit()
    {
        int encoded = FourberieFightClubResultCodec.Encode(
            new FourberieFightClubResult(3, 3, 2, 0, false, false, false, false));
        var valid = Request("town-a", string.Empty, encoded);
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(valid));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request("", string.Empty, encoded)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request("town-a", string.Empty, -1)));
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.TypeName == "Fourberie.FourbFightClubController" &&
            spec.MethodName == "OnEndMissionRequest" &&
            spec.Kind == FourberiePatchKind.FightClubOutcome);
    }

    [Fact]
    public void LifecycleProtocol_PinsAdmissionEnrollmentPatronStableAndRecruitment()
    {
        int admission = FourberieFightClubResultCodec.Encode(
            new FourberieFightClubResult(4, 1, 0, 0, false, false, false, true));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.StartFightClubMatch, "town-a", string.Empty, admission)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.EnrollFightClub, "town-a", "hero-a", 0)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.RefuteFightClubPatron, "town-a", "hero-a", 0)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.OwnFightClubStable, "town-a", string.Empty, 0)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.RefreshFightClubMenu, "town-a", string.Empty, 0)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0,
            FourberieOperation.RecruitFightClubStable,
            "town-a", string.Empty, string.Empty, 0,
            new[] { new FourberieTroopSelection("troop-a", 2) })));

        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(
            Request(FourberieOperation.EnrollFightClub, "town-a", string.Empty, 0)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0,
            FourberieOperation.RecruitFightClubStable,
            "town-a", string.Empty, string.Empty, 0,
            Array.Empty<FourberieTroopSelection>())));

        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.TypeName == "Fourberie.FourbFightClubBehavior+<>c__DisplayClass18_0" &&
            spec.MethodName == "<PitTrainingStart>b__0" &&
            spec.Kind == FourberiePatchKind.FightClubAdmission);
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.TypeName == "Fourberie.FourbFightClubBehavior+<>c__DisplayClass19_0" &&
            spec.MethodName == "<PitFightStart>b__0" &&
            spec.Kind == FourberiePatchKind.FightClubAdmission);
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MethodName == "RecruitLadsOnDoneClicked" &&
            spec.Kind == FourberiePatchKind.FightClubStableRecruitment);
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MethodName == "fb_menu_main_on_init" &&
            spec.Kind == FourberiePatchKind.FightClubMenuRefresh);
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MethodName == "PatronPaycheck" &&
            spec.Kind == FourberiePatchKind.FightClubPatronPayment);
    }

    private static NetworkRequestFourberieOperation Request(string settlement, string patron, int result) =>
        Request(FourberieOperation.CompleteFightClubMatch, settlement, patron, result);

    private static NetworkRequestFourberieOperation Request(
        FourberieOperation operation,
        string settlement,
        string patron,
        int result) =>
        new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0,
            operation,
            settlement, patron, string.Empty, result,
            Array.Empty<FourberieTroopSelection>());
}
