using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieMissionOutcomeTests
{
    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    [InlineData(1, true, 4)]
    [InlineData(2, true, 5)]
    [InlineData(3, true, 6)]
    public void ResultCodec_RoundTripsOnlyBoundedOutcomeAndDisguise(
        int outcomeValue,
        bool disguised,
        int encoded)
    {
        var outcome = (FourberieInsideMissionOutcome)outcomeValue;
        Assert.Equal(encoded, FourberieInsideMissionResultCodec.Encode(outcome, disguised));
        FourberieInsideMissionResult decoded = FourberieInsideMissionResultCodec.Decode(encoded);
        Assert.Equal(outcome, decoded.Outcome);
        Assert.Equal(disguised, decoded.Disguised);
    }

    [Theory]
    [InlineData("AfterMathsGrabAndRun", 48)]
    [InlineData("AfterMathsBashing", 49)]
    [InlineData("AfterMathsIsoRob", 50)]
    [InlineData("AfterMathsPickFail", 51)]
    [InlineData("AfterMathsGrudgeAssassin", 52)]
    [InlineData("AfterMathsTavernBrawl", 53)]
    [InlineData("AfterMathsLarceny", 54)]
    [InlineData("AfterMathsEncounterAlley", 55)]
    public void ExactAftermathCallback_MapsToOneTypedServerOperation(
        string method,
        int operationValue)
    {
        var operation = (FourberieOperation)operationValue;
        Assert.Equal(operation, FourberieAuthorityPatches.InsideMissionOperationForMethod(method));
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.TypeName == "Fourberie.InsideMissionsHelper" &&
            spec.MethodName == method &&
            spec.Kind == FourberiePatchKind.InsideMissionOutcome);
    }

    [Fact]
    public void Protocol_RequiresCurrentSettlementAndExactOutcomeShape()
    {
        foreach (FourberieOperation operation in Enum.GetValues(typeof(FourberieOperation))
                     .Cast<FourberieOperation>()
                     .Where(value => value is >= FourberieOperation.CompleteGrabAndRun and <= FourberieOperation.CompleteAlleyFight))
        {
            Assert.True(FourberieOperationProtocol.IsRequestShapeValid(Request(operation, "town-a", 1)));
            Assert.True(FourberieOperationProtocol.IsRequestShapeValid(Request(operation, "town-a", 6)));
            Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(operation, "", 1)));
            Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(operation, "town-a", 0)));
            Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(operation, "town-a", 7)));
        }
    }

    [Fact]
    public void UnknownAftermathCallback_HasNoOperationRoute()
    {
        Assert.Null(FourberieAuthorityPatches.InsideMissionOperationForMethod("AfterMathsNotPinned"));
        Assert.Equal(FourberieInsideMissionOutcome.None,
            FourberieInsideMissionResultCodec.Decode(0).Outcome);
        Assert.Equal(FourberieInsideMissionOutcome.None,
            FourberieInsideMissionResultCodec.Decode(7).Outcome);
    }

    private static NetworkRequestFourberieOperation Request(
        FourberieOperation operation,
        string settlement,
        int outcome) =>
        new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"),
            1,
            0,
            operation,
            settlement,
            string.Empty,
            string.Empty,
            outcome,
            Array.Empty<FourberieTroopSelection>());
}
