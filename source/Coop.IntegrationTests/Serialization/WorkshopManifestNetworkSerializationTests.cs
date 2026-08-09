using Coop.Core.Server.Connections.Messages;
using GameInterface.Configuration;
using GameInterface.Services.Modules;
using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using TaleWorlds.Library;
using Xunit;

namespace Coop.IntegrationTests.Serialization;

public class WorkshopManifestNetworkSerializationTests
{
    [Fact]
    public void ValidationRequest_RoundTripsCompleteBoundedManifest()
    {
        var catalog = new FriendEditionWorkshopModuleCatalog();
        var manifest = new WorkshopCompatibilityManifest(
            WorkshopPeerRole.Client,
            catalog.Modules.Select(module => new WorkshopCompatibilityManifestEntry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                module.Role,
                module.Profile,
                new string('a', 64),
                new string('b', 64),
                managedDistributionComponent: true,
                loadOrder: module.LoadOrder,
                active: module.FeatureActiveExpectedOnClient)));
        ModuleInfo[] modules =
        {
            new("Native", true, false, ApplicationVersion.FromString("v1.4.3")),
            new("Coop", false, false, ApplicationVersion.FromString("v0.1.0")),
        };
        var request = new NetworkModuleVersionsValidate(modules, manifest);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        NetworkModuleVersionsValidate roundTrip =
            Serializer.Deserialize<NetworkModuleVersionsValidate>(stream);

        Assert.True(roundTrip.TryValidateWireShape(out string error), error);
        Assert.NotNull(roundTrip.WorkshopManifest);
        Assert.True(roundTrip.WorkshopManifest.TryValidateWireShape(out error), error);
        Assert.Equal(manifest.ManifestSha256, roundTrip.WorkshopManifest.ManifestSha256);
    }

    [Fact]
    public void ValidationRequest_RejectsExcessiveOutboundModuleCount()
    {
        var modules = Enumerable.Range(0, NetworkModuleVersionsValidate.MaximumModules + 1)
            .Select(index => new ModuleInfo(
                "Module" + index,
                false,
                false,
                ApplicationVersion.FromString("v1.0.0")));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetworkModuleVersionsValidate(modules, null));
    }

    [Fact]
    public void ValidationRequest_RejectsDuplicateIdsAndMissingOfficialVersion()
    {
        var duplicate = new NetworkModuleVersionsValidate(new[]
        {
            new ModuleInfo("Native", true, false, ApplicationVersion.FromString("v1.4.3")),
            new ModuleInfo("native", true, false, ApplicationVersion.FromString("v1.4.3")),
        });
        Assert.False(duplicate.TryValidateWireShape(out string duplicateError));
        Assert.Contains("duplicate", duplicateError, StringComparison.OrdinalIgnoreCase);

        var noOfficial = new NetworkModuleVersionsValidate(new[]
        {
            new ModuleInfo("Coop", false, false, ApplicationVersion.FromString("v0.1.0")),
        });
        Assert.False(noOfficial.TryValidateWireShape(out string officialError));
        Assert.Contains("official", officialError, StringComparison.OrdinalIgnoreCase);

        var defaultOfficialVersion = new NetworkModuleVersionsValidate(new[]
        {
            new ModuleInfo("Native", true, false, new ApplicationVersion()),
        });
        Assert.False(defaultOfficialVersion.TryValidateWireShape(out string defaultVersionError));
        Assert.Contains("valid game version", defaultVersionError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationResponse_RoundTripsMandatoryHostConfigBarrier()
    {
        var options = new ModOptions(new ModOptionsData
        {
            WandererLimit = 48,
            Separatism = new SeparatismOptionsData { DailyLordRebellionChance = 0.25f },
        });
        var config = new ModConfigSnapshot(
            "0123456789abcdef0123456789abcdef",
            7,
            options,
            birthAndDeathEnabled: true);
        var response = new NetworkModuleVersionsValidated(true, null, null, config);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, response);
        stream.Position = 0;
        NetworkModuleVersionsValidated roundTrip =
            Serializer.Deserialize<NetworkModuleVersionsValidated>(stream);

        Assert.True(roundTrip.Matches);
        Assert.True(ModConfigSnapshotCodec.TryValidate(roundTrip.HostModConfig, out var failure), failure);
        Assert.Equal(7, roundTrip.HostModConfig.Revision);
        Assert.Equal(48, roundTrip.HostModConfig.ModOptions.WandererLimit);
        Assert.True(new NetworkClientValidate("controller", roundTrip.HostModConfig)
            .Acknowledges(config));
        Assert.False(new NetworkClientValidate("controller").Acknowledges(config));
    }
}
