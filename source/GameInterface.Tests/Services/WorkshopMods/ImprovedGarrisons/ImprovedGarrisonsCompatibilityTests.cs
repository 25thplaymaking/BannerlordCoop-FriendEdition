using Common;
using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
using HarmonyLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using LiteNetLib;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.ImprovedGarrisons;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ImprovedGarrisonsCompatibilityTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly IImprovedGarrisonsPatchRuntime previousRuntime = ImprovedGarrisonsPatchRuntime.Current;

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
        ImprovedGarrisonsPatchRuntime.Current = previousRuntime;
        ImprovedGarrisonsAuthorityPatches.ResetTickLedger();
    }

    [Fact]
    public void SupportedIdentity_RequiresExactCreatorApprovedVersionAndBinary()
    {
        Assert.True(ImprovedGarrisonsCompatibilityManifest.IsSupportedIdentity(
            "v4.2.0.7",
            "fedab4041748951282634101871a9c41219bf3f2fa90d4e9cd6a4cbec082ce15"));
        Assert.False(ImprovedGarrisonsCompatibilityManifest.IsSupportedIdentity(
            "v4.2.0.8",
            ImprovedGarrisonsCompatibilityManifest.SupportedSha256));
        Assert.False(ImprovedGarrisonsCompatibilityManifest.IsSupportedIdentity(
            ImprovedGarrisonsCompatibilityManifest.SupportedModuleVersion,
            new string('0', 64)));
    }

    [Fact]
    public void AuditedContract_HasNoDuplicateMethodSignatures()
    {
        var duplicate = ImprovedGarrisonsCompatibilityManifest.Methods
            .GroupBy(method => method.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        Assert.Null(duplicate);
    }

    [Fact]
    public void UnknownImprovedGarrisonsHarmonyOwners_FailIdentification()
    {
        Assert.True(ImprovedGarrisonsHarmonyIsolation.LooksLikeUnknownModuleOwner("improved_garrisons.runtime"));
        Assert.True(ImprovedGarrisonsHarmonyIsolation.LooksLikeUnknownModuleOwner("ImprovedGarrison-Patches"));
        Assert.False(ImprovedGarrisonsHarmonyIsolation.LooksLikeUnknownModuleOwner("bannerlord.coop.friend-edition"));
        Assert.False(ImprovedGarrisonsHarmonyIsolation.LooksLikeUnknownModuleOwner(null!));
    }

    [Fact]
    public void PreinstalledModuleHarmonyPatch_IsRemovedAndPostPurgeAssertionIsEmpty()
    {
        var probeAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ImprovedGarrisonsIsolationProbe_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var type = probeAssembly.DefineDynamicModule("probe")
            .DefineType("ApprovedModulePatch", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var prefixBuilder = type.DefineMethod(
            "Prefix",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool),
            Type.EmptyTypes);
        var il = prefixBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        var prefix = type.CreateType()!.GetMethod("Prefix")!;
        var target = typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
            nameof(HarmonyIsolationTarget),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var owner = new Harmony("coop.ig.isolation.probe." + Guid.NewGuid().ToString("N"));

        try
        {
            owner.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert.Single(ImprovedGarrisonsHarmonyIsolation.DescribeModulePatches(probeAssembly));

            Assert.Equal(1, ImprovedGarrisonsHarmonyIsolation.RemoveModulePatches(probeAssembly, owner));
            Assert.Empty(ImprovedGarrisonsHarmonyIsolation.DescribeModulePatches(probeAssembly));
        }
        finally
        {
            owner.Unpatch(target, HarmonyPatchType.All, owner.Id);
        }
    }

    [Fact]
    public void AdapterGuardInventory_RequiresExactDedicatedOwnerAndRejectsForeignOverlap()
    {
        var probeAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ImprovedGarrisonsInventoryProbe_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var type = probeAssembly.DefineDynamicModule("probe")
            .DefineType("AdapterPatch", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var prefixBuilder = type.DefineMethod(
            "Prefix",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool),
            Type.EmptyTypes);
        var prefixIl = prefixBuilder.GetILGenerator();
        prefixIl.Emit(OpCodes.Ldc_I4_1);
        prefixIl.Emit(OpCodes.Ret);
        var postfixBuilder = type.DefineMethod(
            "Postfix",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes);
        postfixBuilder.GetILGenerator().Emit(OpCodes.Ret);
        var patchType = type.CreateType()!;
        var prefix = patchType.GetMethod("Prefix")!;
        var postfix = patchType.GetMethod("Postfix")!;
        var target = typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
            nameof(HarmonyIsolationTarget),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var ownerId = "coop.ig.inventory." + Guid.NewGuid().ToString("N");
        var owner = new Harmony(ownerId);
        var foreign = new Harmony("foreign.ig.inventory." + Guid.NewGuid().ToString("N"));
        var expected = new[] { (Original: target, Prefix: prefix, Postfix: postfix) };

        try
        {
            owner.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
            ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(expected, ownerId);

            foreign.Patch(target, postfix: new HarmonyMethod(
                typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
                    nameof(ForeignHarmonyPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic)!));
            Assert.Throws<InvalidOperationException>(() =>
                ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(expected, ownerId));
        }
        finally
        {
            owner.Unpatch(target, HarmonyPatchType.All, owner.Id);
            foreign.Unpatch(target, HarmonyPatchType.All, foreign.Id);
        }
    }

    [Fact]
    public void CachedAdapterInventory_ReassertsExactTargetsAndRejectsUnknownRuntimeOverlap()
    {
        var runtimeAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ImprovedGarrisonsRuntimeProbe_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var type = runtimeAssembly.DefineDynamicModule("probe")
            .DefineType("ImprovedGarrisons.RuntimeProbe", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var guardedBuilder = type.DefineMethod(
            "Guarded",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes);
        guardedBuilder.GetILGenerator().Emit(OpCodes.Ret);
        var unexpectedBuilder = type.DefineMethod(
            "Unexpected",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes);
        unexpectedBuilder.GetILGenerator().Emit(OpCodes.Ret);
        var runtimeType = type.CreateType()!;
        var guarded = runtimeType.GetMethod("Guarded")!;
        var unexpected = runtimeType.GetMethod("Unexpected")!;
        var prefix = typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
            nameof(AdapterHarmonyPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var postfix = typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
            nameof(ForeignHarmonyPostfix),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var ownerId = "coop.ig.cached.inventory." + Guid.NewGuid().ToString("N");
        var owner = new Harmony(ownerId);
        var foreign = new Harmony("foreign.ig.cached.inventory." + Guid.NewGuid().ToString("N"));
        var expected = new[] { (Original: guarded, Prefix: prefix, Postfix: (MethodInfo)null!) };

        try
        {
            owner.Patch(guarded, prefix: new HarmonyMethod(prefix));

            // The cached installation path performs both checks again instead of trusting the
            // process-local assembly marker from an earlier handler instance.
            ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(expected, ownerId);
            ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                runtimeAssembly,
                expected.Select(item => item.Original),
                ownerId);
            ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(expected, ownerId);
            ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                runtimeAssembly,
                expected.Select(item => item.Original),
                ownerId);

            foreign.Patch(unexpected, postfix: new HarmonyMethod(postfix));
            Assert.Throws<InvalidOperationException>(() =>
                ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                    runtimeAssembly,
                    expected.Select(item => item.Original),
                    ownerId));
        }
        finally
        {
            owner.Unpatch(guarded, HarmonyPatchType.All, owner.Id);
            foreign.Unpatch(unexpected, HarmonyPatchType.All, foreign.Id);
        }
    }

    [Fact]
    public void CanonicalHash_IsIndependentOfInputOrder()
    {
        var first = new[]
        {
            new ImprovedGarrisonsStateValue("town", "town_B", "EnableTraining", "true"),
            new ImprovedGarrisonsStateValue("config", "", "DailyEXPAmount", "20")
        };
        var second = first.Reverse().ToArray();

        Assert.Equal(
            ImprovedGarrisonsCanonicalState.ComputeHash(first),
            ImprovedGarrisonsCanonicalState.ComputeHash(second));
    }

    [Fact]
    public void CanonicalHash_LengthFramesValuesToPreventSeparatorCollisions()
    {
        var first = new[] { new ImprovedGarrisonsStateValue("a", "bc", "d", "e") };
        var second = new[] { new ImprovedGarrisonsStateValue("ab", "c", "d", "e") };

        Assert.NotEqual(
            ImprovedGarrisonsCanonicalState.ComputeHash(first),
            ImprovedGarrisonsCanonicalState.ComputeHash(second));
    }

    [Fact]
    public void CanonicalValues_AreLocaleInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
            Assert.Equal("0.35", ImprovedGarrisonsCanonicalState.FormatValue(0.35f));
            Assert.True(ImprovedGarrisonsCanonicalState.TryParseValue("0.35", typeof(float), out var value));
            Assert.Equal(0.35f, Assert.IsType<float>(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NativeAndFileMutations_RunOnlyOnServer(bool isServer, bool expected)
    {
        ModInformation.IsServer = isServer;
        Assert.Equal(expected, ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix());
    }

    [Fact]
    public void DestructiveLifecyclePaths_ArePinnedAndClientsCannotEnterThem()
    {
        var expected = new[]
        {
            "ImprovedGarrisons.Main::OnApplicationTick(System.Single)",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::RegisterEvents()",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnGameOpen(TaleWorlds.CampaignSystem.CampaignGameStarter)",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnGameStartDeleteAllIGParties()",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnGameStartSetAllIGParties()",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::ReturnAllIGParties()",
            "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::SetPartyOwner(TaleWorlds.CampaignSystem.Party.MobileParty)"
        };

        foreach (var key in expected)
        {
            var spec = Assert.Single(ImprovedGarrisonsCompatibilityManifest.Methods, method => method.Key == key);
            Assert.Equal(ImprovedGarrisonsPatchKind.ServerLifecycle, spec.Kind);
        }

        ModInformation.IsServer = false;
        Assert.False(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix());
        ModInformation.IsServer = true;
        Assert.True(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix());
    }

    [Theory]
    [InlineData("ImprovedGarrisons.Recruitment.GarrisonRecruitmentLogic::RecruitFromSurroundingVillages(TaleWorlds.CampaignSystem.Settlements.Settlement)", "ServerMutation")]
    [InlineData("ImprovedGarrisons.Upgrade.GarrisonUpgradeLogic::GiveGarrisonExp(TaleWorlds.CampaignSystem.Settlements.Settlement)", "ServerMutation")]
    [InlineData("ImprovedGarrisons.Behaviours.GarrisonPartyBehavior::OnSettlementOwnerChanged(TaleWorlds.CampaignSystem.Settlements.Settlement,System.Boolean,TaleWorlds.CampaignSystem.Hero,TaleWorlds.CampaignSystem.Hero,TaleWorlds.CampaignSystem.Hero,TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction+ChangeOwnerOfSettlementDetail)", "ServerMutation")]
    [InlineData("ImprovedGarrisons.SaveSystem.SaveBehavior::OnSaveEvent(System.Boolean,System.String)", "ServerPersistence")]
    [InlineData("ImprovedGarrisons.SaveSystem.SaveBehavior::OnLoadEvent(TaleWorlds.CampaignSystem.CampaignGameStarter)", "ServerPersistence")]
    public void RecruitUpgradeCaptureAndSaveLifecycle_IsPinnedToServerAuthority(
        string methodKey,
        string expectedKind)
    {
        var spec = Assert.Single(
            ImprovedGarrisonsCompatibilityManifest.Methods,
            method => method.Key == methodKey);

        Assert.Equal(expectedKind, spec.Kind.ToString());
        ModInformation.IsServer = false;
        Assert.False(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix());
        ModInformation.IsServer = true;
        Assert.True(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix());
    }

    [Fact]
    public void DuplicateServerTick_IsSuppressedButNextTickRuns()
    {
        var ledger = new ImprovedGarrisonsTickLedger();

        Assert.True(ledger.TryEnter("daily", 100));
        Assert.False(ledger.TryEnter("daily", 100));
        Assert.True(ledger.TryEnter("daily", 101));
        Assert.True(ledger.TryEnter("hourly", 100));
    }

    [Fact]
    public void ClientSetting_IsRoutedAndOriginalMutationIsSuppressed()
    {
        ModInformation.IsServer = false;
        var runtime = new RecordingRuntime(route: true);
        ImprovedGarrisonsPatchRuntime.Current = runtime;
        var method = typeof(ImprovedGarrisonsCompatibilityTests)
            .GetMethod(nameof(FakeSetting), BindingFlags.Static | BindingFlags.NonPublic)!;

        var runOriginal = ImprovedGarrisonsAuthorityPatches.RoutedSettingPrefix(
            new object(), method, new object[] { new object(), true });

        Assert.False(runOriginal);
        Assert.Equal(1, runtime.RouteCalls);
        Assert.Equal(0, runtime.DeniedCalls);
    }

    [Fact]
    public void UnsupportedClientSelection_IsExplicitlyDenied()
    {
        ModInformation.IsServer = false;
        var runtime = new RecordingRuntime(route: false);
        ImprovedGarrisonsPatchRuntime.Current = runtime;

        Assert.False(ImprovedGarrisonsAuthorityPatches.DeniedClientUiPrefix(
            typeof(ImprovedGarrisonsCompatibilityTests).GetMethod(
                nameof(FakeSetting), BindingFlags.Static | BindingFlags.NonPublic)!));
        Assert.Equal(1, runtime.DeniedCalls);
    }

    [Fact]
    public void StablePartyIdentity_UsesOriginalIdTokenToPreventSuffixCollisions()
    {
        var first = ImprovedGarrisonsAuthorityPatches.BuildStablePartyId(
            "improvedgarrison_recruiter_Porós_2", "town_ES3", "village_ES3_1");
        var repeat = ImprovedGarrisonsAuthorityPatches.BuildStablePartyId(
            "improvedgarrison_recruiter_Porós_2", "town_ES3", "village_ES3_1");
        var collisionCandidate = ImprovedGarrisonsAuthorityPatches.BuildStablePartyId(
            "improvedgarrison_recruiter_Poros_2", "town_ES3", "village_ES3_1");

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, collisionCandidate);
        Assert.StartsWith("improvedgarrison_recruiter_coop_h8_town_ES3_s13_village_ES3_1_o", first);
        Assert.EndsWith("_2", first);
        Assert.True(ImprovedGarrisonsAuthorityPatches.TryReadStablePartyIds(first, out var homeId, out var spawnId));
        Assert.Equal("town_ES3", homeId);
        Assert.Equal("village_ES3_1", spawnId);
    }

    [Theory]
    [InlineData("SetTownMaxUpgradeTier", 1, true)]
    [InlineData("SetTownMaxUpgradeTier", 11, false)]
    [InlineData("SetRecruiterAmountToRecruit", 150, true)]
    [InlineData("SetRecruiterAmountToRecruit", 0, false)]
    [InlineData("SetAutoGarrisonSize", 10001, false)]
    public void RoutedValues_AreBounded(string method, int value, bool expected)
    {
        Assert.Equal(expected, ImprovedGarrisonsCompatibilityHandler.IsValueAllowed(method, value));
    }

    [Fact]
    public void StateMessage_ProtobufRoundTripsCanonicalPayload()
    {
        var values = new[]
        {
            new ImprovedGarrisonsStateValue("config", "", "DailyEXPAmount", "20"),
            new ImprovedGarrisonsStateValue("town", "town_ES3", "EnableTraining", "true")
        };
        var original = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            42,
            ImprovedGarrisonsCanonicalState.ComputeHash(values),
            values);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkImprovedGarrisonsState>(stream);

        Assert.Equal(42, copy.Revision);
        Assert.Equal(original.CanonicalHash, copy.CanonicalHash);
        Assert.Equal(2, copy.Values.Length);
        Assert.Equal("town_ES3", copy.Values[1].TargetId);
    }

    [Theory]
    [InlineData(4, "aaaa", 3, "aaaa", false)]
    [InlineData(4, "aaaa", 4, "bbbb", false)]
    [InlineData(4, "aaaa", 4, "AAAA", true)]
    [InlineData(4, "aaaa", 5, "bbbb", true)]
    [InlineData(0, null, 0, "bbbb", true)]
    public void StateRevisionGate_RejectsStaleAndEqualRevisionConflicts(
        long currentRevision,
        string? currentHash,
        long incomingRevision,
        string incomingHash,
        bool expected)
    {
        Assert.Equal(expected, ImprovedGarrisonsCompatibilityHandler.CanApplyState(
            currentRevision, currentHash!, incomingRevision, incomingHash));
    }

    [Fact]
    public void SnapshotOrigin_AcceptsOnlyClientServerTransportAndRejectsLocalPublishers()
    {
#pragma warning disable SYSLIB0050 // NetPeer has no public test constructor; no member is invoked.
        var transportPeer = (NetPeer)FormatterServices.GetUninitializedObject(typeof(NetPeer));
#pragma warning restore SYSLIB0050

        Assert.True(ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
            transportPeer,
            localIsClient: true));
        Assert.False(ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
            transportPeer,
            localIsClient: false));
        Assert.False(ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
            new object(),
            localIsClient: true));
        Assert.False(ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
            null!,
            localIsClient: true));
    }

    [Fact]
    public void StateAdmission_RejectsCorruptStaleConflictingAndSemanticFailuresWithoutRevisionCommit()
    {
        var initialValues = new[]
        {
            new ImprovedGarrisonsStateValue("config", "", "DailyEXPAmount", "20")
        };
        var initialHash = ImprovedGarrisonsCanonicalState.ComputeHash(initialValues);
        var currentRevision = 4L;
        var currentHash = new string('a', 64);

        var corrupt = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            5,
            new string('0', 64),
            initialValues);
        AssertRejectedWithoutCommit(corrupt, currentRevision, currentHash, AcceptCanonicalState, "corrupt");

        var stale = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            3,
            initialHash,
            initialValues);
        AssertRejectedWithoutCommit(stale, currentRevision, currentHash, AcceptCanonicalState, "stale");

        var conflicting = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            currentRevision,
            initialHash,
            initialValues);
        AssertRejectedWithoutCommit(conflicting, currentRevision, currentHash, AcceptCanonicalState, "conflicting");

        var semanticFailure = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            5,
            initialHash,
            initialValues);
        AssertRejectedWithoutCommit(
            semanticFailure,
            currentRevision,
            currentHash,
            RejectCanonicalState,
            "semantic rejection");
    }

    [Fact]
    public void StateAdmission_CommitsOnlyAfterCanonicalTransactionSucceeds()
    {
        var values = new[]
        {
            new ImprovedGarrisonsStateValue("config", "", "DailyEXPAmount", "20")
        };
        var hash = ImprovedGarrisonsCanonicalState.ComputeHash(values);
        var state = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            5,
            hash,
            values);

        Assert.True(ImprovedGarrisonsCompatibilityHandler.TryAcceptState(
            4,
            new string('a', 64),
            state,
            AcceptCanonicalState,
            out var acceptedRevision,
            out var acceptedHash,
            out var rejection));
        Assert.Equal(5, acceptedRevision);
        Assert.Equal(hash, acceptedHash);
        Assert.Null(rejection);
    }

    [Fact]
    public void CanonicalParsing_RejectsAmbiguousFlagsAndNonFiniteNumbers()
    {
        Assert.False(ImprovedGarrisonsCanonicalState.TryParseValue("1,true", typeof(bool[]), out _));
        Assert.False(ImprovedGarrisonsCanonicalState.TryParseValue("NaN", typeof(float), out _));
        Assert.False(ImprovedGarrisonsCanonicalState.TryParseValue("Infinity", typeof(double), out _));
        Assert.True(ImprovedGarrisonsCanonicalState.TryParseValue("1,0,1", typeof(bool[]), out var flags));
        Assert.Equal(new[] { true, false, true }, Assert.IsType<bool[]>(flags));
    }

    [Fact]
    public void MissingInitialAuthoritativeState_AbortsSession()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ImprovedGarrisonsCompatibilityHandler.DenyPeerOrAbortSession(
                null!,
                "capture failed"));

        Assert.Contains("cannot continue", exception.Message);
        Assert.Contains("capture failed", exception.Message);
    }

    [Fact]
    public void RequestLedger_SuppressesReplaysAndBoundsMemoryPerPeer()
    {
        var ledger = new ImprovedGarrisonsRequestLedger<string>(2);
        ledger.Record("peer-a", 10);
        ledger.Record("peer-a", 11);
        ledger.Record("peer-a", 12);

        Assert.False(ledger.HasSeen("peer-a", 10));
        Assert.True(ledger.HasSeen("peer-a", 11));
        Assert.True(ledger.HasSeen("peer-a", 12));
        Assert.False(ledger.HasSeen("peer-b", 12));
    }

    [Fact]
    public void SettingRequest_RequiresIdsRevisionAndBoundedFields()
    {
        var valid = new NetworkRequestImprovedGarrisonsSettingChange(
            9, 3, "ImprovedGarrisons.Manager", "ToggleTraining", "town_ES3", "true");
        var missingId = new NetworkRequestImprovedGarrisonsSettingChange(
            0, 3, "ImprovedGarrisons.Manager", "ToggleTraining", "town_ES3", "true");
        var oversized = new NetworkRequestImprovedGarrisonsSettingChange(
            9, 3, new string('m', ImprovedGarrisonsCompatibilityHandler.MaxRequestManagerLength + 1),
            "ToggleTraining", "town_ES3", "true");

        Assert.True(ImprovedGarrisonsCompatibilityHandler.IsRequestShapeValid(valid));
        Assert.False(ImprovedGarrisonsCompatibilityHandler.IsRequestShapeValid(missingId));
        Assert.False(ImprovedGarrisonsCompatibilityHandler.IsRequestShapeValid(oversized));
    }

    [Fact]
    public void SettingRequest_ProtobufPreservesConcurrencyEnvelope()
    {
        var original = new NetworkRequestImprovedGarrisonsSettingChange(
            91, 7, "ImprovedGarrisons.Manager", "ToggleTraining", "town_ES3", "true");

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkRequestImprovedGarrisonsSettingChange>(stream);

        Assert.Equal(91, copy.RequestId);
        Assert.Equal(7, copy.ExpectedRevision);
        Assert.Equal("ToggleTraining", copy.Method);
        Assert.Equal("town_ES3", copy.TownId);
    }

    [Fact]
    public void SnapshotShape_BoundsValueCountAndEveryString()
    {
        var values = new[] { new ImprovedGarrisonsStateValue("town", "town_ES3", "EnableTraining", "true") };
        var valid = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            1,
            ImprovedGarrisonsCanonicalState.ComputeHash(values),
            values);
        var oversized = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            1,
            ImprovedGarrisonsCanonicalState.ComputeHash(values),
            new[]
            {
                new ImprovedGarrisonsStateValue(
                    "town", "town_ES3", "EnableTraining",
                    new string('x', ImprovedGarrisonsCompatibilityHandler.MaxSnapshotValueLength + 1))
            });

        Assert.True(ImprovedGarrisonsCompatibilityHandler.IsSnapshotShapeValid(valid, out _));
        Assert.False(ImprovedGarrisonsCompatibilityHandler.IsSnapshotShapeValid(oversized, out _));
    }

    [Fact]
    public void SnapshotShape_RejectsDuplicateCanonicalProperties()
    {
        var duplicateValues = new[]
        {
            new ImprovedGarrisonsStateValue("town", "town_ES3", "EnableTraining", "true"),
            new ImprovedGarrisonsStateValue("town", "town_ES3", "EnableTraining", "false")
        };
        var state = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            2,
            ImprovedGarrisonsCanonicalState.ComputeHash(duplicateValues),
            duplicateValues);

        Assert.False(ImprovedGarrisonsCompatibilityHandler.IsSnapshotShapeValid(state, out var failure));
        Assert.Contains("duplicate canonical property", failure);
    }

    [Fact]
    public void ClientConfigLoad_IsReplacedWithInMemoryDefaultsAndNoOriginalFileCall()
    {
        var manager = new FakeConfigManager();
        ModInformation.IsServer = false;

        Assert.False(ImprovedGarrisonsAuthorityPatches.ClientDefaultConfigPrefix(manager));
        Assert.NotNull(manager.Config);
        Assert.True(manager.Config.WasReset);

        ModInformation.IsServer = true;
        Assert.True(ImprovedGarrisonsAuthorityPatches.ClientDefaultConfigPrefix(manager));
    }

    [Fact]
    public void CompletedServerTick_PublishesThroughRuntime()
    {
        ModInformation.IsServer = true;
        var runtime = new RecordingRuntime(route: false);
        ImprovedGarrisonsPatchRuntime.Current = runtime;

        ImprovedGarrisonsAuthorityPatches.ServerTickPostfix();

        Assert.Equal(1, runtime.TickCompletedCalls);
    }

    private static void FakeSetting(object town, bool enabled)
    {
    }

    private static void HarmonyIsolationTarget()
    {
    }

    private static bool AdapterHarmonyPrefix() => true;

    private static void ForeignHarmonyPostfix()
    {
    }

    private static bool AcceptCanonicalState(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure)
    {
        failure = null!;
        return values != null;
    }

    private static bool RejectCanonicalState(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure)
    {
        failure = "semantic rejection";
        return false;
    }

    private static void AssertRejectedWithoutCommit(
        NetworkImprovedGarrisonsState state,
        long currentRevision,
        string currentHash,
        ImprovedGarrisonsCompatibilityHandler.CanonicalStateApplier apply,
        string expectedFailure)
    {
        Assert.False(ImprovedGarrisonsCompatibilityHandler.TryAcceptState(
            currentRevision,
            currentHash,
            state,
            apply,
            out var acceptedRevision,
            out var acceptedHash,
            out var rejection));
        Assert.Equal(currentRevision, acceptedRevision);
        Assert.Equal(currentHash, acceptedHash);
        Assert.Contains(expectedFailure, rejection);
    }

    private sealed class RecordingRuntime : IImprovedGarrisonsPatchRuntime
    {
        private readonly bool route;
        public int RouteCalls { get; private set; }
        public int DeniedCalls { get; private set; }
        public int TickCompletedCalls { get; private set; }

        public RecordingRuntime(bool route) => this.route = route;

        public bool TryRouteSetting(object manager, MethodBase method, object[] arguments)
        {
            RouteCalls++;
            return route;
        }

        public void NotifyDenied(string method) => DeniedCalls++;

        public void OnAuthoritativeTickCompleted() => TickCompletedCalls++;

        public bool TryGetTownSettings(TaleWorlds.CampaignSystem.Settlements.Town town, out object settings)
        {
            settings = null!;
            return false;
        }

        public void ReconcileSettlements()
        {
        }

        public void OnSettlementOwnerChanged(TaleWorlds.CampaignSystem.Settlements.Settlement settlement)
        {
        }
    }

    private sealed class FakeConfigManager
    {
        public FakeConfig Config { get; set; } = null!;
    }

    private sealed class FakeConfig
    {
        public bool WasReset { get; private set; }
        public void ResetToDefault() => WasReset = true;
    }
}
