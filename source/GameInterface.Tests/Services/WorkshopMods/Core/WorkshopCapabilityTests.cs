using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopCapabilityTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Registry_AppliesCurrentSnapshotAndRejectsStaleOrConflictingRevision()
    {
        var registry = new WorkshopCapabilityRegistry();
        var snapshot = new WorkshopCapabilitySnapshot(
            Session,
            revision: 2,
            new[] { new WorkshopCapability("Fourberie", "RecruitBandits", true, string.Empty) });

        Assert.Equal(WorkshopCapabilityApplyResult.Applied, registry.Apply(snapshot));
        Assert.True(registry.IsEnabled("Fourberie", "RecruitBandits"));
        Assert.False(registry.IsEnabled("Fourberie", "Unknown"));
        Assert.Equal(WorkshopCapabilityApplyResult.AlreadyCurrent, registry.Apply(snapshot));
        Assert.Equal(
            WorkshopCapabilityApplyResult.Stale,
            registry.Apply(new WorkshopCapabilitySnapshot(Session, 1, Array.Empty<WorkshopCapability>())));
        Assert.Equal(
            WorkshopCapabilityApplyResult.Conflict,
            registry.Apply(new WorkshopCapabilitySnapshot(Session, 2, Array.Empty<WorkshopCapability>())));
        Assert.True(registry.IsEnabled("Fourberie", "RecruitBandits"));
    }

    [Fact]
    public void Registry_RejectsMalformedDuplicateOrExcessiveCapabilitiesBeforeCommit()
    {
        var registry = new WorkshopCapabilityRegistry();
        var malformed = new WorkshopCapabilitySnapshot(
            Session,
            0,
            new[] { new WorkshopCapability("Fourberie\nforged", "RecruitBandits", true, string.Empty) });
        var duplicate = new WorkshopCapabilitySnapshot(
            Session,
            0,
            new[]
            {
                new WorkshopCapability("Fourberie", "RecruitBandits", true, string.Empty),
                new WorkshopCapability("Fourberie", "RecruitBandits", false, "disabled"),
            });
        var excessive = new WorkshopCapabilitySnapshot(
            Session,
            0,
            Enumerable.Range(0, WorkshopCapabilitySnapshot.MaximumCapabilities + 1)
                .Select(index => new WorkshopCapability("Module", "Operation" + index, true, string.Empty))
                .ToArray());

        Assert.Equal(WorkshopCapabilityApplyResult.Malformed, registry.Apply(malformed));
        Assert.Equal(WorkshopCapabilityApplyResult.Malformed, registry.Apply(duplicate));
        Assert.Equal(WorkshopCapabilityApplyResult.Malformed, registry.Apply(excessive));
        Assert.False(registry.IsEnabled("Fourberie", "RecruitBandits"));
    }

    [Fact]
    public void SnapshotDigest_IsCanonicalAcrossCapabilityOrder()
    {
        var first = new WorkshopCapabilitySnapshot(
            Session,
            4,
            new[]
            {
                new WorkshopCapability("ImprovedGarrisons", "ManageGarrison", true, string.Empty),
                new WorkshopCapability("Fourberie", "RecruitBandits", false, "not routed"),
            });
        var second = new WorkshopCapabilitySnapshot(
            Session,
            4,
            first.Capabilities.Reverse().ToArray());

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(
            first.Capabilities.Select(value => value.ModuleId + "/" + value.Operation),
            second.Capabilities.Select(value => value.ModuleId + "/" + value.Operation));
    }

    [Fact]
    public void Reset_RemovesAcceptedCapabilitiesAndRevisionIdentity()
    {
        var registry = new WorkshopCapabilityRegistry();
        Assert.Equal(
            WorkshopCapabilityApplyResult.Applied,
            registry.Apply(new WorkshopCapabilitySnapshot(
                Session,
                0,
                new[] { new WorkshopCapability("Separatism", "RebelConversation", true, string.Empty) })));

        registry.Reset();

        Assert.False(registry.IsEnabled("Separatism", "RebelConversation"));
        Assert.Equal(
            WorkshopCapabilityApplyResult.Applied,
            registry.Apply(new WorkshopCapabilitySnapshot(
                "fedcba9876543210fedcba9876543210",
                0,
                Array.Empty<WorkshopCapability>())));
    }
}
