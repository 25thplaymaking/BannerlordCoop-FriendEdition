using HarmonyLib;
using Missions.WorkshopMods.Combat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.WorkshopMods.Combat;

[Collection(nameof(CombatModHarmonyInventoryCollection))]
public sealed class CombatModCompatibilityTests
{
    private const string SyntheticRbmOwner = "com.rbmcombat";

    [Theory]
    [InlineData("DismembermentPlus", "2.0.8.7", "FC16D8C5F455710B7960848C8F27A3DB0551E1A028BB128BF7CE1C6A95F79DD1")]
    [InlineData("UnblockableThrust", "1.1.3.1", "FF73B80A598BCE31E8D620FAE84E21C7F633F767F03E5F192E05169425DC83DF")]
    [InlineData("RBMCombat", "1.0.0.0", "4629E2E331AC551D40F998413F2FB5400D4D74E958CBAECC675A312CE615A7E1")]
    public void AuditedBinaryFingerprints_AreAccepted(string name, string version, string hash)
    {
        Assert.True(CombatModFingerprintCatalog.IsAccepted(name, new Version(version), hash));
    }

    [Fact]
    public void ChangedBinaryFingerprint_IsRejected()
    {
        Assert.False(CombatModFingerprintCatalog.IsAccepted(
            "RBMCombat",
            new Version(1, 0, 0, 0),
            new string('0', 64)));
    }

