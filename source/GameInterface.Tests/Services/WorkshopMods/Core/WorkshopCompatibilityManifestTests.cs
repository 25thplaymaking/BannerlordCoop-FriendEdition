using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public class WorkshopCompatibilityManifestTests
{
    [Fact]
    public void Protobuf_RoundTrip_PreservesAndValidatesManifest()
    {
        WorkshopCompatibilityManifest original = ManifestFactory.Create(WorkshopPeerRole.Client);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        WorkshopCompatibilityManifest roundTrip = Serializer.Deserialize<WorkshopCompatibilityManifest>(stream);

        Assert.True(roundTrip.TryValidateWireShape(out string error), error);
        Assert.Equal(original.ManifestSha256, roundTrip.ManifestSha256);
        Assert.Equal(11, roundTrip.Entries.Length);
        Assert.Equal(WorkshopPeerRole.Client, roundTrip.PeerRole);
        Assert.Equal(160, roundTrip.Entries.Single(entry => entry.ModuleId == "PlayerSettlement").LoadOrder);

        // Each entry's Active flag must survive the round trip exactly as the catalog policy
        // dictates it. The explicit Harmony check proves a true Active actually crossed the
        // wire; without it an all-false wire bug would pass vacuously.
        var catalog = new FriendEditionWorkshopModuleCatalog();
        Assert.All(roundTrip.Entries, entry =>
        {
            Assert.True(catalog.TryGet(entry.ModuleId, out WorkshopModuleExpectation expectation));
            Assert.Equal(expectation.FeatureActiveExpectedOnClient, entry.Active);
        });
        Assert.True(roundTrip.Entries.Single(entry => entry.ModuleId == "Bannerlord.Harmony").Active);
    }

    [Fact]
    public void Digest_IsIndependentOfEntryEnumerationOrder()
    {
        WorkshopCompatibilityManifest ordered = ManifestFactory.Create(WorkshopPeerRole.Server);
        var reversed = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Server,
            ordered.Entries.Reverse());

        Assert.Equal(ordered.ManifestSha256, reversed.ManifestSha256);
    }

    [Fact]
    public void WireShape_RejectsDuplicateAndOversizedEntrySets()
    {
        WorkshopCompatibilityManifest valid = ManifestFactory.Create(WorkshopPeerRole.Client);
        var duplicate = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Client,
            valid.Entries.Concat(new[] { valid.Entries[0] }));

        Assert.False(duplicate.TryValidateWireShape(out string duplicateError));
        Assert.Contains("duplicate", duplicateError, StringComparison.OrdinalIgnoreCase);

        var repeated = Enumerable.Range(0, WorkshopCompatibilityManifest.MaximumEntries + 1)
            .Select(index => ManifestFactory.Entry(
                "Synthetic" + index,
                index.ToString(),
                "v1.0.0",
                new string('a', 64),
                new string('b', 64)));
        var oversized = new WorkshopCompatibilityManifest(WorkshopPeerRole.Client, repeated);

        Assert.False(oversized.TryValidateWireShape(out string oversizedError));
        Assert.Contains("more than", oversizedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WireShape_RejectsOutOfRangeNumericLoadOrder()
    {
        WorkshopCompatibilityManifest invalid = ManifestFactory.Create(
            WorkshopPeerRole.Client,
            module => ManifestFactory.Entry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                ManifestFactory.StableHash(module.ModuleId, 'a'),
                ManifestFactory.StableHash(module.ModuleId, 'b'),
                module.Role,
                module.Profile,
                loadOrder: module.ModuleId == "RBM"
                    ? WorkshopCompatibilityManifest.MaximumLoadOrder + 1
                    : module.LoadOrder));

        Assert.False(invalid.TryValidateWireShape(out string error));
        Assert.Contains("load order", error, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ManifestFactory
{
    public static WorkshopCompatibilityManifest Create(
        WorkshopPeerRole role,
        Func<WorkshopModuleExpectation, WorkshopCompatibilityManifestEntry> entryFactory = null)
    {
        var catalog = new FriendEditionWorkshopModuleCatalog();
        return new WorkshopCompatibilityManifest(role, catalog.Modules.Select(module =>
            entryFactory?.Invoke(module) ?? Entry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                StableHash(module.ModuleId, 'a'),
                StableHash(module.ModuleId, 'b'),
                module.Role,
                module.Profile,
                loadOrder: module.LoadOrder,
                active: role == WorkshopPeerRole.Server
                    ? module.FeatureActiveExpectedOnServer
                    : module.FeatureActiveExpectedOnClient)));
    }

    public static WorkshopCompatibilityManifestEntry Entry(
        string moduleId,
        string workshopId,
        string version,
        string contentHash,
        string configurationHash,
        WorkshopModuleRole role = WorkshopModuleRole.Framework,
        WorkshopCompatibilityProfile profile = WorkshopCompatibilityProfile.AllPeersExact,
        bool managedDistributionComponent = true,
        int loadOrder = 0,
        bool? active = null) =>
        new(moduleId, workshopId, version, role, profile,
            contentHash, configurationHash, managedDistributionComponent,
            loadOrder: loadOrder,
            active: active ?? ExpectedActive(moduleId));

    private static bool ExpectedActive(string moduleId)
    {
        var catalog = new FriendEditionWorkshopModuleCatalog();
        return catalog.TryGet(moduleId, out WorkshopModuleExpectation expectation) &&
               expectation.FeatureActiveExpectedOnServer;
    }

    public static string StableHash(string identity, char fill)
    {
        int offset = Math.Abs(StringComparer.Ordinal.GetHashCode(identity)) % 16;
        char value = (char)(fill + offset % 5);
        return new string(value, 64);
    }
}
