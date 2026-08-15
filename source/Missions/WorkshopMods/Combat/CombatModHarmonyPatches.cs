using Common;
using Common.Logging;
using GameInterface;
using GameInterface.Services.MapEvents;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Missions.Agents.Extensions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.WorkshopMods.Combat;

[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmPatchWaveCompatibilityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        CombatModCompatibilityGuard.Initialize();
        return CombatModCompatibilityGuard.ResolveOptionalDeclaredMethods(
            "RBM",
            "RBM.SubModule",
            "ApplyHarmonyPatches");
    }

    // A same-named-but-different RBM build can leave IsFamilyPresent (the category-level gate in
    // MissionModule) true while this class's own target still fails to resolve — e.g. a renamed or
    // removed ApplyHarmonyPatches method. Prepare makes this class immune to that mismatch: Harmony
    // skips it entirely instead of throwing "Undefined target method" when TargetMethods() is empty.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod, out bool __state)
    {
        __state = CombatModCompatibilityGuard.BeforeRbmPatchApply(__originalMethod);
        return __state;
    }

    [HarmonyPostfix]
    private static void Postfix(bool __state)
    {
        if (__state)
            CombatModCompatibilityGuard.AfterRbmPatchApply();
    }
}

/// <summary>
/// RBM reloads per-user XML at several startup seams.  The original load may populate local UI
/// preferences, but the audited gameplay subset is restored in a finalizer even if the load throws
/// after partially changing statics.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmConfigLoadPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        CombatModCompatibilityGuard.Initialize();
        return CombatModCompatibilityGuard.ResolveOptionalDeclaredMethods(
            "RBMConfig",
            "RBMConfig.RBMConfig",
            "LoadConfig");
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare: this class's own target must independently resolve
    // before Harmony is allowed to try patching it, regardless of the category-level presence gate.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod, out bool __state)
    {
        __state = ShouldRun(
            __originalMethod,
            CombatModCompatibilityGuard.IsInitialized,
            CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.Rbm434));
        return __state;
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, bool __state)
    {
        if (__state)
            CombatModCompatibilityGuard.ReassertCanonicalRbmGameplayConfiguration();
        return __exception;
    }

    internal static bool ShouldRun(
        MethodBase method,
        bool guardInitialized,
        bool moduleCompatible)
    {
        return CombatModCompatibilityGuard.AllowRbmConfigEntryPoint(
            method,
            RbmConfigEntryPoint.LoadConfig,
            guardInitialized,
            moduleCompatible);
    }
}

/// <summary>
/// Canonical gameplay values are reapplied before RBM serializes its XML.  Thus a configuration UI
/// edit cannot persist a different combat formula or patch-family selection for the next reload.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmConfigSavePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return CombatModCompatibilityGuard.ResolveOptionalDeclaredMethods(
            "RBMConfig",
            "RBMConfig.RBMConfig",
            "saveXmlConfig");
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        return ShouldRun(
                __originalMethod,
                CombatModCompatibilityGuard.IsInitialized,
                CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.Rbm434))
            && CombatModCompatibilityGuard.ReassertCanonicalRbmGameplayConfiguration();
    }

    internal static bool ShouldRun(
        MethodBase method,
        bool guardInitialized,
        bool moduleCompatible)
    {
        return CombatModCompatibilityGuard.AllowRbmConfigEntryPoint(
            method,
            RbmConfigEntryPoint.SaveXmlConfig,
            guardInitialized,
            moduleCompatible);
    }
}

/// <summary>
/// ExecuteDone directly mutates RBM's public gameplay statics before saving.  Its exact audited
/// shape is guarded, and the finalizer restores the canonical subset even on an exceptional exit.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmConfigUiDonePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return CombatModCompatibilityGuard.ResolveOptionalDeclaredMethods(
            "RBMConfig",
            "RBMConfig.RBMConfigViewModel",
            "ExecuteDone");
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod, out bool __state)
    {
        __state = ShouldRun(
            __originalMethod,
            CombatModCompatibilityGuard.IsInitialized,
            CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.Rbm434));
        return __state;
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, bool __state)
    {
        if (__state)
            CombatModCompatibilityGuard.ReassertCanonicalRbmGameplayConfiguration();
        return __exception;
    }

    internal static bool ShouldRun(
        MethodBase method,
        bool guardInitialized,
        bool moduleCompatible)
    {
        return CombatModCompatibilityGuard.AllowRbmConfigEntryPoint(
            method,
            RbmConfigEntryPoint.ExecuteDone,
            guardInitialized,
            moduleCompatible);
    }
}

