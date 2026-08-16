using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.WorkshopMods.RebellionsAndDemographics;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.RebellionsAndDemographics;

public sealed class RebellionsAndDemographicsProtocolTests
{
    private static readonly AuthorityRequestHeader Header = new(7, "session-a", 41, 9);

    [Fact]
    public void ChoiceCommand_IsAnAuthenticatedTypedRoute_AndRejectsMalformedLease()
    {
        var valid = new NetworkRequestRebellionsAndDemographicsChoice(Header, "lease-a", RdPromptKind.Ultimatum, true, 12);
        var malformed = new NetworkRequestRebellionsAndDemographicsChoice(Header, string.Empty, (RdPromptKind)99, true, 12);

        Assert.True(valid.IsValid);
        Assert.Equal(Header.RequestId, valid.Header.RequestId);
        Assert.Equal(Header.SessionId, valid.Header.SessionId);
        Assert.False(malformed.IsValid);
        Assert.Contains(typeof(NetworkRequestRebellionsAndDemographicsChoice).GetCustomAttributes(false),
            attribute => attribute is AuthorityRouteAttribute route && route.RouteId == RebellionsAndDemographicsCompatibilityHandler.ChoiceRouteId);
    }

    [Fact]
    public void PinnedWorkshopBinary_UsesItsActualManagedIdentity_WhenLocalAuditPayloadIsPresent()
    {
        const string path = @"P:\SteamLibrary\steamapps\workshop\content\261550\3644127631\bin\Win64_Shipping_Client\RebellionsAndDemographics.dll";
        if (!File.Exists(path)) return; // CI uses a staged private suite rather than this developer audit source.

        var identity = AssemblyName.GetAssemblyName(path);
        Assert.Equal("ClassLibrary22", identity.Name);
        Assert.Equal(RebellionsAndDemographicsModule.AssemblyName, identity.Name);
    }

    [Fact]
    public void CanonicalState_UsesStructuredBoundedCultures_AndCorrelatedPromptTombstone()
    {
        var lease = new RdPromptLease("lease-a", RdPromptKind.Defeat, "session-a", "hero-owner", "rebels",
            new[] { "clan-a", "clan-b" }, 0, 12, 15);
        var tombstone = new RdPromptTombstone(lease, Header.RequestId, accepted: false, completed: true);
        var settlement = new RdSettlementPopulationState("town_A", 4200, 1200, 0, 24,
            new[] { new RdCulturePopulationState("empire", 4000), new RdCulturePopulationState("vlandia", 200) });
        var state = new RebellionsAndDemographicsState("session-a", 13, new[] { "PopulationBehavior" },
            new[] { settlement }, new RdPlagueState("town_A", 3, "day-14"), Array.Empty<RdPromptLease>(), new[] { tombstone }, Array.Empty<RdInterventionWatermark>());

        Assert.Equal(64, state.Fingerprint.Length);
        Assert.Equal(new[] { "empire", "vlandia" }, state.Settlements.Single().Cultures.Select(value => value.CultureId));
        Assert.Single(state.PromptTombstones);
        Assert.Equal(Header.RequestId, state.PromptTombstones[0].AuthorityRequestId);
        Assert.True(state.PromptTombstones[0].Completed);
    }

    [Fact]
    public void InterventionWatermark_CorrelatesActorTargetAndExactPostconditions()
    {
        var watermark = new RdInterventionWatermark(Header.RequestId, "hero-owner", "clan-owner", "hero-target",
            "clan-target", "kingdom-old", "kingdom-new", 123, 456.5f, 3);

        Assert.Equal(Header.RequestId, watermark.AuthorityRequestId);
        Assert.Equal("kingdom-new", watermark.NewKingdomId);
        Assert.Equal(123, watermark.PostActorGold);
        Assert.Equal(3, watermark.AllyCount);
    }

    [Fact]
    public void CanonicalState_RetainsConcurrentInterventionWatermarks()
    {
        var first = new RdInterventionWatermark(41, "hero-a", "clan-a", "hero-target-a", "clan-target-a", "old-a", "new-a", 1, 2, 3);
        var second = new RdInterventionWatermark(42, "hero-b", "clan-b", "hero-target-b", "clan-target-b", "old-b", "new-b", 4, 5, 4);
        var state = new RebellionsAndDemographicsState("session-a", 13, Array.Empty<string>(), Array.Empty<RdSettlementPopulationState>(),
            new RdPlagueState(string.Empty, 0, string.Empty), Array.Empty<RdPromptLease>(), Array.Empty<RdPromptTombstone>(), new[] { second, first });

        Assert.Equal(new long[] { 41, 42 }, state.InterventionWatermarks.Select(value => value.AuthorityRequestId));
    }
}
