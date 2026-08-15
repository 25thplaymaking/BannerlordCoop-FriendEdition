using Common;
using GameInterface.Services.WorkshopMods.PlayerSettlement;
using HarmonyLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.PlayerSettlement;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PlayerSettlementCompatibilityTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly IPlayerSettlementPatchRuntime previousRuntime = PlayerSettlementPatchRuntime.Current;

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
        PlayerSettlementPatchRuntime.Current = previousRuntime;
    }

    [Fact]
    public void SupportedIdentity_RequiresExactCreatorApprovedVersionAndBinary()
    {
        Assert.True(PlayerSettlementCompatibilityManifest.IsSupportedIdentity(
            "v7.5.0",
            "74F9AB2EBC82BDC755886C6AD0802500C2DF89015D7C65CD5018C543DBF18119"));
        Assert.True(PlayerSettlementCompatibilityManifest.IsSupportedIdentity(
            "v7.5.0",
            "7862692EFCAC86A08D24F09670D74EE9BD72C549A9849652A7C08D491C7A7314"));
        Assert.False(PlayerSettlementCompatibilityManifest.IsSupportedIdentity(
            "v7.5.1",
            PlayerSettlementCompatibilityManifest.SupportedAssemblySha256.First()));
        Assert.False(PlayerSettlementCompatibilityManifest.IsSupportedIdentity(
            PlayerSettlementCompatibilityManifest.ModuleVersion,
            new string('0', 64)));
        Assert.Contains(
            "a74dd0ed13470240074dfe2a52295a33cb6235a1b6a0ae739fcd0b0d8dd8d1ac",
            PlayerSettlementCompatibilityManifest.SupportedFixesSha256,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditedContract_HasNoDuplicateShapes_AndCoversDangerousEntryPoints()
    {
        var contract = PlayerSettlementCompatibilityManifest.Methods
            .Concat(PlayerSettlementCompatibilityManifest.FixesMethods)
            .ToArray();
        Assert.Null(contract
            .GroupBy(spec => spec.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1));

        var names = contract
            .Select(spec => spec.MethodName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("OnSubModuleLoad", names);
        Assert.Contains("EnsureSiegeFixApplied", names);
        Assert.Contains("RegisterEvents", names);
        Assert.Contains("OnBeforeTick", names);
        Assert.Contains("SetupGameMenus", names);
        Assert.Contains("StartPortPlacement", names);
        Assert.Contains("StartGatePlacement", names);
        Assert.Contains("ExecuteCreatePlayerSettlement", names);
        Assert.Contains("Reset", names);
        Assert.Contains("RefreshVisualSelection", names);
        Assert.Contains("Overwrite", names);
        Assert.Contains("Rebuild", names);
        Assert.Contains("BuildTown", names);
        Assert.Contains("BuildCastle", names);
        Assert.Contains("BuildVillage", names);
        Assert.Contains("SaveLoad", names);
        Assert.Contains("UpdateBlacklist", names);
    }

    [Fact]
    public void MethodShapeGate_RequiresExactStaticReturnAndParameters()
    {
        var correct = new PlayerSettlementMethodSpec(
            typeof(ShapeProbe).FullName!,
            nameof(ShapeProbe.Expected),
            isStatic: true,
            "System.Boolean",
            PlayerSettlementPatchKind.BlockedSharedMutation,
            "System.Int32",
            "System.String");

        Assert.True(PlayerSettlementCompatibilityManifest.TryResolveMethods(
            typeof(PlayerSettlementCompatibilityTests).Assembly,
            new[] { correct },
            out var methods,
            out var failure), failure);
        Assert.Single(methods);

        var wrong = new PlayerSettlementMethodSpec(
            typeof(ShapeProbe).FullName!,
            nameof(ShapeProbe.Expected),
            isStatic: true,
            "System.Boolean",
            PlayerSettlementPatchKind.BlockedSharedMutation,
            "System.Int64",
            "System.String");
        Assert.False(PlayerSettlementCompatibilityManifest.TryResolveMethods(
            typeof(PlayerSettlementCompatibilityTests).Assembly,
            new[] { wrong },
            out _,
            out failure));
        Assert.Contains("resolved 0 times", failure);
    }

    [Fact]
    public void HarmonyIsolation_IdentifiesOnlyExactOptionalAssemblyPatchMethods()
    {
        var patchMethod = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.BlockedPatch),
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.True(PlayerSettlementHarmonyIsolation.IsModulePatchMethod(
            patchMethod,
            typeof(PlayerSettlementCompatibilityTests).Assembly,
            typeof(string).Assembly));
        Assert.False(PlayerSettlementHarmonyIsolation.IsModulePatchMethod(
            patchMethod,
            typeof(string).Assembly,
            typeof(Uri).Assembly));
        Assert.True(PlayerSettlementHarmonyIsolation.IsModulePatchOwner(
            PlayerSettlementHarmonyIsolation.MainHarmonyOwner));
        Assert.True(PlayerSettlementHarmonyIsolation.IsModulePatchOwner(
            PlayerSettlementHarmonyIsolation.FixesHarmonyOwner));
        Assert.False(PlayerSettlementHarmonyIsolation.IsModulePatchOwner("coop.test"));
    }

    [Theory]
    [InlineData(PlayerSettlementHarmonyIsolation.MainHarmonyOwner)]
    [InlineData(PlayerSettlementHarmonyIsolation.FixesHarmonyOwner)]
    public void HarmonyIsolation_RemovesOptionalOwnerPatchAndLeavesNoInventory(string optionalOwner)
    {
        var optionalHarmony = new Harmony(optionalOwner);
        var cleanupHarmony = new Harmony("coop.tests.playersettlement.isolation");
        var target = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.MutationTarget),
            BindingFlags.Static | BindingFlags.NonPublic);
        var prefix = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.BlockedPatch),
            BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            optionalHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert.NotEmpty(PlayerSettlementHarmonyIsolation.DescribeModulePatches(null, null));

            Assert.True(PlayerSettlementHarmonyIsolation.RemoveModulePatches(
                mainAssembly: null,
                fixesAssembly: null,
                unpatcher: cleanupHarmony) >= 1);
            Assert.Empty(PlayerSettlementHarmonyIsolation.DescribeModulePatches(null, null));
        }
        finally
        {
            optionalHarmony.UnpatchAll(optionalOwner);
            cleanupHarmony.UnpatchAll(cleanupHarmony.Id);
        }
    }

    [Fact]
    public void HarmonyIsolation_RequiresExactDedicatedGuardInventoryAndRejectsOverlap()
    {
        var adapterHarmony = new Harmony(PlayerSettlementHarmonyIsolation.AdapterHarmonyOwner);
        var overlapHarmony = new Harmony("coop.tests.playersettlement.overlap");
        var target = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.MutationTarget),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var prefix = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.BlockedPatch),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var postfix = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.AfterPatch),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            PlayerSettlementHarmonyIsolation.AssertNoPatchOverlap(new[] { target });
            adapterHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
            PlayerSettlementHarmonyIsolation.AssertExactAdapterPatchInventory(
                new[] { (target, prefix) });

            overlapHarmony.Patch(target, postfix: new HarmonyMethod(postfix));
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PlayerSettlementHarmonyIsolation.AssertExactAdapterPatchInventory(
                    new[] { (target, prefix) }));
            Assert.Contains("inventory mismatch", exception.Message);
        }
        finally
        {
            adapterHarmony.UnpatchAll(adapterHarmony.Id);
            overlapHarmony.UnpatchAll(overlapHarmony.Id);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void LifecycleAndPersistence_RunOnlyOnServer(bool isServer, bool expected)
    {
        ModInformation.IsServer = isServer;
        Assert.Equal(expected, PlayerSettlementAuthorityPatches.ServerPersistencePrefix());
        Assert.Equal(expected, PlayerSettlementAuthorityPatches.ServerLifecyclePrefix());
        Assert.Equal(!isServer, PlayerSettlementAuthorityPatches.ClientPresentationPrefix());
        Assert.True(PlayerSettlementAuthorityPatches.RoleLifecyclePrefix());
    }

    [Fact]
    public void StoreResolver_UsesThePlayerSettlementExtensionMethod()
    {
        var method = PlayerSettlementStoreResolver.ResolveGetStore(
            typeof(PlayerSettlementCompatibilityTests).Assembly,
            typeof(StoreExtensionProbe).FullName!,
            typeof(StoreCampaignProbe),
            typeof(StoreBehaviorProbe),
            typeof(object));

        Assert.Equal(typeof(StoreExtensionProbe), method.DeclaringType);
        Assert.True(method.IsStatic);
        Assert.Equal(nameof(StoreExtensionProbe.GetStore), method.Name);
    }

    [Fact]
    public void UnavailableEarlyStore_AdmitsOnlyProvablyEmptyCampaignState()
    {
        PlayerSettlementUnavailableStoreAdmission.RequireEmpty(
            metadataCaptureSucceeded: true,
            Array.Empty<PlayerSettlementStateEntry>(),
            metadataCaptureFailure: null,
            legacyHasGeneratedObjects: false,
            legacyConfigDirectoryExists: false);

        var generated = new[]
        {
            Entry(PlayerSettlementObjectKind.Town, 0, "player_settlement_town_a", string.Empty),
        };
        Assert.Throws<InvalidOperationException>(() =>
            PlayerSettlementUnavailableStoreAdmission.RequireEmpty(
                metadataCaptureSucceeded: true,
                generated,
                metadataCaptureFailure: null,
                legacyHasGeneratedObjects: false,
                legacyConfigDirectoryExists: false));
        Assert.Throws<InvalidOperationException>(() =>
            PlayerSettlementUnavailableStoreAdmission.RequireEmpty(
                metadataCaptureSucceeded: true,
                Array.Empty<PlayerSettlementStateEntry>(),
                metadataCaptureFailure: null,
                legacyHasGeneratedObjects: true,
                legacyConfigDirectoryExists: false));
        Assert.Throws<InvalidOperationException>(() =>
            PlayerSettlementUnavailableStoreAdmission.RequireEmpty(
                metadataCaptureSucceeded: true,
                Array.Empty<PlayerSettlementStateEntry>(),
                metadataCaptureFailure: null,
                legacyHasGeneratedObjects: false,
                legacyConfigDirectoryExists: true));
    }

    [Theory]
    [InlineData(true, 1, 1, true)]
    [InlineData(false, 1, 0, false)]
    public void LifecyclePrefixes_LoadValidatedXmlOnlyOnTheHost(
        bool isServer,
        int expectedBootstrapCalls,
        int expectedValidationCalls,
        bool expectedOriginalRegistration)
    {
        ModInformation.IsServer = isServer;
        var runtime = new RecordingRuntime();
        PlayerSettlementPatchRuntime.Current = runtime;

        Assert.False(PlayerSettlementAuthorityPatches.BootstrapPersistenceBehaviorPrefix(new object()));
        Assert.Equal(
            expectedOriginalRegistration,
            PlayerSettlementAuthorityPatches.GuardedObjectRegistrationPrefix(__0: true));
        Assert.Equal(expectedBootstrapCalls, runtime.BootstrapCalls);
        Assert.Equal(expectedValidationCalls, runtime.ValidationCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructionEntryPoint_IsDeniedOnEveryRole(bool isServer)
    {
        ModInformation.IsServer = isServer;
        var runtime = new RecordingRuntime();
        PlayerSettlementPatchRuntime.Current = runtime;
        var method = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.Expected),
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.False(PlayerSettlementAuthorityPatches.BlockedPrefix(method));
        Assert.Equal(1, runtime.Notifications);
    }

    [Fact]
    public void SnapshotOrderingAndFingerprint_AreIndependentOfInputOrder()
    {
        var town = Entry(PlayerSettlementObjectKind.Town, 0, "player_settlement_town_a", string.Empty);
        var village = Entry(PlayerSettlementObjectKind.BoundVillage, 0,
            "player_settlement_village_a", town.StringId);

        Assert.Equal(
            PlayerSettlementStateCodec.ComputeHash(new[] { town, village }),
            PlayerSettlementStateCodec.ComputeHash(new[] { village, town }));
        Assert.Equal(
            new[] { town.StringId, village.StringId },
            PlayerSettlementStateCodec.Sort(new[] { village, town }).Select(entry => entry.StringId));
    }

    [Fact]
    public void SnapshotValidation_RejectsDuplicateStableIdsAndTamperedXml()
    {
        var first = Entry(PlayerSettlementObjectKind.Town, 0, "player_settlement_town_a", string.Empty);
        var duplicate = Entry(PlayerSettlementObjectKind.Castle, 0, first.StringId, string.Empty);
        var duplicated = Snapshot(2, first, duplicate);

        Assert.False(PlayerSettlementStateCodec.TryValidate(duplicated, out var failure));
        Assert.Contains("duplicate stable object ID", failure);

        first.Xml += " ";
        var tampered = Snapshot(3, first);
        // Keep the original per-entry XML hash and recompute only the envelope hash.
        tampered.StateFingerprint = PlayerSettlementStateCodec.ComputeHash(tampered.Entries);
        Assert.False(PlayerSettlementStateCodec.TryValidate(tampered, out failure));
        Assert.Contains("XML fingerprint or stable object ID mismatch", failure);
    }

    [Fact]
    public void StateAdmission_ThrowsBeforePublicationForCaptureFailureOrGeneratedObjects()
    {
        var state = new[]
        {
            Entry(PlayerSettlementObjectKind.Town, 0, "player_settlement_town_a", string.Empty),
        };
        var originalHash = PlayerSettlementStateCodec.ComputeHash(state);

        var captureException = Assert.Throws<InvalidOperationException>(() =>
            PlayerSettlementStateAdmission.RequireEmpty(
                captureSucceeded: false,
                Array.Empty<PlayerSettlementStateEntry>(),
                "metadata unreadable",
                "test"));
        Assert.Contains("could not be captured", captureException.Message);

        var stateException = Assert.Throws<InvalidOperationException>(() =>
            PlayerSettlementStateAdmission.RequireEmpty(
                captureSucceeded: true,
                state,
                null,
                "test"));
        Assert.Contains("contains 1 generated objects", stateException.Message);
        Assert.Equal(originalHash, PlayerSettlementStateCodec.ComputeHash(state));
    }

    [Fact]
    public void SnapshotOrigin_RejectsLocalAndNullBrokerPublishers()
    {
        Assert.False(PlayerSettlementSnapshotOriginGuard.IsTrustedServerTransport(
            null,
            localIsClient: true));
        Assert.False(PlayerSettlementSnapshotOriginGuard.IsTrustedServerTransport(
            this,
            localIsClient: true));
        Assert.False(PlayerSettlementSnapshotOriginGuard.IsTrustedServerTransport(
            this,
            localIsClient: false));
    }

    [Fact]
    public void NonEmptyLateJoin_RequiresEveryGeneratedSettlementInTheReplicatedRegistry()
    {
        var town = Entry(PlayerSettlementObjectKind.Town, 0, "player_settlement_town_a", string.Empty);
        var village = Entry(
            PlayerSettlementObjectKind.BoundVillage,
            0,
            "player_settlement_village_a",
            town.StringId);
        var registered = new HashSet<string>(StringComparer.Ordinal) { town.StringId, village.StringId };

        Assert.True(PlayerSettlementObjectGraphRegistry.TryVerify(
            new[] { town, village }, registered.Contains, out var failure), failure);
        registered.Remove(village.StringId);
        Assert.False(PlayerSettlementObjectGraphRegistry.TryVerify(
            new[] { town, village }, registered.Contains, out failure));
        Assert.Contains(village.StringId, failure);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void SnapshotFailurePolicy_DisconnectsOnlyRejectedTrustedTransport(
        bool trustedTransport,
        bool accepted,
        bool expectedDisconnect)
    {
        Assert.Equal(expectedDisconnect,
            PlayerSettlementSnapshotFailurePolicy.MustDisconnect(trustedTransport, accepted));
    }

    [Fact]
    public void RevisionGate_RejectsStaleAndConflictingEqualRevision()
    {
        var gate = new PlayerSettlementRevisionGate();
        var first = PlayerSettlementStateCodec.ComputeHash(Array.Empty<PlayerSettlementStateEntry>());
        var second = new string('a', 64);

        Assert.True(gate.Commit(5, first));
        Assert.Equal(PlayerSettlementRevisionDecision.AlreadyApplied, gate.Evaluate(5, first));
        Assert.Equal(PlayerSettlementRevisionDecision.ConflictingRevision, gate.Evaluate(5, second));
        Assert.Equal(PlayerSettlementRevisionDecision.Stale, gate.Evaluate(4, first));
        Assert.Equal(PlayerSettlementRevisionDecision.Apply, gate.Evaluate(6, second));
    }

    [Fact]
    public void EmptyLateJoinSnapshot_ProtobufRoundTripsAndValidates()
    {
        var original = Snapshot(9);
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkPlayerSettlementState>(stream);

        Assert.Equal(9, copy.Revision);
        Assert.Equal(PlayerSettlementFeatureStatus.Enabled, copy.FeatureStatus);
        // protobuf-net omits an empty repeated field, so an empty snapshot legitimately
        // deserializes with null Entries — the exact zeroed-receiver shape TryValidate
        // normalizes with its `Entries ?? Array.Empty` before validating.
        Assert.True(copy.Entries == null || copy.Entries.Length == 0);
        Assert.True(PlayerSettlementStateCodec.TryValidate(copy, out var failure), failure);
    }

    private static NetworkPlayerSettlementState Snapshot(
        long revision,
        params PlayerSettlementStateEntry[] entries) =>
        new NetworkPlayerSettlementState(
            PlayerSettlementCompatibilityManifest.AdapterVersion,
            revision,
            PlayerSettlementFeatureStatus.Enabled,
            PlayerSettlementStateCodec.ComputeHash(entries),
            entries);

    private static PlayerSettlementStateEntry Entry(
        PlayerSettlementObjectKind kind,
        int ordinal,
        string id,
        string parent)
    {
        var xml = $"<Settlements><Settlement id=\"{id}\"><Town id=\"{id}_component\" /></Settlement></Settlements>";
        Assert.True(PlayerSettlementStateCodec.TryFingerprintXml(
            xml, out var xmlHash, out var componentHash, out var failure), failure);
        return new PlayerSettlementStateEntry(
            kind, ordinal, id, parent, "prefab", "7.5.0.0", "Test", 42f,
            xml, xmlHash, componentHash);
    }

    private sealed class RecordingRuntime : IPlayerSettlementPatchRuntime
    {
        public int Notifications { get; private set; }
        public int BootstrapCalls { get; private set; }
        public int ValidationCalls { get; private set; }
        public void NotifyFeatureBlocked(string method) => Notifications++;
        public void AddBehavior(object campaignGameStarter) => BootstrapCalls++;
        public void ValidateObjectRegistration(bool isSavedCampaign) => ValidationCalls++;
    }

    private sealed class ShapeProbe
    {
        internal static bool Expected(int value, string text) => value > 0 && text != null;
        internal static bool MutationTarget() => true;
        internal static bool BlockedPatch() => false;
        internal static void AfterPatch()
        {
        }
    }

    private sealed class StoreCampaignProbe { }
    private sealed class StoreBehaviorProbe { }
    private static class StoreExtensionProbe
    {
        public static object GetStore(StoreCampaignProbe campaign, StoreBehaviorProbe behavior) =>
            new object();
    }
}