    [Fact]
    public void RbmFamily_RequiresAllFiveAuditedAssemblies()
    {
        string[] names = CombatModFingerprintCatalog.Entries
            .Where(entry => entry.Family == CombatModFamily.Rbm434)
            .Select(entry => entry.AssemblyName)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[] { "RBM", "RBMAI", "RBMCombat", "RBMConfig", "RBMTournament" },
            names);
    }

    [Theory]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "RegisterBlow")]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "MeleeHitCallback")]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "MissileHitCallback")]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "ChargeDamageCallback")]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "HandleMissileCollisionReaction")]
    [InlineData("TaleWorlds.MountAndBlade.Agent", "RegisterBlow")]
    [InlineData("TaleWorlds.MountAndBlade.Agent", "HandleBlow")]
    [InlineData("TaleWorlds.MountAndBlade.Agent", "OnShieldDamaged")]
    [InlineData("SandBox.Missions.MissionLogics.BattleAgentLogic", "OnAgentHit")]
    public void CoopOwnedHitTargets_RemoveCollidingRbmPatch(string type, string method)
    {
        Assert.Equal(
            CombatPatchDisposition.Remove,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmcombat",
                type,
                method,
                isServer: false));
    }

    [Fact]
    public void RbmProjectileDecision_IsLocalAuthorityOnly()
    {
        Assert.Equal(
            CombatPatchDisposition.LocalMissionAuthorityOnly,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmcombat",
                "TaleWorlds.MountAndBlade.Mission",
                "OnAgentShootMissile",
                isServer: false));
    }

    [Theory]
    [InlineData("TaleWorlds.MountAndBlade.Mission", "OnAgentHit")]
    [InlineData("TaleWorlds.MountAndBlade.HumanAIComponent", "OnTick")]
    [InlineData("SandBox.GameComponents.SandboxAgentStatCalculateModel", "SetAiRelatedProperties")]
    [InlineData("TaleWorlds.MountAndBlade.Agent", "SetFiringOrder")]
    [InlineData("TaleWorlds.MountAndBlade.Formation", "SetMovementOrder")]
    [InlineData("TaleWorlds.MountAndBlade.Agent", "UpdateFormationOrders")]
    [InlineData("TaleWorlds.MountAndBlade.TacticFullScaleAttack", "Advance")]
    [InlineData("TaleWorlds.MountAndBlade.TacticFullScaleAttack", "ManageFormationCounts")]
    public void RbmAiMutations_AreDefaultDeniedOnClientAndRetainedOnServer(string type, string method)
    {
        Assert.Equal(
            CombatPatchDisposition.ServerOnly,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmai",
                type,
                method,
                isServer: false));
        Assert.Equal(
            CombatPatchDisposition.Keep,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmai",
                type,
                method,
                isServer: true));
    }

    [Fact]
    public void RbmAiAssemblyIdentity_ClosesHarmonyOwnerAliasGap()
    {
        Assert.Equal(
            CombatPatchDisposition.ServerOnly,
            CombatModCompatibilityGuard.ClassifyPatch(
                "unexpected.owner.alias",
                "TaleWorlds.MountAndBlade.Formation",
                "SetMovementOrder",
                isServer: false,
                patchAssemblyName: "RBMAI"));
    }

    [Fact]
    public void OptionalPatchInstanceAgent_IsRecognizedAsAuthoritySource()
    {
        var patchMethod = typeof(CombatModCompatibilityTests)
            .GetMethod(nameof(FakeAgentInstancePatch), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.Equal(0, CombatModCompatibilityGuard.FindAuthorityAgentParameterIndex(patchMethod));
    }

    [Fact]
    public void RbmTournamentPatch_IsDeniedOnClientButKeptOnServer()
    {
        Assert.Equal(
            CombatPatchDisposition.ServerOnly,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmt",
                "TaleWorlds.CampaignSystem.TournamentGames.TournamentManager",
                "GivePrizeToWinner",
                isServer: false));
        Assert.Equal(
            CombatPatchDisposition.Keep,
            CombatModCompatibilityGuard.ClassifyPatch(
                "com.rbmt",
                "TaleWorlds.CampaignSystem.TournamentGames.TournamentManager",
                "GivePrizeToWinner",
                isServer: true));
    }

    [Fact]
    public void CriticalRbmPatchInventory_IsEmptyAfterInitialAndSecondPatchWaves()
    {
        MethodInfo target = AccessTools.Method(typeof(Mission), "RegisterBlow");
        MethodInfo prefix = AccessTools.Method(
            typeof(CombatModCompatibilityTests),
            nameof(FakeRbmCriticalPrefix));
        Assert.NotNull(target);
        Assert.NotNull(prefix);

        // Use RBM's real Harmony owner against Coop's real critical callback.  The synthetic
        // patch assembly intentionally cannot pass the audited fingerprint, just like a changed
        // workshop binary, and must disappear from the live Harmony inventory immediately.
        var rbmHarmony = new Harmony(SyntheticRbmOwner);
        try
        {
            rbmHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert.Contains(SyntheticRbmOwner, GetPatchOwners(target));

            CombatModCompatibilityGuard.Initialize();
            Assert.DoesNotContain(SyntheticRbmOwner, GetPatchOwners(target));

            // RBM applies a second Harmony wave from OnGameStart.  Its guarded postfix must
            // sanitize that new inventory too, rather than relying on the first startup pass.
            rbmHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert.Contains(SyntheticRbmOwner, GetPatchOwners(target));

            CombatModCompatibilityGuard.AfterRbmPatchApply();
            Assert.DoesNotContain(SyntheticRbmOwner, GetPatchOwners(target));
        }
        finally
        {
            rbmHarmony.Unpatch(target, HarmonyPatchType.All, SyntheticRbmOwner);
        }
    }

    [Fact]
    public void RbmAiPatchInventory_IsPurgedFromConcreteMutationTargets()
    {
        MethodInfo movementOrder = AccessTools.Method(typeof(Formation), "SetMovementOrder");
        MethodInfo formationOrders = AccessTools.Method(typeof(Agent), "UpdateFormationOrders");
        MethodInfo prefix = AccessTools.Method(
            typeof(CombatModCompatibilityTests),
            nameof(FakeRbmAiMutationPrefix));
        Assert.NotNull(movementOrder);
        Assert.NotNull(formationOrders);
        Assert.NotNull(prefix);

        var rbmAiHarmony = new Harmony("com.rbmai");
        try
        {
            rbmAiHarmony.Patch(movementOrder, prefix: new HarmonyMethod(prefix));
            rbmAiHarmony.Patch(formationOrders, prefix: new HarmonyMethod(prefix));
            Assert.Contains("com.rbmai", GetPatchOwners(movementOrder));
            Assert.Contains("com.rbmai", GetPatchOwners(formationOrders));

            CombatModCompatibilityGuard.Initialize();

            Assert.DoesNotContain("com.rbmai", GetPatchOwners(movementOrder));
            Assert.DoesNotContain("com.rbmai", GetPatchOwners(formationOrders));
        }
        finally
        {
            rbmAiHarmony.Unpatch(movementOrder, HarmonyPatchType.All, "com.rbmai");
            rbmAiHarmony.Unpatch(formationOrders, HarmonyPatchType.All, "com.rbmai");
        }
    }

    [Fact]
    public void RbmLifecycleShape_AcceptsOnlyAuditedOverrides()
    {
        MethodInfo missionInitialize = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnMissionBehaviorInitialize",
            new[] { typeof(Mission) });
        MethodInfo gameInitialize = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnGameInitializationFinished",
            new[] { typeof(Game) });
        MethodInfo changedOverload = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnGameInitializationFinished",
            new[] { typeof(string) });

        Assert.True(CombatModCompatibilityGuard.IsExpectedRbmLifecycleMethodShape(
            missionInitialize,
            RbmLifecycleEntryPoint.MissionBehaviorInitialize));
        Assert.True(CombatModCompatibilityGuard.IsExpectedRbmLifecycleMethodShape(
            gameInitialize,
            RbmLifecycleEntryPoint.GameInitializationFinished));
        Assert.False(CombatModCompatibilityGuard.IsExpectedRbmLifecycleMethodShape(
            changedOverload,
            RbmLifecycleEntryPoint.GameInitializationFinished));
    }

    [Fact]
    public void RbmPatchWaveShape_AcceptsOnlyAuditedStaticEntryPoint()
    {
        MethodInfo exact = CreateVoidEntryPoint(
            "RBM.SubModule",
            "ApplyHarmonyPatches",
            isStatic: true,
            isPublic: true);
        MethodInfo changed = CreateVoidEntryPoint(
            "RBM.SubModule",
            "ApplyHarmonyPatches",
            isStatic: true,
            isPublic: true,
            typeof(string));

        Assert.True(CombatModCompatibilityGuard.IsExpectedRbmPatchWaveMethodShape(exact));
        Assert.False(CombatModCompatibilityGuard.IsExpectedRbmPatchWaveMethodShape(changed));
    }

    [Fact]
    public void RbmLifecycleDetours_DenyClientsAndPreserveServerPath()
    {
        MethodInfo missionInitialize = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnMissionBehaviorInitialize",
            new[] { typeof(Mission) });
        MethodInfo gameInitialize = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnGameInitializationFinished",
            new[] { typeof(Game) });
        MethodInfo changedOverload = AccessTools.Method(
            typeof(RbmLifecycleShapeFixture),
            "OnGameInitializationFinished",
            new[] { typeof(string) });

        Assert.False(RbmMissionBehaviorInitializationPatch.ShouldRun(
            missionInitialize,
            guardInitialized: true,
            moduleCompatible: true,
            isServer: false,
            isCoopBattleActive: true));
        Assert.True(RbmMissionBehaviorInitializationPatch.ShouldRun(
            missionInitialize,
            guardInitialized: true,
            moduleCompatible: true,
            isServer: true,
            isCoopBattleActive: true));
        Assert.False(RbmGameInitializationFinishedPatch.ShouldRun(
            gameInitialize,
            guardInitialized: true,
            moduleCompatible: true,
            isServer: false));
        Assert.True(RbmGameInitializationFinishedPatch.ShouldRun(
            gameInitialize,
            guardInitialized: true,
            moduleCompatible: true,
            isServer: true));
        Assert.False(RbmGameInitializationFinishedPatch.ShouldRun(
            changedOverload,
            guardInitialized: true,
            moduleCompatible: true,
            isServer: true));

        // A compatible binary must not recover a server mutation path after guard initialization
        // failed and safely purged the optional Harmony inventory.
        Assert.False(RbmMissionBehaviorInitializationPatch.ShouldRun(
            missionInitialize,
            guardInitialized: false,
            moduleCompatible: true,
            isServer: true,
            isCoopBattleActive: true));
        Assert.False(RbmGameInitializationFinishedPatch.ShouldRun(
            gameInitialize,
            guardInitialized: false,
            moduleCompatible: true,
            isServer: true));
    }

    [Fact]
    public void RbmConfigEntryPoints_RequireInitializationFingerprintAndExactShape()
    {
        MethodInfo load = CreateVoidEntryPoint(
            "RBMConfig.RBMConfig", "LoadConfig", isStatic: true, isPublic: true);
        MethodInfo save = CreateVoidEntryPoint(
            "RBMConfig.RBMConfig", "saveXmlConfig", isStatic: true, isPublic: true);
        MethodInfo uiDone = CreateVoidEntryPoint(
            "RBMConfig.RBMConfigViewModel", "ExecuteDone", isStatic: false, isPublic: false);
        MethodInfo changedLoad = CreateVoidEntryPoint(
            "RBMConfig.RBMConfig",
            "LoadConfig",
            isStatic: true,
            isPublic: true,
            typeof(string));

        Assert.True(RbmConfigLoadPatch.ShouldRun(load, guardInitialized: true, moduleCompatible: true));
        Assert.True(RbmConfigSavePatch.ShouldRun(save, guardInitialized: true, moduleCompatible: true));
        Assert.True(RbmConfigUiDonePatch.ShouldRun(uiDone, guardInitialized: true, moduleCompatible: true));
        Assert.False(RbmConfigLoadPatch.ShouldRun(load, guardInitialized: false, moduleCompatible: true));
        Assert.False(RbmConfigLoadPatch.ShouldRun(load, guardInitialized: true, moduleCompatible: false));
        Assert.False(RbmConfigLoadPatch.ShouldRun(changedLoad, guardInitialized: true, moduleCompatible: true));
    }

    [Fact]
    public void CanonicalRbmGameplaySubset_ReassertsAfterPostInitializationLocalEdit()
    {
        SetOppositeRbmGameplayValues();
        Assert.True(CombatModCompatibilityGuard.TryApplyCanonicalRbmScalarValues(
            typeof(RbmConfigScalarFixture),
            out string firstFailure), firstFailure);

        // Simulate a later XML/UI edit after the guard has initialized.
        SetOppositeRbmGameplayValues();
        Assert.True(CombatModCompatibilityGuard.TryApplyCanonicalRbmScalarValues(
            typeof(RbmConfigScalarFixture),
            out string secondFailure), secondFailure);

        Assert.True(RbmConfigScalarFixture.rbmAiEnabled);
        Assert.True(RbmConfigScalarFixture.rbmCombatEnabled);
        Assert.True(RbmConfigScalarFixture.rbmTournamentEnabled);
        Assert.Equal(0.05f, RbmConfigScalarFixture.ThrustMagnitudeModifier);
        Assert.Equal(20f, RbmConfigScalarFixture.OneHandedThrustDamageBonus);
        Assert.Equal(20f, RbmConfigScalarFixture.TwoHandedThrustDamageBonus);
        Assert.False(RbmConfigScalarFixture.postureEnabled);
        Assert.False(RbmConfigScalarFixture.staminaEnabled);
    }

    [Fact]
    public void ChangedRbmConfigFieldShape_FailsClosed()
    {
        Assert.False(CombatModCompatibilityGuard.TryApplyCanonicalRbmScalarValues(
            typeof(ChangedRbmConfigScalarFixture),
            out string failure));
        Assert.Contains("field shape mismatch", failure);
    }

    [Fact]
    public void OppositeLocalRbmFamilySelections_NormalizeToIdenticalPatchWave()
    {
        RbmConfigScalarFixture.rbmTournamentEnabled = false;
        RbmConfigScalarFixture.rbmAiEnabled = false;
        RbmConfigScalarFixture.rbmCombatEnabled = false;
        Assert.True(CombatModCompatibilityGuard.TryApplyCanonicalRbmScalarValues(
            typeof(RbmConfigScalarFixture),
            out string disabledFailure), disabledFailure);
        string[] disabledSelection = SelectRbmPatchOwners();

        RbmConfigScalarFixture.rbmTournamentEnabled = true;
        RbmConfigScalarFixture.rbmAiEnabled = true;
        RbmConfigScalarFixture.rbmCombatEnabled = true;
        Assert.True(CombatModCompatibilityGuard.TryApplyCanonicalRbmScalarValues(
            typeof(RbmConfigScalarFixture),
            out string enabledFailure), enabledFailure);
        string[] enabledSelection = SelectRbmPatchOwners();

        Assert.Equal(
            new[] { "com.rbmmain", "com.rbmt", "com.rbmai", "com.rbmcombat" },
            disabledSelection);
        Assert.Equal(disabledSelection, enabledSelection);
    }

    private static string[] GetPatchOwners(MethodBase target)
    {
        Patches patches = Harmony.GetPatchInfo(target);
        if (patches == null) return Array.Empty<string>();

        return patches.Prefixes
            .Concat(patches.Postfixes)
            .Concat(patches.Transpilers)
            .Concat(patches.Finalizers)
            .Select(patch => patch.owner)
            .ToArray();
    }

    private static bool FakeRbmCriticalPrefix()
    {
        return true;
    }

    private static bool FakeRbmAiMutationPrefix()
    {
        return true;
    }

    private static bool FakeAgentInstancePatch(ref Agent __instance)
    {
        return true;
    }

    private static MethodInfo CreateVoidEntryPoint(
        string typeName,
        string methodName,
        bool isStatic,
        bool isPublic,
        params Type[] parameterTypes)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SyntheticRbmConfigShape_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("Main");
        TypeBuilder type = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);
        MethodAttributes attributes = isPublic ? MethodAttributes.Public : MethodAttributes.Private;
        if (isStatic) attributes |= MethodAttributes.Static;
        MethodBuilder method = type.DefineMethod(
            methodName,
            attributes,
            typeof(void),
            parameterTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        Type created = type.CreateType();
        return created.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);
    }

    private static void SetOppositeRbmGameplayValues()
    {
        RbmConfigScalarFixture.ThrustMagnitudeModifier = 9f;
        RbmConfigScalarFixture.OneHandedThrustDamageBonus = 9f;
        RbmConfigScalarFixture.TwoHandedThrustDamageBonus = 9f;
        RbmConfigScalarFixture.rbmTournamentEnabled = false;
        RbmConfigScalarFixture.rbmAiEnabled = false;
        RbmConfigScalarFixture.rbmCombatEnabled = false;
        RbmConfigScalarFixture.developerMode = true;
        RbmConfigScalarFixture.postureEnabled = true;
        RbmConfigScalarFixture.staminaEnabled = true;
        RbmConfigScalarFixture.playerPostureMultiplier = 9f;
        RbmConfigScalarFixture.vanillaCombatAi = true;
        RbmConfigScalarFixture.keepBattleEnabled = true;
        RbmConfigScalarFixture.realisticArrowArc = true;
        RbmConfigScalarFixture.armorMultiplier = 9f;
        RbmConfigScalarFixture.passiveShoulderShields = true;
        RbmConfigScalarFixture.troopOverhaulActive = false;
        RbmConfigScalarFixture.realisticRangedReload = "0";
        RbmConfigScalarFixture.maceBluntModifier = 9f;
        RbmConfigScalarFixture.armorThresholdModifier = 9f;
        RbmConfigScalarFixture.bluntTraumaBonus = 9f;
        RbmConfigScalarFixture.sneakAttackInstaKill = true;
    }

    private static string[] SelectRbmPatchOwners()
    {
        var owners = new List<string> { "com.rbmmain" };
        if (RbmConfigScalarFixture.rbmTournamentEnabled) owners.Add("com.rbmt");
        if (RbmConfigScalarFixture.rbmAiEnabled) owners.Add("com.rbmai");
        if (RbmConfigScalarFixture.rbmCombatEnabled) owners.Add("com.rbmcombat");
        return owners.ToArray();
    }

    private static class RbmConfigScalarFixture
    {
        public static float ThrustMagnitudeModifier;
        public static float OneHandedThrustDamageBonus;
        public static float TwoHandedThrustDamageBonus;
        public static bool rbmTournamentEnabled;
        public static bool rbmAiEnabled;
        public static bool rbmCombatEnabled;
        public static bool developerMode;
        public static bool postureEnabled;
        public static bool staminaEnabled;
        public static float playerPostureMultiplier;
        public static bool vanillaCombatAi;
        public static bool keepBattleEnabled;
        public static bool realisticArrowArc;
        public static float armorMultiplier;
        public static bool passiveShoulderShields;
        public static bool troopOverhaulActive;
        public static string realisticRangedReload = string.Empty;
        public static float maceBluntModifier;
        public static float armorThresholdModifier;
        public static float bluntTraumaBonus;
        public static bool sneakAttackInstaKill;
    }

    private static class ChangedRbmConfigScalarFixture
    {
        // The audited RBMConfig field is float.  A changed type must disable the family instead of
        // coercing an unknown configuration contract.
        public static int ThrustMagnitudeModifier = 0;
    }

    private sealed class RbmLifecycleShapeFixture : MBSubModuleBase
    {
        public override void OnMissionBehaviorInitialize(Mission mission)
        {
        }

        public override void OnGameInitializationFinished(Game game)
        {
        }

        public void OnGameInitializationFinished(string unexpected)
        {
        }
    }
}

[CollectionDefinition(nameof(CombatModHarmonyInventoryCollection), DisableParallelization = true)]
public sealed class CombatModHarmonyInventoryCollection
{
}
