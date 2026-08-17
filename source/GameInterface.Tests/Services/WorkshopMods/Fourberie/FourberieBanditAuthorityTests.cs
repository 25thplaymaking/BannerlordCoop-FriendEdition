using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieBanditAuthorityTests
{
    [Fact]
    public void ExactBanditPresentationSurface_ExcludesEverySelectionConsequence()
    {
        int[] local =
        {
            0x06000013, 0x06000014, 0x06000278, 0x06000291, 0x06000292,
            0x0600029A, 0x060002B8, 0x060002B9, 0x060002BA, 0x060002BD,
            0x06000738, 0x0600077C, 0x0600077E, 0x06000780,
        };
        Assert.All(local, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation)));

        int[] consequences =
        {
            0x06000260, 0x06000272, 0x06000280, 0x06000282, 0x06000285,
            0x060002C0, 0x06000745, 0x0600074C, 0x0600074E, 0x0600075E,
            0x06000766, 0x06000776, 0x06000782, 0x06000791, 0x06000795,
        };
        Assert.All(consequences, token => Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.MetadataToken == token && spec.Kind == FourberiePatchKind.ClientPresentation));
    }

    [Fact]
    public void EveryBanditEvent_HasOnlyItsBoundedAuthenticatedShape()
    {
        Assert.True(Valid(FourberieBanditEvent.RepairShips, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.HealWounds, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.ReleaseAllFollowers, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.RefuseBanditJoin, target: "party"));
        Assert.True(Valid(FourberieBanditEvent.FollowParties, objects: new[] { "party-a", "party-b" }));
        Assert.True(Valid(FourberieBanditEvent.StopFollower, target: "party"));
        Assert.True(Valid(FourberieBanditEvent.AcceptTruce, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.BreakTruce, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.BetrayBandits, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.SelectWarDogKingdom, settlement: true, target: "kingdom"));
        Assert.True(Valid(FourberieBanditEvent.AcquireCoveShip, settlement: true, target: "ship-hull"));
        Assert.True(Valid(FourberieBanditEvent.TransferFollowerShip, target: "party", secondary: "actor.0"));
        Assert.True(Valid(FourberieBanditEvent.DonatePrisoners, settlement: true,
            troops: new[] { new FourberieTroopSelection("bandit", 5) }));
        Assert.True(Valid(FourberieBanditEvent.CommitBanditRoster, target: "party",
            roster: new[] { new FourberieRosterSelection("bandit", 2, -1) }));
        Assert.True(Valid(FourberieBanditEvent.PrepareRecruitment, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.OpenBanditStash, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.RefreshBlackMarket, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.StartHideoutWait, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.StopHideoutWait, settlement: true));
        Assert.True(Valid(FourberieBanditEvent.DonateLoot, settlement: true,
            items: new[] { new FourberieItemSelection("loot", string.Empty, 1) }));

        Assert.False(Valid(FourberieBanditEvent.RepairShips));
        Assert.False(Valid(FourberieBanditEvent.FollowParties, objects: new[] { "duplicate", "duplicate" }));
        Assert.False(Valid(FourberieBanditEvent.TransferFollowerShip, target: "party", secondary: "actor.64"));
        Assert.False(Valid(FourberieBanditEvent.CommitBanditRoster, target: "party",
            roster: new[] { new FourberieRosterSelection("bandit", 0, 0) }));
    }

    [Fact]
    public void ExactBanditConsequences_MapToExecutableOwners()
    {
        AssertRoutes(FourberiePatchKind.ServerOnly, 0x06000260, 0x06000286, 0x060002AF, 0x060002B2);
        AssertRoutes(FourberiePatchKind.BanditConsequence,
            0x06000272, 0x060002C0, 0x06000745, 0x0600074C, 0x0600074E,
            0x0600075E, 0x06000766, 0x06000776, 0x06000782, 0x06000791, 0x06000795);
        AssertRoutes(FourberiePatchKind.BanditDonationConsequence, 0x06000280);
        AssertRoutes(FourberiePatchKind.BanditRosterOpen, 0x06000282);
        AssertRoutes(FourberiePatchKind.BanditRosterConsequence, 0x06000285);
        AssertRoutes(FourberiePatchKind.BanditPreparation,
            0x06000741, 0x06000744, 0x06000746, 0x06000748, 0x0600074A);
    }

    private static void AssertRoutes(FourberiePatchKind kind, params int[] tokens) =>
        Assert.All(tokens, token => Assert.Single(
            FourberieCompatibilityManifest.Methods.Where(spec =>
                spec.MetadataToken == token && spec.Kind == kind)));

    private static bool Valid(
        FourberieBanditEvent banditEvent,
        bool settlement = false,
        string target = "",
        string secondary = "",
        FourberieTroopSelection[] troops = null,
        FourberieItemSelection[] items = null,
        string[] objects = null,
        FourberieRosterSelection[] roster = null) =>
        FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            Guid.NewGuid().ToString("N"),
            1,
            0,
            FourberieOperation.CommitBanditEvent,
            settlement ? "settlement-current" : string.Empty,
            target,
            secondary,
            (int)banditEvent,
            troops ?? Array.Empty<FourberieTroopSelection>(),
            items ?? Array.Empty<FourberieItemSelection>(),
            objects ?? Array.Empty<string>(),
            roster ?? Array.Empty<FourberieRosterSelection>()));
}
