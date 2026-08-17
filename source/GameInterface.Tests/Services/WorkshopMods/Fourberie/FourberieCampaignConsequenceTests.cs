using System;
using System.Linq;
using GameInterface.Services.WorkshopMods.Fourberie;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieCampaignConsequenceTests
{
    [Fact]
    public void ExactCallbacks_UseTypedHostConsequences()
    {
        int[] tokens = { 0x060002DF, 0x06000462, 0x0600032F, 0x06000336, 0x060004BB };
        Assert.All(tokens, token => Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
            spec.MetadataToken == token && spec.Kind == FourberiePatchKind.CampaignConsequence)));
    }

    [Fact]
    public void Protocol_BindsOnlyCompanionRelationToATarget()
    {
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(Request(
            FourberieCampaignConsequence.StartAssassination, string.Empty)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(Request(
            FourberieCampaignConsequence.SafehouseCompanionRelation, "hero.companion")));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(
            FourberieCampaignConsequence.SafehouseCompanionRelation, string.Empty)));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(Request(
            FourberieCampaignConsequence.HealWound, "forged.target")));
    }

    [Fact]
    public void MinorRecruitment_IsBoundedTypedRosterCommand()
    {
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x06000621 && spec.Kind == FourberiePatchKind.MinorRecruitmentConsequence);
        var request = new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.RecruitMinorTroops,
            "town.test", string.Empty, string.Empty, 0,
            new[] { new FourberieTroopSelection("minor.test", 30) },
            Array.Empty<FourberieItemSelection>(), Array.Empty<string>(),
            Array.Empty<FourberieRosterSelection>());
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
        var excessive = new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.RecruitMinorTroops,
            "town.test", string.Empty, string.Empty, 0,
            new[] { new FourberieTroopSelection("minor.test", 31) },
            Array.Empty<FourberieItemSelection>(), Array.Empty<string>(),
            Array.Empty<FourberieRosterSelection>());
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(excessive));
    }

    [Fact]
    public void KingdomLeave_AcceptsOnlyTaleWorldsChoices()
    {
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x06000375 && spec.Kind == FourberiePatchKind.KingdomLeaveConsequence);
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(LeaveRequest("keep")));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(LeaveRequest("dontkeep")));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(LeaveRequest("forged")));
    }

    [Fact]
    public void GuardKills_AreSettlementBoundAndHostScored()
    {
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x06000313 && spec.Kind == FourberiePatchKind.GuardKillConsequence);
        var valid = new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.CommitGuardKills,
            "town.test", string.Empty, string.Empty, 4,
            Array.Empty<FourberieTroopSelection>(), Array.Empty<FourberieItemSelection>(),
            Array.Empty<string>(), Array.Empty<FourberieRosterSelection>());
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(valid));
    }

    [Fact]
    public void SafehouseEncounter_UsesBoundedMissionResult()
    {
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x060004A3 && spec.Kind == FourberiePatchKind.SafehouseEncounterConsequence);
        Assert.Contains(FourberieCompatibilityManifest.Methods, spec =>
            spec.MetadataToken == 0x060004A2 && spec.Kind == FourberiePatchKind.MissionLocal);
        var request = new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.CommitSafehouseEncounter,
            "hideout.test", string.Empty, string.Empty, 2,
            Array.Empty<FourberieTroopSelection>(), Array.Empty<FourberieItemSelection>(),
            Array.Empty<string>(), Array.Empty<FourberieRosterSelection>());
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(request));
    }

    [Fact]
    public void CriminalConsequences_AreExactTypedHostTransactions()
    {
        int[] tokens =
        {
            0x0600032E, 0x06000810, 0x06000824, 0x06000825,
            0x0600084D, 0x06000850, 0x06000866, 0x0600086E, 0x06000877,
            0x06000A3D, 0x06000A3F, 0x06000A41,
            0x060005DB, 0x06000A52,
            0x0600031A, 0x0600046E,
            0x0600055B, 0x06000563, 0x060005A2, 0x060005B5,
            0x0600095B, 0x06000991, 0x060009BB, 0x060009F4,
            0x0600082E, 0x0600082F, 0x06000830, 0x06000831, 0x06000833, 0x06000835,
        };
        Assert.All(tokens, token => Assert.Single(FourberieCompatibilityManifest.Methods.Where(spec =>
            spec.MetadataToken == token && spec.Kind == FourberiePatchKind.CriminalConsequence)));

        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.PrisonBreakSuccess)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.Fortune, secondary: "attempts.10")));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.ClearRivalry, target: "hero.rival")));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.GatherFollowers, objects: new[] { "party.follower" })));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.PromoteCompanion, target: "hero.companion")));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.Fortune, secondary: "attempts.11")));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.GatherFollowers)));
        Assert.True(FourberieOperationProtocol.IsRequestShapeValid(CriminalRequest(
            FourberieCriminalConsequence.PayRiotInfluence, target: "hero.victim")));
    }

    private static NetworkRequestFourberieOperation LeaveRequest(string choice) =>
        new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0, FourberieOperation.LeaveKingdom,
            string.Empty, string.Empty, choice, 0,
            Array.Empty<FourberieTroopSelection>(), Array.Empty<FourberieItemSelection>(),
            Array.Empty<string>(), Array.Empty<FourberieRosterSelection>());

    private static NetworkRequestFourberieOperation Request(
        FourberieCampaignConsequence consequence,
        string target) => new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0,
            FourberieOperation.CommitCampaignConsequence,
            string.Empty, target, string.Empty, (int)consequence,
            Array.Empty<FourberieTroopSelection>(),
            Array.Empty<FourberieItemSelection>(),
            Array.Empty<string>(),
            Array.Empty<FourberieRosterSelection>());

    private static NetworkRequestFourberieOperation CriminalRequest(
        FourberieCriminalConsequence consequence,
        string target = "",
        string secondary = "",
        string[] objects = null) => new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"), 1, 0,
            FourberieOperation.CommitCriminalConsequence,
            "town.test", target, secondary, (int)consequence,
            Array.Empty<FourberieTroopSelection>(),
            Array.Empty<FourberieItemSelection>(),
            objects ?? Array.Empty<string>(),
            Array.Empty<FourberieRosterSelection>());
}