/// <summary>
/// RBM installs mission behaviors that mutate AI, formations and siege archer state.  During a live
/// Coop mission only the server may install them.  Any changed binary or lifecycle signature is
/// denied before the optional method executes.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmMissionBehaviorInitializationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return CombatModCompatibilityGuard.ResolveOptionalSubModuleLifecycleMethods(
            "RBM",
            "OnMissionBehaviorInitialize");
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        return ShouldRun(
            __originalMethod,
            CombatModCompatibilityGuard.IsInitialized,
            CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.Rbm434),
            ModInformation.IsServer,
            BattleSpawnGate.IsCoopBattleActive);
    }

    internal static bool ShouldRun(
        MethodBase method,
        bool guardInitialized,
        bool moduleCompatible,
        bool isServer,
        bool isCoopBattleActive)
    {
        return guardInitialized
            && CombatModAuthorityPolicy.AllowRbmMissionBehaviorInitialization(
                moduleCompatible,
                CombatModCompatibilityGuard.IsExpectedRbmLifecycleMethodShape(
                    method,
                    RbmLifecycleEntryPoint.MissionBehaviorInitialize),
                isServer,
                isCoopBattleActive);
    }
}

/// <summary>
/// RBM's game-initialization callback invokes DestroyClanAction.  Campaign state belongs to the
/// server, so clients must never execute this callback, even before a Coop battle becomes active.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Rbm)]
internal static class RbmGameInitializationFinishedPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return CombatModCompatibilityGuard.ResolveOptionalSubModuleLifecycleMethods(
            "RBM",
            "OnGameInitializationFinished");
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        return ShouldRun(
            __originalMethod,
            CombatModCompatibilityGuard.IsInitialized,
            CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.Rbm434),
            ModInformation.IsServer);
    }

    internal static bool ShouldRun(
        MethodBase method,
        bool guardInitialized,
        bool moduleCompatible,
        bool isServer)
    {
        return guardInitialized
            && CombatModAuthorityPolicy.AllowRbmGameInitializationFinished(
                moduleCompatible,
                CombatModCompatibilityGuard.IsExpectedRbmLifecycleMethodShape(
                    method,
                    RbmLifecycleEntryPoint.GameInitializationFinished),
                isServer);
    }
}

/// <summary>
/// DismembermentPlus's mission logic exists on each client so replicated severed-body presentation can
/// be applied everywhere. It is never attached on the campaign server.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.DismembermentPlus)]
internal static class DismembermentMissionInitializerPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Assembly assembly = CombatModCompatibilityGuard.ResolveOptionalAssembly("DismembermentPlus");
        foreach (Type type in CombatModCompatibilityGuard.GetLoadableTypes(assembly))
        {
            if (!typeof(MBSubModuleBase).IsAssignableFrom(type)) continue;
            foreach (MethodInfo target in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (target.Name == "OnMissionBehaviorInitialize")
                    yield return target;
            }
        }
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return CombatModAuthorityPolicy.AllowDismembermentPresentation(
            CombatModCompatibilityGuard.IsInitialized
                && CombatModCompatibilityGuard.IsFamilyCompatible(
                    CombatModFamily.DismembermentPlus2088),
            ModInformation.IsServer,
            BattleSpawnGate.IsCoopBattleActive);
    }
}

[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.DismembermentPlus)]
internal static class DismembermentRegisterBlowPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DismembermentRegisterBlowPatch));

    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase target = CombatModCompatibilityGuard.ResolveOptionalMethod(
            "DismembermentPlus.Logic.DismembermentPlusMissionLogic",
            "OnRegisterBlow");
        if (target != null) yield return target;
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(
        object __instance,
        Agent attacker,
        Agent victim,
        Blow blow,
        ref AttackCollisionData collisionData)
    {
        bool compatible = CombatModCompatibilityGuard.IsInitialized
            && CombatModCompatibilityGuard.IsFamilyCompatible(
                CombatModFamily.DismembermentPlus2088);
        if (!BattleSpawnGate.IsCoopBattleActive)
            return CombatModAuthorityPolicy.AllowDismembermentPresentation(
                compatible,
                ModInformation.IsServer,
                isCoopBattleActive: false);

        // The original method creates Random/Guid state locally. In Coop, suppress that complete
        // path and let the accepted victim-authority blow produce one deterministic cosmetic event.
        if (compatible &&
            !ModInformation.IsServer &&
            ContainerProvider.TryResolve<IDismembermentPresentationHandler>(out var handler))
        {
            handler.ProcessAcceptedBlow(__instance, attacker, victim, blow, collisionData);
        }
        return false;
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception)
    {
        if (__exception != null)
            Logger.Warning(__exception, "[WorkshopCombat] Dismemberment presentation failed; gameplay was unaffected");
        return null;
    }
}

