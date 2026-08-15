using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieStealthAuthorityTests
{
    [Fact]
    public void EveryStealthEvent_HasOneBoundedAuthenticatedRequestShape()
    {
        foreach (FourberieStealthEvent stealthEvent in Enum.GetValues(typeof(FourberieStealthEvent)))
        {
            bool targetRequired = stealthEvent == FourberieStealthEvent.LordWounded;
            Assert.True(FourberieOperationProtocol.IsRequestShapeValid(
                Request(stealthEvent, targetRequired ? "hero-target" : string.Empty)));
            Assert.False(FourberieOperationProtocol.IsRequestShapeValid(
                Request(stealthEvent, targetRequired ? string.Empty : "hero-target")));
        }

        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request((FourberieStealthEvent)0, string.Empty)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request((FourberieStealthEvent)17, string.Empty)));
    }

    [Fact]
    public void ExactStealthSurface_SeparatesLocalSimulationFromHostConsequences()
    {
        AssertRoutes(FourberiePatchKind.StealthMissionLocal, 0x06000504, 0x06000528);
        AssertRoutes(FourberiePatchKind.StealthHit, 0x060004FD);
        AssertRoutes(FourberiePatchKind.StealthMissionEnd, 0x06000503);
        AssertRoutes(FourberiePatchKind.StealthMilitiaPayment, 0x0600050C);
        AssertRoutes(FourberiePatchKind.StealthMilitiaChoice, 0x0600050D);
        AssertRoutes(FourberiePatchKind.StealthAbortContract, 0x0600050F);
        AssertRoutes(FourberiePatchKind.StealthAlertConsequence, 0x06000510);
        AssertRoutes(FourberiePatchKind.StealthAnswer, 0x06000511);
        AssertRoutes(FourberiePatchKind.StealthAgentRemoved, 0x0600052A);
        AssertRoutes(FourberiePatchKind.StealthAlarm, 0x0600052E);
        AssertRoutes(FourberiePatchKind.StealthScandalSuccess, 0x0600092A);
        AssertRoutes(FourberiePatchKind.StealthPrisonSuccess, 0x0600092D);

        int[] persistent =
        {
            0x060004FD, 0x06000503, 0x0600050C, 0x0600050D, 0x0600050F, 0x06000510,
            0x06000511, 0x0600052A, 0x0600052E, 0x0600092A, 0x0600092D,
        };
        Assert.All(persistent, token => Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation));
    }

    private static void AssertRoutes(FourberiePatchKind kind, params int[] tokens) =>
        Assert.All(tokens, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == kind)));

    private static NetworkRequestFourberieOperation Request(
        FourberieStealthEvent stealthEvent,
        string target) =>
        new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"),
            1,
            0,
            FourberieOperation.CommitStealthEvent,
            "settlement-current",
            target,
            string.Empty,
            (int)stealthEvent,
            Array.Empty<FourberieTroopSelection>());
}
