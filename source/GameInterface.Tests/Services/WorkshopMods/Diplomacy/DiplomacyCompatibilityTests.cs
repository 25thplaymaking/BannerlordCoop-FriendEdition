using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.WorkshopMods.Diplomacy;
using HarmonyLib;
using LiteNetLib;
using Moq;
using ProtoBuf;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Diplomacy;

[Collection(ModInformationRoleCollection.Name)]
public sealed class DiplomacyCompatibilityTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly ModOptions wasOptions = ModConfigProvider.ModOptions;

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;
        ModConfigProvider.ModOptions = wasOptions;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SharedCampaignMutation_IsServerOnly(bool isServer, bool expected)
    {
        ModInformation.IsServer = isServer;
        Assert.Equal(expected, DiplomacyCompatibilityPolicy.ShouldRunSharedMutation());
    }

    [Fact]
    public void OriginalMessengerQueue_IsRetiredOnBothPeers()
    {
        ModInformation.IsServer = true;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunOriginalMessengerBehavior());
        ModInformation.IsServer = false;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunOriginalMessengerBehavior());
    }

    [Fact]
    public void DiplomacyCivilWar_IsBlockedOnBothPeers_WhenFriendSeparatismIsEnabled()
    {
        ModConfigProvider.LoadModConfig(new ModOptionsData
        {
            Separatism = new SeparatismOptionsData { Enabled = true },
        });

        ModInformation.IsServer = true;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint());
        ModInformation.IsServer = false;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint());
    }

    [Fact]
    public void DiplomacyCivilWar_RemainsBlocked_WhenFriendSeparatismIsDisabled()
    {
        ModConfigProvider.LoadModConfig(new ModOptionsData
        {
            Separatism = new SeparatismOptionsData { Enabled = false },
        });

        ModInformation.IsServer = false;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint());
        ModInformation.IsServer = true;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint());
    }

    [Theory]
    [InlineData("Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior", "ConsiderDiplomaticAgreements")]
    [InlineData("Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior", "ReduceCorruption")]
    [InlineData("Diplomacy.CampaignBehaviors.WarExhaustionBehavior", "OnDailyTick")]
    public void AutomatedDiplomacyMutations_AreServerCallbacks(string typeName, string methodName)
    {
        ModInformation.IsServer = true;
        Assert.True(DiplomacyCompatibilityPolicy.ShouldRunSharedMutation(typeName, methodName));
        ModInformation.IsServer = false;
        Assert.False(DiplomacyCompatibilityPolicy.ShouldRunSharedMutation(typeName, methodName));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, true, false)]
    public void KingdomActions_RequireAnExplicitOrDerivedServerActorContext(
        bool isServer,
        bool explicitOperation,
        bool automatedOperation,
        bool hasProposingLeader,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyPlayerKingdomActionGuardPatch.ShouldAllowKingdomAction(
            isServer, explicitOperation, automatedOperation, hasProposingLeader));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void PeaceResolution_BypassesLocalInquiryOnlyInsideAuthoritativeScope(
        bool isServer,
        bool explicitOperation,
        bool automatedOperation,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyPeaceInquiryAuthorityPatch.ShouldAcceptWithoutInquiry(
            isServer, explicitOperation, automatedOperation));
    }

    [Fact]
    public void MutableLocalMcm_CannotOwnKingdomDecisionFormula()
    {
        Assert.False(DiplomacyDecisionPermissionModelGuardPatch.ShouldUseDiplomacyDecisionFormula());
        Assert.True(DiplomacyCompatibilityPolicy.IsRequiredMethodShape(
            "Diplomacy.Models.DiplomacyKingdomDecisionPermissionModel",
            "IsWarDecisionAllowedBetweenKingdoms",
            3));
        Assert.True(DiplomacyCompatibilityPolicy.IsRequiredMethodShape(
            "Diplomacy.Models.DiplomacyKingdomDecisionPermissionModel",
            "IsPeaceDecisionAllowedBetweenKingdoms",
            3));
    }

    [Theory]
    [InlineData("Diplomacy.CampaignBehaviors.WarExhaustionBehavior", "OnMapEventEnded")]
    [InlineData("Diplomacy.CampaignBehaviors.WarExhaustionBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior", "ConsiderDiplomaticAgreements")]
    [InlineData("Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.ExpansionismBehavior", "OnDailyTickClan")]
    [InlineData("Diplomacy.CampaignBehaviors.ExpansionismBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.CooldownBehavior", "SyncData")]
    [InlineData("Diplomacy.CampaignBehaviors.CooldownBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior", "RegisterEvents")]
    public void AuditedSharedMutationCallbacks_AreInAuthorityCatalog(string typeName, string methodName)
    {
        Assert.True(DiplomacyCompatibilityPolicy.IsSharedMutation(typeName, methodName));
    }

    [Theory]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "RegisterEvents")]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "SyncData")]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "DailyTickClan")]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "ResolveCivilWar")]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "OnGameLoadFinished")]
    [InlineData("Diplomacy.CivilWar.Actions.StartRebellionAction", "Apply")]
    [InlineData("Diplomacy.CivilWar.Factions.RebelFaction", "AddClan")]
    [InlineData("Diplomacy.ViewModel.RebelFactionItemVM", "OnStartRebellion")]
    public void EveryAuditedRebellionEntryPoint_IsInCollisionCatalog(string typeName, string methodName)
    {
        Assert.True(DiplomacyCompatibilityPolicy.IsCivilWarEntryPoint(typeName, methodName));
    }

    [Fact]
    public void DiplomacyUiLifecycle_SeparatesSnapshotGatedAndPermanentlyRetiredExtensions()
    {
        Assert.Equal(new[]
        {
            "Diplomacy.ViewModelMixin.DiplomacyPanelPrefabExtension",
            "Diplomacy.ViewModelMixin.KingdomDiplomacyVMMixin",
            "Diplomacy.ViewModelMixin.KingdomWarItemVMMixin",
            "Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin",
            "Diplomacy.ViewModelMixin.KingdomClanVMMixin",
            "Diplomacy.ViewModelMixin.EncyclopediaHeroPagePrefabExtension",
            "Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin",
            "Diplomacy.ViewModelMixin.PartyNameplateVMMixin",
            "Diplomacy.ViewModelMixin.PlayerPartyNameplateVMMixin",
            "Diplomacy.ViewModelMixin.SettlementNameplatesVMMixin",
        }, DiplomacyClientUiLifecycle.SnapshotGatedUiTypeNames);
        Assert.Equal(new[]
        {
            "Diplomacy.ViewModelMixin.KingdomManagementPrefabExtension",
            "Diplomacy.ViewModelMixin.KingdomManagementScalingPatch",
            "Diplomacy.ViewModelMixin.KingdomManagementVMMixin",
            "Diplomacy.ViewModelMixin.FactionsButtonExtension",
            "Diplomacy.ViewModelMixin.EncyclopediaFactionPagePrefabExtension",
            "Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin",
        }, DiplomacyClientUiLifecycle.PermanentlyRetiredUiTypeNames);
    }

    [Fact]
    public void DiplomacyUiLifecycle_FailsClosedUntilEveryGatedExtensionEnables()
    {
        ModInformation.IsServer = false;
        var wholeTransitions = new List<bool>();
        int enabledTypes = 0;
        var lifecycle = new DiplomacyClientUiLifecycle(
            _ => typeof(DiplomacyCompatibilityTests),
            (_, enabled) =>
            {
                if (enabled && ++enabledTypes == 3)
                    throw new InvalidOperationException("test enable failure");
            },
            enabled => wholeTransitions.Add(enabled));

        lifecycle.ResetForCampaign();
        Assert.False(lifecycle.IsReady);
        Assert.Equal(new[] { false }, wholeTransitions);

        Assert.False(lifecycle.TryMarkSnapshotReady(out var failure));
        Assert.Contains("test enable failure", failure);
        Assert.False(lifecycle.IsReady);
        Assert.Equal(new[] { false, false }, wholeTransitions);
    }

    [Fact]
    public void DiplomacyUiLifecycle_ResolvesAuditedTypesOnceAndReusesTheirExactIdentity()
    {
        ModInformation.IsServer = false;
        var resolved = new Dictionary<string, Type>();
        var resolutionCounts = new Dictionary<string, int>();
        var transitions = new List<(Type Type, bool Enabled)>();
        var knownTypes = DiplomacyClientUiLifecycle.SnapshotGatedUiTypeNames
            .Concat(DiplomacyClientUiLifecycle.PermanentlyRetiredUiTypeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string typeName in knownTypes)
            resolved[typeName] = typeof(DiplomacyCompatibilityTests);

        var lifecycle = new DiplomacyClientUiLifecycle(
            typeName =>
            {
                resolutionCounts[typeName] = resolutionCounts.TryGetValue(typeName, out int count) ? count + 1 : 1;
                return resolved[typeName];
            },
            (type, enabled) => transitions.Add((type, enabled)),
            _ => { });

        lifecycle.ResetForCampaign();
        Assert.True(lifecycle.TryMarkSnapshotReady(out var failure), failure);

        Assert.All(knownTypes, typeName => Assert.Equal(1, resolutionCounts[typeName]));
        Assert.All(transitions, transition => Assert.Same(typeof(DiplomacyCompatibilityTests), transition.Type));
    }

    [Fact]
    public void DiplomacyUiLifecycle_DisablesWholeExtensionBeforeAnyTypeResolutionFailure()
    {
        ModInformation.IsServer = false;
        var operations = new List<string>();
        var lifecycle = new DiplomacyClientUiLifecycle(
            typeName =>
            {
                operations.Add("resolve:" + typeName);
                throw new TypeLoadException(typeName);
            },
            (_, _) => operations.Add("type"),
            enabled => operations.Add(enabled ? "whole-enable" : "whole-disable"));

        Assert.Throws<AggregateException>(() => lifecycle.ResetForCampaign());

        Assert.Equal("whole-disable", operations[0]);
        Assert.DoesNotContain("type", operations);
        Assert.False(lifecycle.IsReady);
    }

    [Fact]
    public void DiplomacySettingsBridge_PreservesProviderAndCachesOneFallbackPerCampaignOnEveryRole()
    {
        DiplomacyClientSettingsBridge.Reset();
        var providerValue = new object();
        var firstFallback = new object();
        int factoryCalls = 0;

        Assert.Same(providerValue, DiplomacyClientSettingsBridge.Resolve(
            providerValue, () => throw new InvalidOperationException()));
        Assert.Same(firstFallback, DiplomacyClientSettingsBridge.Resolve(
            providerValue: null, () => { factoryCalls++; return firstFallback; }));
        Assert.Same(firstFallback, DiplomacyClientSettingsBridge.Resolve(
            providerValue: null, () => { factoryCalls++; return new object(); }));
        Assert.Equal(1, factoryCalls);
        Assert.Same(firstFallback, DiplomacyClientSettingsBridge.Resolve(
            providerValue: null, () => { factoryCalls++; return new object(); }));
        Assert.Equal(1, factoryCalls);

        DiplomacyClientSettingsBridge.Reset();
        var secondFallback = new object();
        Assert.Same(secondFallback, DiplomacyClientSettingsBridge.Resolve(
            providerValue: null, () => secondFallback));
    }

    [Theory]
    [InlineData("Diplomacy.Patches.KingdomDecisionProposalBehaviorPatch")]
    [InlineData("Diplomacy.Patches.MakePeaceKingdomDecisionPatch")]
    [InlineData("Diplomacy.Patches.RebelKingdomPatches")]
    [InlineData("Diplomacy.Patches.MBBannerEditorGauntletScreenPatch")]
    [InlineData("Diplomacy.Patches.DefaultEncyclopediaFactionPagePatch")]
    [InlineData("Diplomacy.Patches.KingdomManagementVMPatch")]
    public void OverlappingDiplomacyHarmonyPatch_IsClassifiedForRemoval(string patchType)
    {
        Assert.True(DiplomacyPatchCompatibilityGate.IsConflictingPatchType(patchType));
    }

    [Fact]
    public void ServerConflictAllowlist_DoesNotClassifyInfluenceFormulaPatch()
    {
        Assert.False(DiplomacyPatchCompatibilityGate.IsConflictingPatchType(
            "Diplomacy.Patches.DefaultClanPoliticsModelPatch"));
        Assert.True(DiplomacyPatchCompatibilityGate.IsServerAllowedPatchType(
            "Diplomacy.Patches.DefaultClanPoliticsModelPatch"));
    }

    [Fact]
    public void MissingOptionalDiplomacyType_FailsClosedWithoutThrowing()
    {
        Assert.Null(DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.Does.Not.Exist"));
    }

    [Theory]
    [InlineData("Bannerlord.Diplomacy.1.4.7", "1.4.7.0", "90930a1dfb48c8cf040b8bd2c89156a69838a8dc86b8ed97e0cd8475f2081257", true)]
    [InlineData("Bannerlord.Diplomacy.1.4.8", "1.4.7.0", "90930a1dfb48c8cf040b8bd2c89156a69838a8dc86b8ed97e0cd8475f2081257", false)]
    [InlineData("Bannerlord.Diplomacy.1.4.7", "1.4.7.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void ImplementationGate_RequiresExactNameVersionAndDllFingerprint(
        string name,
        string version,
        string hash,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyCompatibilityPolicy.IsExpectedAssemblyIdentity(name, version, hash));
    }

    [Theory]
    [InlineData("Bannerlord.Diplomacy.1.4.8", false, true)]
    [InlineData("Bannerlord.Diplomacy", false, false)]
    [InlineData("Bannerlord.Diplomacy", true, true)]
    [InlineData("Renamed.Workshop.Mod", true, true)]
    [InlineData("Unrelated.Assembly", false, false)]
    public void CandidateDiscovery_DoesNotTreatRenamedOrUpdatedDiplomacyAsAbsent(
        string assemblyName,
        bool hasDiplomacySubModule,
        bool expected)
    {
        Assert.Equal(
            expected,
            DiplomacyCompatibilityPolicy.IsPotentialDiplomacyAssembly(
                assemblyName,
                hasDiplomacySubModule));
    }

    [Fact]
    public void SupportedClient_RemovesEveryDiplomacyPatchOwner_AndLocalFormulaPatch()
    {
        ModInformation.IsServer = false;
        Assert.True(DiplomacyPatchCompatibilityGate.ShouldRemoveEveryDiplomacyPatch(
            implementationSupported: true));

        var original = typeof(DiplomacyCompatibilityTests).GetMethod(
            nameof(DummyInfluenceFormula),
            BindingFlags.Static | BindingFlags.NonPublic);
        var prefix = typeof(DiplomacyCompatibilityTests).GetMethod(
            nameof(DummyInfluencePrefix),
            BindingFlags.Static | BindingFlags.NonPublic);
        var harmony = new Harmony("bannerlord.diplomacy.test.client-formula");
        try
        {
            dummyLocalMcmInfluence = 999;
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            Assert.Equal(999, DummyInfluenceFormula(7));

            DiplomacyPatchCompatibilityGate.RemoveAndAssert(removeEveryDiplomacyPatch: true);

            // Simulate a local MCM edit after the authoritative client cleanup. With every
            // original Diplomacy owner removed, that local value can no longer affect gameplay.
            dummyLocalMcmInfluence = 321;
            Assert.False(DiplomacyPatchCompatibilityGate.HasForbiddenPatches(
                removeEveryDiplomacyPatch: true));
            Assert.Equal(7, DummyInfluenceFormula(7));
        }
        finally
        {
            // Never use Harmony's null-owner sweep here: it removes the process-wide GameBootStrap
            // engine shims and makes unrelated serialization/restorer tests depend on execution order.
            harmony.UnpatchAll(harmony.Id);
            dummyLocalMcmInfluence = 999;
        }
    }

    [Theory]
    [InlineData("Diplomacy.SubModule", "OnGameStart", 2)]
    [InlineData("Diplomacy.CampaignBehaviors.WarExhaustionBehavior", "OnMapEventEnded", 1)]
    [InlineData("Diplomacy.CampaignBehaviors.CivilWarBehavior", "RegisterEvents", 0)]
    [InlineData("Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin", "ProposeNonAggressionPact", 0)]
    [InlineData("Diplomacy.DiplomaticAction.NonAggressionPact.FormNonAggressionPactAction", "ApplyInternal", 3)]
    [InlineData("Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction", "ApplyPeace", 6)]
    public void ImplementationGate_RequiresAuditedMethodShapes(string type, string method, int arity)
    {
        Assert.True(DiplomacyCompatibilityPolicy.IsRequiredMethodShape(type, method, arity));
        Assert.False(DiplomacyCompatibilityPolicy.IsRequiredMethodShape(type, method, arity + 1));
    }

    [Fact]
    public void CaravanWarExhaustion_RejectsEndedEventWithRemovedParty()
    {
        var eventGraph = new FakeMapEvent
        {
            AttackerSide = CompleteSide(),
            DefenderSide = new FakeMapEventSide
            {
                LeaderParty = CompleteParty(),
                Parties = new[] { new FakeMapEventParty { Party = null } },
            },
        };

        Assert.False(DiplomacyWarExhaustionSafety.IsSafeMapEvent(eventGraph));
    }

    [Fact]
    public void CaravanWarExhaustion_AllowsCompleteEventGraph()
    {
        var eventGraph = new FakeMapEvent
        {
            AttackerSide = CompleteSide(),
            DefenderSide = CompleteSide(),
        };

        Assert.True(DiplomacyWarExhaustionSafety.IsSafeMapEvent(eventGraph));
    }

    [Fact]
    public void CaravanWarExhaustion_RejectsDetachedMobileParty()
    {
        var detached = new FakeParty { IsMobile = true, MobileParty = null };
        var eventGraph = new FakeMapEvent
        {
            AttackerSide = CompleteSide(),
            DefenderSide = new FakeMapEventSide
            {
                LeaderParty = CompleteParty(),
                Parties = new[] { new FakeMapEventParty { Party = detached } },
            },
        };

        Assert.False(DiplomacyWarExhaustionSafety.IsSafeMapEvent(eventGraph));
    }

    [Fact]
    public void SettingsAndStateFingerprints_AreIndependentOfInputOrder()
    {
        var settings = new[]
        {
            new DiplomacySettingEntry { Name = "B", TypeName = "System.Int32", Value = "2" },
            new DiplomacySettingEntry { Name = "A", TypeName = "System.Boolean", Value = "true" },
        };
        var state = new[]
        {
            new DiplomacyStateEntry { Section = "z", Key = "2", Value1 = 2f },
            new DiplomacyStateEntry { Section = "a", Key = "1", Value1 = 1f },
        };

        Assert.Equal(
            DiplomacyRuntime.FingerprintSettings(settings),
            DiplomacyRuntime.FingerprintSettings(new[] { settings[1], settings[0] }));
        Assert.Equal(
            DiplomacyRuntime.FingerprintState(state),
            DiplomacyRuntime.FingerprintState(new[] { state[1], state[0] }));
    }

    [Fact]
    public void CompatibilitySnapshot_ProtobufRoundTripsHostConfigurationAndState()
    {
        var original = new NetworkDiplomacySnapshot
        {
            AssemblyVersion = "1.4.7.0",
            AssemblySha256 = DiplomacyCompatibilityPolicy.SupportedAssemblySha256,
            CampaignId = "campaign-protobuf",
            Revision = 42,
            SettingsFingerprint = "settings",
            StateFingerprint = "state",
            FriendSeparatismOwnsRebellions = true,
            Settings = new List<DiplomacySettingEntry>
            {
                new() { Name = "EnableWarExhaustion", TypeName = "System.Boolean", Value = "true" },
            },
            State = new List<DiplomacyStateEntry>
            {
                new() { Section = "war-exhaustion.score", Key = "a+b", Value1 = 12.5f, Value2 = 7.5f },
            },
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkDiplomacySnapshot>(stream);

        Assert.Equal("1.4.7.0", copy.AssemblyVersion);
        Assert.Equal(42, copy.Revision);
        Assert.Equal(DiplomacyCompatibilityPolicy.SupportedAssemblySha256, copy.AssemblySha256);
        Assert.Equal("campaign-protobuf", copy.CampaignId);
        Assert.True(copy.FriendSeparatismOwnsRebellions);
        Assert.Equal("EnableWarExhaustion", Assert.Single(copy.Settings).Name);
        Assert.Equal(12.5f, Assert.Single(copy.State).Value1);
    }

    [Fact]
    public void SnapshotRequest_ProtobufRoundTripsAcceptedConfigIdentity()
    {
        var acceptedConfig = CurrentConfigSnapshot();
        var original = new NetworkRequestDiplomacySnapshot(acceptedConfig);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        var copy = Serializer.Deserialize<NetworkRequestDiplomacySnapshot>(stream);

        Assert.True(copy.TryValidateWireShape(out var failure), failure);
        Assert.True(copy.Matches(acceptedConfig));
    }

    [Theory]
    [InlineData((int)DiplomacySnapshotApplyStatus.VersionMismatch)]
    [InlineData((int)DiplomacySnapshotApplyStatus.ConfigurationMismatch)]
    [InlineData((int)DiplomacySnapshotApplyStatus.SettingsMismatch)]
    [InlineData((int)DiplomacySnapshotApplyStatus.StateMismatch)]
    public void SnapshotMismatch_IsRetainedForDiagnostics(int statusValue)
    {
        var status = (DiplomacySnapshotApplyStatus)statusValue;
        var runtime = new StubRuntime(new DiplomacySnapshotApplyResult(status, "mismatch"));
        var handler = CreateHandler(runtime);

        handler.ApplySnapshot(ValidEmptySnapshot(revision: 0));

        Assert.Equal(status, handler.LastApplyResult.Status);
        Assert.Equal("mismatch", handler.LastApplyResult.Detail);
        Assert.False(handler.LastApplyResult.Succeeded);
    }

    [Fact]
    public void SnapshotRevisionGate_RejectsStaleAndConflictingEqualRevision()
    {
        var gate = new DiplomacyRevisionGate();
        var accepted = ValidEmptySnapshot(revision: 7);

        Assert.Equal(DiplomacyRevisionDecision.Apply, gate.Evaluate(accepted));
        Assert.True(gate.Commit(accepted));
        Assert.Equal(DiplomacyRevisionDecision.AlreadyCurrent, gate.Evaluate(ValidEmptySnapshot(revision: 7)));
        Assert.Equal(DiplomacyRevisionDecision.Stale, gate.Evaluate(ValidEmptySnapshot(revision: 6)));

        var conflict = ValidEmptySnapshot(revision: 7);
        conflict.FriendSeparatismOwnsRebellions = !accepted.FriendSeparatismOwnsRebellions;
        Assert.Equal(DiplomacyRevisionDecision.Conflict, gate.Evaluate(conflict));
    }

    [Fact]
    public void ServerSnapshotRevision_IncrementsOnlyWhenCanonicalIdentityChanges()
    {
        var sequence = new DiplomacySnapshotRevisionSequence();
        var first = ValidEmptySnapshot(revision: 99);
        var equal = ValidEmptySnapshot(revision: 12);
        var changed = ValidEmptySnapshot(revision: 0);
        changed.SettingsFingerprint = new string('a', 64);

        Assert.Equal(0, sequence.Stamp(first));
        Assert.Equal(0, sequence.Stamp(equal));
        Assert.Equal(1, sequence.Stamp(changed));
    }

    [Fact]
    public void Handler_AppliesNewRevisionOnce_AndRejectsStaleOrConflictingReplay()
    {
        var runtime = new CountingRuntime();
        var handler = CreateHandler(runtime);
        var current = ValidEmptySnapshot(revision: 3);

        handler.ApplySnapshot(current);
        Assert.Equal(DiplomacySnapshotApplyStatus.Applied, handler.LastApplyResult.Status);
        handler.ApplySnapshot(ValidEmptySnapshot(revision: 3));
        Assert.Equal(DiplomacySnapshotApplyStatus.AlreadyCurrent, handler.LastApplyResult.Status);
        handler.ApplySnapshot(ValidEmptySnapshot(revision: 2));
        Assert.Equal(DiplomacySnapshotApplyStatus.StaleRevision, handler.LastApplyResult.Status);

        var conflict = ValidEmptySnapshot(revision: 3);
        conflict.FriendSeparatismOwnsRebellions = !current.FriendSeparatismOwnsRebellions;
        handler.ApplySnapshot(conflict);
        Assert.Equal(DiplomacySnapshotApplyStatus.RevisionConflict, handler.LastApplyResult.Status);
        Assert.Equal(1, runtime.ApplyCount);
    }

    [Fact]
    public void Handler_EnablesDiplomacyUiOnlyAfterSnapshotApplyAndRevisionCommit()
    {
        ModInformation.IsServer = false;
        var lifecycle = new RecordingUiLifecycle();
        var handler = CreateHandler(new CountingRuntime(), lifecycle: lifecycle);

        handler.ApplySnapshot(ValidEmptySnapshot(revision: 3));

        Assert.Equal(DiplomacySnapshotApplyStatus.Applied, handler.LastApplyResult.Status);
        Assert.Equal(1, lifecycle.MarkReadyCalls);
        Assert.True(lifecycle.IsReady);
    }

    [Fact]
    public void Handler_KeepsDiplomacyUiDisabledWhenSnapshotApplyFails()
    {
        ModInformation.IsServer = false;
        var lifecycle = new RecordingUiLifecycle();
        var handler = CreateHandler(
            new StubRuntime(new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.ApplyFailed, "apply failed")),
            lifecycle: lifecycle);

        handler.ApplySnapshot(ValidEmptySnapshot(revision: 3));

        Assert.Equal(DiplomacySnapshotApplyStatus.ApplyFailed, handler.LastApplyResult.Status);
        Assert.Equal(0, lifecycle.MarkReadyCalls);
        Assert.False(lifecycle.IsReady);
    }

    [Fact]
    public void Handler_RetriesUiEnableForAlreadyCommittedSnapshotWithoutReapplyingState()
    {
        ModInformation.IsServer = false;
        var runtime = new CountingRuntime();
        var lifecycle = new RecordingUiLifecycle { FailuresRemaining = 1 };
        var handler = CreateHandler(runtime, lifecycle: lifecycle);
        var snapshot = ValidEmptySnapshot(revision: 3);

        handler.ApplySnapshot(snapshot);
        Assert.Equal(DiplomacySnapshotApplyStatus.ApplyFailed, handler.LastApplyResult.Status);
        Assert.False(lifecycle.IsReady);

        handler.ApplySnapshot(ValidEmptySnapshot(revision: 3));

        Assert.Equal(DiplomacySnapshotApplyStatus.AlreadyCurrent, handler.LastApplyResult.Status);
        Assert.True(lifecycle.IsReady);
        Assert.Equal(2, lifecycle.MarkReadyCalls);
        Assert.Equal(1, runtime.ApplyCount);
    }

    [Fact]
    public void Handler_ReappliesTrustedSnapshotWhenKingdomUiManagerWasLost()
    {
        ModInformation.IsServer = false;
        var runtime = new ReadinessRuntime();
        var lifecycle = new RecordingUiLifecycle();
        var handler = CreateHandler(runtime, lifecycle: lifecycle);
        var snapshot = ValidEmptySnapshot(revision: 3);

        handler.ApplySnapshot(snapshot);
        runtime.DropUiDependencies();

        Assert.True(handler.TryEnsureClientUiReady(out var failure), failure);
        Assert.True(runtime.UiDependenciesReady);
        Assert.Equal(2, runtime.ApplyCount);
        Assert.True(lifecycle.IsReady);
    }

    [Fact]
    public void Handler_BlocksKingdomUiWithoutTrustedSnapshot()
    {
        ModInformation.IsServer = false;
        var handler = CreateHandler(new ReadinessRuntime());

        Assert.False(handler.TryEnsureClientUiReady(out var failure));
        Assert.Contains("snapshot", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthoritativePublisher_SendsEachChangedRevisionOnce()
    {
        ModInformation.IsServer = true;
        var revisionZero = ValidEmptySnapshot(revision: 0);
        var revisionOne = ValidEmptySnapshot(revision: 1);
        var runtime = new Mock<IDiplomacyRuntime>();
        runtime.SetupGet(value => value.IsAvailable).Returns(true);
        runtime.SetupSequence(value => value.CaptureSnapshot())
            .Returns(revisionZero)
            .Returns(revisionZero)
            .Returns(revisionOne);
        var network = new Mock<INetwork>();
        var publisher = new DiplomacySnapshotPublisher(network.Object, runtime.Object);

        publisher.PublishIfChanged();
        publisher.PublishIfChanged();
        publisher.PublishIfChanged();

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Exactly(2));
    }

    [Fact]
    public void SnapshotCodec_RejectsNonFiniteStateBeforeRuntimeMutation()
    {
        var snapshot = ValidEmptySnapshot(revision: 0);
        snapshot.State.Add(new DiplomacyStateEntry
        {
            Section = DiplomacyRuntime.ExpansionismSection,
            Faction1Id = "kingdom",
            Value1 = float.NaN,
        });
        snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);
        var runtime = new CountingRuntime();
        var handler = CreateHandler(runtime);

        handler.ApplySnapshot(snapshot);

        Assert.Equal(DiplomacySnapshotApplyStatus.MalformedSnapshot, handler.LastApplyResult.Status);
        Assert.Equal(0, runtime.ApplyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotCodec_RejectsOversizedWireLists(bool stateList)
    {
        var snapshot = ValidEmptySnapshot(revision: 0);
        if (stateList)
        {
            snapshot.State = Enumerable.Range(0, DiplomacySnapshotCodec.MaximumStateEntries + 1)
                .Select(index => new DiplomacyStateEntry
                {
                    Section = DiplomacyRuntime.ExpansionismSection,
                    Key = DiplomacyRuntime.MarkerKey,
                })
                .ToList();
        }
        else
        {
            snapshot.Settings = Enumerable.Range(0, DiplomacySnapshotCodec.MaximumSettings + 1)
                .Select(index => new DiplomacySettingEntry
                {
                    Name = $"Setting{index}",
                    TypeName = "System.Boolean",
                    Value = "true",
                })
                .ToList();
        }
        snapshot.SettingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);

        Assert.False(DiplomacySnapshotCodec.TryValidate(snapshot, out var failure));
        Assert.Contains("bounded", failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void SnapshotCodec_RejectsEveryOversizedOrMalformedStringFamily(int field)
    {
        var snapshot = ValidEmptySnapshot(revision: 0);
        var setting = new DiplomacySettingEntry
        {
            Name = "Setting",
            TypeName = "System.String",
            Value = "value",
        };
        var state = new DiplomacyStateEntry
        {
            Section = DiplomacyRuntime.ExpansionismSection,
            Faction1Id = "kingdom",
        };

        switch (field)
        {
            case 0: snapshot.AssemblyVersion = new string('a', 65); break;
            case 1: snapshot.CampaignId = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 2: setting.Name = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 3: setting.TypeName = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 4: setting.Value = new string('a', DiplomacySnapshotCodec.MaximumSettingValueLength + 1); break;
            case 5: state.Key = new string('a', DiplomacySnapshotCodec.MaximumKeyLength + 1); break;
            case 6: state.Faction1Id = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 7: state.Faction2Id = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 8: state.Section = new string('a', DiplomacySnapshotCodec.MaximumIdentityLength + 1); break;
            case 9: snapshot.CampaignId = "bad\0identity"; break;
            case 10: snapshot.CampaignId = "bad" + '\ud800'; break;
            case 11: snapshot.AssemblySha256 = "not-a-sha"; break;
            case 12: snapshot.SettingsFingerprint = "not-a-sha"; break;
            case 13: snapshot.StateFingerprint = "not-a-sha"; break;
        }

        if (field is >= 2 and <= 4) snapshot.Settings.Add(setting);
        if (field is >= 5 and <= 8) snapshot.State.Add(state);
        if (field != 12)
            snapshot.SettingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        if (field != 13)
            snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);

        Assert.False(DiplomacySnapshotCodec.TryValidate(snapshot, out _));
    }

    [Fact]
    public void SnapshotCodec_RejectsNonFiniteSettingBeforeRuntimeMutation()
    {
        var snapshot = ValidEmptySnapshot(revision: 0);
        snapshot.Settings.Add(new DiplomacySettingEntry
        {
            Name = "Rate",
            TypeName = "System.Single",
            Value = "NaN",
        });
        snapshot.SettingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        var runtime = new CountingRuntime();
        var handler = CreateHandler(runtime);

        handler.ApplySnapshot(snapshot);

        Assert.Equal(DiplomacySnapshotApplyStatus.MalformedSnapshot, handler.LastApplyResult.Status);
        Assert.Equal(0, runtime.ApplyCount);
    }

    [Fact]
    public void SnapshotCodec_RejectsMissingRequiredManagerMarkerBeforeAnyApply()
    {
        var snapshot = ValidEmptySnapshot(revision: 0);
        snapshot.State.RemoveAll(entry => entry.Section == DiplomacyRuntime.AgreementSection);
        snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);

        Assert.False(DiplomacySnapshotCodec.TryValidate(snapshot, out var failure));
        Assert.Contains("missing required manager section", failure);
    }

    [Fact]
    public void SnapshotCodec_AcceptsCompleteEmptyManagerShape()
    {
        var snapshot = ValidEmptySnapshot(revision: 0);

        Assert.True(DiplomacySnapshotCodec.TryValidate(snapshot, out var failure), failure);
        Assert.Equal(
            DiplomacySnapshotCodec.RequiredSections.OrderBy(value => value),
            snapshot.State.Select(entry => entry.Section).OrderBy(value => value));
    }

    [Fact]
    public void ServerCapture_PreparesEveryRequiredManagerBeforeShapeValidation()
    {
        var calls = new List<string>();

        DiplomacyManagerCaptureBarrier.RequireReady(
            manager => calls.Add("ensure:" + manager),
            () =>
            {
                calls.Add("validate");
                return null;
            });

        Assert.Equal(
            DiplomacyManagerCaptureBarrier.RequiredManagerTypeNames
                .Select(manager => "ensure:" + manager)
                .Append("validate"),
            calls);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DiplomacyManagerCaptureBarrier.RequireReady(_ => { }, () => "missing dictionary"));
        Assert.Contains("missing dictionary", exception.Message);
    }

    [Fact]
    public void AgreementCapture_UsesTheModsActualFactionPairNamespace()
    {
        Assert.Equal("Diplomacy.FactionPair", DiplomacyManagerCaptureBarrier.AgreementKeyTypeName);
    }

    [Fact]
    public void RebelFactionManager_IsEnsuredOnBothRoles_SoKingdomTabCostMathCannotNullRef()
    {
        // 2026-08-13 black Kingdom tab: KingdomWarItemVMMixin -> DiplomacyCostCalculator
        // -> KingdomExtensions.IsRebelKingdomOf reads RebelFactionManager.AllRebelFactions
        // (=> Instance.RebelFactions). Friend Edition retires Diplomacy's civil war, so nothing
        // else ever constructs the singleton; it must be ensured (empty) wherever the other four
        // managers are.
        Assert.Contains(
            "Diplomacy.CivilWar.RebelFactionManager",
            DiplomacyManagerCaptureBarrier.RequiredManagerTypeNames);
    }

    [Theory]
    [InlineData("42+731", true)]
    [InlineData("kingdom_vlandia+kingdom_battania", false)]
    [InlineData("731+42", false)]
    public void WarExhaustionManagerKey_UsesSortedNumericMbGuidTokens(string key, bool expected)
    {
        Assert.Equal(expected, DiplomacyRuntime.TrySplitWarManagerKey(key, out _, out _));
    }

    [Fact]
    public void FailedSnapshotTransaction_RestoresSettingsAndEveryDictionaryEntry()
    {
        var setting = new FakeMutableSetting { Value = 17 };
        var eventRecords = new List<string> { "old-event" };
        var dictionary = new Hashtable
        {
            ["old"] = eventRecords,
            ["score"] = 12.5f,
        };
        var transaction = new DiplomacyMutationTransaction(
            new[]
            {
                new DiplomacyValueBackupTarget(
                    () => setting.Value,
                    value => setting.Value = (int)value),
            },
            new[] { dictionary });

        setting.Value = 99;
        dictionary.Clear();
        dictionary["replacement"] = 1;

        Assert.True(transaction.TryRestore(out var failure), failure);
        Assert.Equal(17, setting.Value);
        Assert.Equal(2, dictionary.Count);
        Assert.Same(eventRecords, dictionary["old"]);
        Assert.Equal(12.5f, dictionary["score"]);
    }

    [Fact]
    public void FailedSnapshotTransaction_RestoresDictionaryAfterSettingSideEffects()
    {
        var setting = new FakeMutableSetting { Value = 17 };
        var dictionary = new Hashtable { ["old"] = "authoritative" };
        var transaction = new DiplomacyMutationTransaction(
            new[]
            {
                new DiplomacyValueBackupTarget(
                    () => setting.Value,
                    value =>
                    {
                        setting.Value = (int)value;
                        dictionary.Clear();
                    }),
            },
            new[] { dictionary });

        setting.Value = 99;
        dictionary.Clear();
        dictionary["replacement"] = "diverged";

        Assert.True(transaction.TryRestore(out var failure), failure);
        Assert.Equal(17, setting.Value);
        Assert.Equal("authoritative", Assert.Single(dictionary.Values.Cast<string>()));
        Assert.True(dictionary.Contains("old"));
    }

    [Fact]
    public void NullBrokerOrigins_CannotRequestOrApplySnapshots()
    {
        var network = new Mock<INetwork>(MockBehavior.Strict);
        var runtime = new CountingRuntime();
        var handler = CreateHandler(runtime, network.Object);

        ModInformation.IsServer = true;
        handler.HandleSnapshotRequest(new MessagePayload<NetworkRequestDiplomacySnapshot>(
            null,
            new NetworkRequestDiplomacySnapshot()));
        ModInformation.IsServer = false;
        handler.HandleSnapshot(new MessagePayload<NetworkDiplomacySnapshot>(
            null,
            ValidEmptySnapshot(revision: 0)));

        network.VerifyNoOtherCalls();
        Assert.Equal(0, runtime.ApplyCount);
    }

    [Fact]
    public void SnapshotRequest_BeforePlayerMapping_StillReceivesAuthoritativeSnapshot()
    {
        ModInformation.IsServer = true;
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
        var peer = (NetPeer)FormatterServices.GetUninitializedObject(typeof(NetPeer));
        int peerSnapshotSends = 0;
        var network = new Mock<INetwork>(MockBehavior.Strict);
        network.Setup(value => value.SendAll(It.IsAny<NetworkDiplomacySnapshot>()));
        network.Setup(value => value.Send(peer, It.IsAny<NetworkDiplomacySnapshot>()))
            .Callback(() => peerSnapshotSends++);
        var handler = CreateHandler(new CountingRuntime(), network.Object);

        // This assembly owns a continuously pumping game-loop thread. Re-marking GameThread from
        // the xUnit worker races that pump and can kill the entire test host with "Wrong thread!".
        // Marshal the server callbacks onto the real test pump, which also matches production.
        GameThread.Run(() =>
        {
            handler.HandleCampaignReady(new MessagePayload<CampaignReady>(this, new CampaignReady()));
            handler.HandleSnapshotRequest(new MessagePayload<NetworkRequestDiplomacySnapshot>(
                peer,
                new NetworkRequestDiplomacySnapshot(CurrentConfigSnapshot())));
        }, blocking: true);

        Assert.Equal(1, peerSnapshotSends);
    }

    [Fact]
    public void DiplomacyConfigBarrier_SubscribesOnlyToPostCommitAuthorityEvent()
    {
        var broker = new Mock<IMessageBroker>();
        _ = new DiplomacyCompatibilityHandler(
            broker.Object,
            new Mock<INetwork>().Object,
            new CountingRuntime(),
            new TestConfigAuthority(CurrentConfigSnapshot()),
            new RecordingUiLifecycle());

        broker.Verify(value => value.Subscribe(
            It.IsAny<Action<MessagePayload<HostModConfigAccepted>>>()), Times.Once);
        broker.Verify(value => value.Subscribe(
            It.IsAny<Action<MessagePayload<NetworkLoadModConfig>>>()), Times.Never);
    }

    [Fact]
    public void DiplomacyConfigBarrier_RequiresAuthorityCurrentIdentity()
    {
        var current = CurrentConfigSnapshot();
        var authority = new TestConfigAuthority(current);
        var handler = CreateHandler(new CountingRuntime(), configAuthority: authority);
        var localAccepted = new MessagePayload<HostModConfigAccepted>(
            this,
            new HostModConfigAccepted(current));

        Assert.True(handler.IsTrustedHostConfig(localAccepted));

        authority.Current = new ModConfigSnapshot(
            new string('d', ModConfigSnapshot.SessionIdLength),
            revision: 2,
            ModConfigProvider.ModOptions,
            birthAndDeathEnabled: true);
        Assert.False(handler.IsTrustedHostConfig(localAccepted));
    }

    [Fact]
    public void TrustedServerSnapshotRejection_AbortsClientInsteadOfContinuingDiverged()
    {
        var runtime = new CountingRuntime();
        var handler = CreateHandler(runtime);
        int aborts = 0;
        string notification = null;

        bool accepted = handler.ApplyTrustedSnapshot(
            new NetworkDiplomacySnapshot(),
            () => aborts++,
            message => notification = message);

        Assert.False(accepted);
        Assert.Equal(1, aborts);
        Assert.Contains("connection was closed", notification);
        Assert.Equal(0, runtime.ApplyCount);
    }

    [Fact]
    public void MissingAuthoritativeServerCapture_AbortsSession()
    {
        ModInformation.IsServer = true;
        var handler = CreateHandler(new NullCaptureRuntime());

        Assert.Throws<InvalidOperationException>(() => handler.HandleCampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
    }

    [Fact]
    public void MissingDiplomacyRuntime_AbortsCampaignStartupInsteadOfServingWithoutTheAdvertisedMod()
    {
        ModInformation.IsServer = true;
        var handler = CreateHandler(new UnavailableRuntime());

        var exception = Assert.Throws<InvalidOperationException>(() => handler.HandleCampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));

        Assert.Contains("Diplomacy runtime is unavailable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedClientUiReset_DoesNotLeaveCampaignReadyForADeferredSnapshotRequest()
    {
        ModInformation.IsServer = false;
        var current = CurrentConfigSnapshot();
        var authority = new TestConfigAuthority(current);
        int requests = 0;
        var network = new Mock<INetwork>(MockBehavior.Strict);
        network.Setup(value => value.SendAll(It.IsAny<NetworkRequestDiplomacySnapshot>()))
            .Callback(() => requests++);
        var handler = CreateHandler(
            new CountingRuntime(),
            network.Object,
            authority,
            new ThrowingUiLifecycle());

        Assert.Throws<InvalidOperationException>(() => handler.HandleCampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));

        handler.HandleHostModConfig(new MessagePayload<HostModConfigAccepted>(
            this,
            new HostModConfigAccepted(current)));

        Assert.Equal(0, requests);
    }

    [Fact]
    public void SnapshotRequestGate_RateLimitsRepeatedAuthenticatedPeerCapture()
    {
        long now = 100;
        var gate = new DiplomacySnapshotRequestGate<object>(() => now, minimumInterval: 10);
        var peer = new object();

        Assert.True(gate.TryAccept(peer));
        now = 109;
        Assert.False(gate.TryAccept(peer));
        now = 110;
        Assert.True(gate.TryAccept(peer));
    }

    private sealed class StubRuntime : IDiplomacyRuntime
    {
        private readonly DiplomacySnapshotApplyResult result;

        public StubRuntime(DiplomacySnapshotApplyResult result) => this.result = result;

        public bool IsAvailable => true;
        public string AssemblyVersion => "1.4.7.0";
        public NetworkDiplomacySnapshot CaptureSnapshot() => new();
        public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot) => result;
        public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.AlreadyCurrent);
        public void ResetSnapshotRevision() { }
    }

    private sealed class CountingRuntime : IDiplomacyRuntime
    {
        public int ApplyCount { get; private set; }
        public bool IsAvailable => true;
        public string AssemblyVersion => DiplomacyCompatibilityPolicy.SupportedAssemblyVersion;
        public NetworkDiplomacySnapshot CaptureSnapshot() => ValidEmptySnapshot(revision: 0);
        public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot)
        {
            ApplyCount++;
            return new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.Applied);
        }
        public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.AlreadyCurrent);
        public void ResetSnapshotRevision() { }
    }

    private sealed class NullCaptureRuntime : IDiplomacyRuntime
    {
        public bool IsAvailable => true;
        public string AssemblyVersion => DiplomacyCompatibilityPolicy.SupportedAssemblyVersion;
        public NetworkDiplomacySnapshot CaptureSnapshot() => null;
        public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.ApplyFailed);
        public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.ApplyFailed);
        public void ResetSnapshotRevision() { }
    }

    private sealed class UnavailableRuntime : IDiplomacyRuntime
    {
        public bool IsAvailable => false;
        public string AssemblyVersion => null;
        public NetworkDiplomacySnapshot CaptureSnapshot() => null;
        public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.ApplyFailed);
        public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
            new(DiplomacySnapshotApplyStatus.ApplyFailed);
        public void ResetSnapshotRevision() { }
    }

    private sealed class ReadinessRuntime : IDiplomacyRuntime
    {
        public int ApplyCount { get; private set; }
        public bool UiDependenciesReady { get; private set; }
        public bool IsAvailable => true;
        public string AssemblyVersion => DiplomacyCompatibilityPolicy.SupportedAssemblyVersion;
        public NetworkDiplomacySnapshot CaptureSnapshot() => ValidEmptySnapshot(revision: 0);
        public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot)
        {
            ApplyCount++;
            UiDependenciesReady = true;
            return new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.Applied);
        }
        public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
            UiDependenciesReady
                ? new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.AlreadyCurrent)
                : new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.ApplyFailed,
                    "required manager singleton is missing");
        public void DropUiDependencies() => UiDependenciesReady = false;
        public void ResetSnapshotRevision() { }
    }

    private sealed class RecordingUiLifecycle : IDiplomacyClientUiLifecycle
    {
        public bool IsReady { get; private set; }
        public int ResetCalls { get; private set; }
        public int MarkReadyCalls { get; private set; }
        public int FailuresRemaining { get; set; }

        public void ResetForCampaign()
        {
            ResetCalls++;
            IsReady = false;
        }

        public bool TryMarkSnapshotReady(out string failure)
        {
            MarkReadyCalls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                failure = "test UI enable failure";
                IsReady = false;
                return false;
            }

            failure = null;
            IsReady = true;
            return true;
        }
    }

    private sealed class ThrowingUiLifecycle : IDiplomacyClientUiLifecycle
    {
        public bool IsReady => false;

        public void ResetForCampaign() =>
            throw new InvalidOperationException("test client UI reset failure");

        public bool TryMarkSnapshotReady(out string failure)
        {
            failure = "test client UI reset failure";
            return false;
        }
    }

    [Fact]
    public void ServiceDiscovery_UsesTheProductionAssembly_NotTestNamespaceLookalikes()
    {
        var module = new ServiceModule();
        var discover = typeof(ServiceModule).GetMethod(
            "GetGameAbstractions",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
                       throw new MissingMethodException(typeof(ServiceModule).FullName, "GetGameAbstractions");

        var services = Assert.IsAssignableFrom<IEnumerable<Type>>(discover.Invoke(module, null)).ToArray();

        Assert.Contains(typeof(DiplomacyClientUiLifecycle), services);
        Assert.DoesNotContain(typeof(RecordingUiLifecycle), services);
        Assert.DoesNotContain(typeof(ThrowingUiLifecycle), services);
        Assert.All(services, type => Assert.Same(typeof(ServiceModule).Assembly, type.Assembly));
    }

    private static NetworkDiplomacySnapshot ValidEmptySnapshot(long revision)
    {
        var snapshot = new NetworkDiplomacySnapshot
        {
            AssemblyVersion = DiplomacyCompatibilityPolicy.SupportedAssemblyVersion,
            AssemblySha256 = DiplomacyCompatibilityPolicy.SupportedAssemblySha256,
            CampaignId = "campaign-test",
            Revision = revision,
            StateSchema = DiplomacyRuntime.CurrentStateSchema,
            FriendSeparatismOwnsRebellions = DiplomacyCompatibilityPolicy.FriendSeparatismOwnsRebellions,
            Settings = new List<DiplomacySettingEntry>(),
            State = DiplomacySnapshotCodec.RequiredSections
                .Select(section => new DiplomacyStateEntry
                {
                    Section = section,
                    Key = DiplomacyRuntime.MarkerKey,
                })
                .ToList(),
        };
        snapshot.SettingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);
        return snapshot;
    }

    private static DiplomacyCompatibilityHandler CreateHandler(
        IDiplomacyRuntime runtime,
        INetwork network = null,
        IModConfigAuthority configAuthority = null,
        IDiplomacyClientUiLifecycle lifecycle = null)
    {
        return new DiplomacyCompatibilityHandler(
            new Mock<IMessageBroker>().Object,
            network ?? new Mock<INetwork>().Object,
            runtime,
            configAuthority ?? new TestConfigAuthority(CurrentConfigSnapshot()),
            lifecycle ?? new RecordingUiLifecycle());
    }

    private static ModConfigSnapshot CurrentConfigSnapshot() => new(
        new string('c', ModConfigSnapshot.SessionIdLength),
        revision: 1,
        ModConfigProvider.ModOptions,
        birthAndDeathEnabled: true);

    private sealed class TestConfigAuthority : IModConfigAuthority
    {
        internal ModConfigSnapshot Current { get; set; }
        private object trustedServer;

        internal TestConfigAuthority(ModConfigSnapshot current = null) => Current = current;

        public bool TryGetCurrent(out ModConfigSnapshot snapshot)
        {
            snapshot = Current;
            return snapshot != null;
        }

        public ModConfigSnapshot InitializeHost(ModConfigData data) => Current;

        public ModConfigAcceptanceResult AcceptClientSnapshot(ModConfigSnapshot snapshot)
        {
            Current = snapshot;
            return new ModConfigAcceptanceResult(ModConfigAcceptanceStatus.Accepted);
        }

        public bool IsCurrent(ModConfigSnapshot snapshot) =>
            snapshot != null && Current != null &&
            snapshot.ProtocolVersion == Current.ProtocolVersion &&
            snapshot.Revision == Current.Revision &&
            string.Equals(snapshot.SessionId, Current.SessionId, StringComparison.Ordinal) &&
            string.Equals(snapshot.Sha256, Current.Sha256, StringComparison.Ordinal);

        public bool TryBindTrustedServer(object transportPeer, out string failure)
        {
            if (transportPeer == null)
            {
                failure = "missing transport peer";
                return false;
            }
            if (trustedServer == null || ReferenceEquals(trustedServer, transportPeer))
            {
                trustedServer = transportPeer;
                failure = null;
                return true;
            }

            failure = "different transport peer";
            return false;
        }

        public bool IsTrustedServer(object transportPeer) =>
            transportPeer != null && ReferenceEquals(trustedServer, transportPeer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DummyInfluenceFormula(int value) => value;

    private static int dummyLocalMcmInfluence = 999;

    private static bool DummyInfluencePrefix(ref int __result)
    {
        __result = dummyLocalMcmInfluence;
        return false;
    }

    private static FakeMapEventSide CompleteSide() => new()
    {
        LeaderParty = CompleteParty(),
        Parties = new[] { new FakeMapEventParty { Party = CompleteParty() } },
    };

    private static FakeParty CompleteParty() => new() { IsMobile = true, MobileParty = new object() };

    private sealed class FakeMapEvent
    {
        public FakeMapEventSide AttackerSide { get; set; }
        public FakeMapEventSide DefenderSide { get; set; }
        public int WinningSide { get; set; } = 0;
        public int DefeatedSide { get; set; } = 1;
        public FakeMapEventSide GetMapEventSide(int side) => side == 0 ? AttackerSide : DefenderSide;
    }

    private sealed class FakeMapEventSide
    {
        public object LeaderParty { get; set; }
        public FakeMapEventParty[] Parties { get; set; }
    }

    private sealed class FakeMapEventParty
    {
        public object Party { get; set; }
    }

    private sealed class FakeParty
    {
        public bool IsMobile { get; set; }
        public object MobileParty { get; set; }
    }

    private sealed class FakeMutableSetting
    {
        public int Value { get; set; }
    }
}
