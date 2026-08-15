using System;
using System.Linq;
using GameInterface.Services.WorkshopMods.Fourberie;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

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
}