[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.DismembermentPlus)]
internal static class DismembermentSlowMotionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase target = CombatModCompatibilityGuard.ResolveOptionalMethod(
            "DismembermentPlus.Logic.DismembermentPlusMissionLogic",
            "CheckSetSlowMotion");
        if (target != null) yield return target;
    }

    // See RbmPatchWaveCompatibilityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return CombatModCompatibilityGuard.IsInitialized
            && CombatModCompatibilityGuard.IsFamilyCompatible(
                CombatModFamily.DismembermentPlus2088)
            && !BattleSpawnGate.IsCoopBattleActive;
    }
}

/// <summary>
/// Authority-aware replacement for UnblockableThrust 1.1.3.1's postfix.  Its workshop postfix is
/// removed by <see cref="CombatModCompatibilityGuard"/>.  These are that build's default gameplay
/// settings: non-shield thrust blocks can be crushed through; shield blocks cannot.  Only the peer that
/// owns the attacker may author the collision result in Coop.
///
/// Deliberately NOT gated behind <see cref="WorkshopPatchCategories.UnblockableThrust"/> like the
/// RBM/DismembermentPlus adapters: its <see cref="TargetMethods"/> patches
/// <see cref="TaleWorlds.MountAndBlade.MissionCombatMechanicsHelper"/>, a native method that always
/// resolves whether or not the UnblockableThrust DLL is loaded, so it has none of the "empty
/// TargetMethods" defect this category split exists to fix. <see cref="ResolveCrushThrough"/> is the
/// mod-presence gate instead — it becomes a no-op passthrough when the mod isn't installed/compatible.
/// Moving this into a mod-presence-gated category would be a regression: it would silently stop
/// applying Coop's authoritative crush-through defaults whenever UnblockableThrust is absent.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(MissionModule.CombatHitPresentationPatchCategory)]
internal static class UnblockableThrustAuthorityPatch
{
    private const bool AuditedMountedOnlyDefault = false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Guarantees the workshop postfix is removed before this replacement joins the same target,
        // independent of Harmony's patch-class discovery order.
        CombatModCompatibilityGuard.Initialize();
        MethodBase target = AccessTools.Method(
            typeof(MissionCombatMechanicsHelper),
            "GetDefendCollisionResults");
        if (target != null) yield return target;
    }

    [HarmonyPostfix]
    private static void Postfix(
        Agent attackerAgent,
        Agent defenderAgent,
        CombatCollisionResult collisionResult,
        StrikeType strikeType,
        ref bool crushedThrough)
    {
        bool compatible = CombatModCompatibilityGuard.IsInitialized
            && CombatModCompatibilityGuard.IsFamilyCompatible(
                CombatModFamily.UnblockableThrust1131);
        if (attackerAgent == null || defenderAgent == null)
        {
            return;
        }

        bool localAuthority = attackerAgent.IsLocallyControlled();
        EquipmentIndex offHand = defenderAgent.GetOffhandWieldedItemIndex();
        bool blockedWithShield = false;
        if (offHand != EquipmentIndex.None)
        {
            MissionWeapon weapon = defenderAgent.Equipment[offHand];
            blockedWithShield = weapon.CurrentUsageItem != null && weapon.CurrentUsageItem.IsShield;
        }

        crushedThrough = ResolveCrushThrough(
            compatible,
            BattleSpawnGate.IsCoopBattleActive,
            localAuthority,
            crushedThrough,
            (int)strikeType,
            (int)collisionResult,
            blockedWithShield,
            attackerAgent.HasMount);
    }

    internal static bool ResolveCrushThrough(
        bool moduleCompatible,
        bool isCoopBattleActive,
        bool sourceIsLocallyControlled,
        bool alreadyCrushedThrough,
        int strikeType,
        int collisionResult,
        bool blockedWithShield,
        bool attackerIsMounted)
    {
        // Audited 1.1.3.1 default. Keep the mounted input explicit so a future configuration
        // change cannot silently drop the foot-versus-mounted authority case from this pure rule.
        if (!moduleCompatible
            || alreadyCrushedThrough
            || strikeType != 1
            || (AuditedMountedOnlyDefault && !attackerIsMounted)
            || !CombatModAuthorityPolicy.AllowMissionGameplayDecision(
                isCoopBattleActive,
                sourceIsLocallyControlled))
        {
            return alreadyCrushedThrough;
        }

        // Audited 1.1.3.1 defaults: CrushThroughShield=false, MinRelativeSpeed=0,
        // MountedOnly=false, and no player-only restriction.
        return !blockedWithShield && collisionResult == 3;
    }
}
