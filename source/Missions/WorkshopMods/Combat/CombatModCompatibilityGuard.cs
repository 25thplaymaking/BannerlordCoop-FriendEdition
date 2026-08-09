using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Missions.Agents.Extensions;
using Serilog;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.WorkshopMods.Combat;

internal enum CombatPatchDisposition
{
    Keep,
    LocalMissionAuthorityOnly,
    ServerOnly,
    Remove
}

internal enum RbmLifecycleEntryPoint
{
    MissionBehaviorInitialize,
    GameInitializationFinished
}

internal enum RbmConfigEntryPoint
{
    LoadConfig,
    SaveXmlConfig,
    ExecuteDone
}

internal sealed class RbmCanonicalConfigValue
{
    internal string Name { get; }
    internal Type FieldType { get; }
    internal object Value { get; }

    internal RbmCanonicalConfigValue(string name, Type fieldType, object value)
    {
        Name = name;
        FieldType = fieldType;
        Value = value;
    }
}

internal sealed class RbmCanonicalWeaponFactor
{
    internal string WeaponType { get; }
    internal float ExtraBluntFactorCut { get; }
    internal float ExtraBluntFactorPierce { get; }
    internal float ExtraBluntFactorBlunt { get; }
    internal float ExtraArmorThresholdFactorPierce { get; }
    internal float ExtraArmorThresholdFactorCut { get; }
    internal float ExtraArmorSkillDamageAbsorb { get; }

    internal RbmCanonicalWeaponFactor(
        string weaponType,
        float extraBluntFactorCut,
        float extraBluntFactorPierce,
        float extraBluntFactorBlunt,
        float extraArmorThresholdFactorPierce,
        float extraArmorThresholdFactorCut,
        float extraArmorSkillDamageAbsorb)
    {
        WeaponType = weaponType;
        ExtraBluntFactorCut = extraBluntFactorCut;
        ExtraBluntFactorPierce = extraBluntFactorPierce;
        ExtraBluntFactorBlunt = extraBluntFactorBlunt;
        ExtraArmorThresholdFactorPierce = extraArmorThresholdFactorPierce;
        ExtraArmorThresholdFactorCut = extraArmorThresholdFactorCut;
        ExtraArmorSkillDamageAbsorb = extraArmorSkillDamageAbsorb;
    }
}

/// <summary>
/// Reflection-only compatibility boundary for RBM, DismembermentPlus and UnblockableThrust.  It
/// deliberately has no compile-time dependency on a workshop DLL: absent modules are a no-op and a
/// changed binary is disabled instead of being optimistically executed inside the coop authority path.
/// </summary>
internal static class CombatModCompatibilityGuard
{
    internal const string GuardHarmonyId = "bannerlordcoop.friend.workshop.combat.guard";

    private static readonly ILogger Logger = LogManager.GetLogger(typeof(CombatModCompatibilityGuard));
    private static readonly object Gate = new object();
    private static readonly HashSet<MethodBase> AuthorityGuardedPatchMethods = new HashSet<MethodBase>();
    private static readonly ConcurrentDictionary<string, bool> FingerprintResults =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private static readonly string[] RbmOwners = { "com.rbmmain", "com.rbmai", "com.rbmcombat", "com.rbmt" };
    private static readonly Harmony GuardHarmony = new Harmony(GuardHarmonyId);

    internal static readonly IReadOnlyList<RbmCanonicalConfigValue> CanonicalRbmGameplayValues =
        new[]
        {
            new RbmCanonicalConfigValue("ThrustMagnitudeModifier", typeof(float), 0.05f),
            new RbmCanonicalConfigValue("OneHandedThrustDamageBonus", typeof(float), 20f),
            new RbmCanonicalConfigValue("TwoHandedThrustDamageBonus", typeof(float), 20f),
            new RbmCanonicalConfigValue("rbmTournamentEnabled", typeof(bool), true),
            new RbmCanonicalConfigValue("rbmAiEnabled", typeof(bool), true),
            new RbmCanonicalConfigValue("rbmCombatEnabled", typeof(bool), true),
            new RbmCanonicalConfigValue("developerMode", typeof(bool), false),
            // RBMAI posture/stamina callbacks collide with Coop's blow/shield owner.
            new RbmCanonicalConfigValue("postureEnabled", typeof(bool), false),
            new RbmCanonicalConfigValue("staminaEnabled", typeof(bool), false),
            new RbmCanonicalConfigValue("playerPostureMultiplier", typeof(float), 1f),
            new RbmCanonicalConfigValue("vanillaCombatAi", typeof(bool), false),
            new RbmCanonicalConfigValue("keepBattleEnabled", typeof(bool), false),
            new RbmCanonicalConfigValue("realisticArrowArc", typeof(bool), false),
            new RbmCanonicalConfigValue("armorMultiplier", typeof(float), 2f),
            new RbmCanonicalConfigValue("passiveShoulderShields", typeof(bool), false),
            new RbmCanonicalConfigValue("troopOverhaulActive", typeof(bool), true),
            new RbmCanonicalConfigValue("realisticRangedReload", typeof(string), "2"),
            new RbmCanonicalConfigValue("maceBluntModifier", typeof(float), 1f),
            new RbmCanonicalConfigValue("armorThresholdModifier", typeof(float), 1f),
            new RbmCanonicalConfigValue("bluntTraumaBonus", typeof(float), 0f),
            new RbmCanonicalConfigValue("sneakAttackInstaKill", typeof(bool), false)
        };

    private static readonly IReadOnlyList<RbmCanonicalWeaponFactor> CanonicalRbmWeaponFactors =
        new[]
        {
            new RbmCanonicalWeaponFactor("Dagger", 0.25f, 0.35f, 1f, 3f, 5f, 1f),
            new RbmCanonicalWeaponFactor("ThrowingKnife", 0.15f, 0.15f, 1f, 3f, 5f, 1f),
            new RbmCanonicalWeaponFactor("OneHandedSword", 0.25f, 0.35f, 1f, 3.5f, 5f, 1f),
            new RbmCanonicalWeaponFactor("TwoHandedSword", 0.25f, 0.35f, 1f, 3.5f, 5f, 1f),
            new RbmCanonicalWeaponFactor("OneHandedBastardAxe", 0.3f, 0.25f, 1f, 2.5f, 5f, 1f),
            new RbmCanonicalWeaponFactor("OneHandedAxe", 0.3f, 0.25f, 1f, 2.5f, 5f, 1f),
            new RbmCanonicalWeaponFactor("TwoHandedAxe", 0.3f, 0.3f, 1f, 2.5f, 5f, 1f),
            new RbmCanonicalWeaponFactor("OneHandedPolearm", 0.3f, 0.35f, 1f, 3f, 5f, 1f),
            new RbmCanonicalWeaponFactor("TwoHandedPolearm", 0.3f, 0.35f, 1f, 3f, 5f, 1f),
            new RbmCanonicalWeaponFactor("Mace", 0.1f, 0.25f, 1f, 2f, 4f, 1f),
            new RbmCanonicalWeaponFactor("TwoHandedMace", 0.1f, 0.25f, 1f, 2f, 4f, 1f),
            new RbmCanonicalWeaponFactor("Arrow", 0.15f, 0.15f, 1f, 2f, 2.6f, 1f),
            new RbmCanonicalWeaponFactor("Bolt", 0.15f, 0.15f, 1f, 2f, 2.6f, 1f),
            new RbmCanonicalWeaponFactor("Javelin", 0.05f, 0.2f, 1f, 3f, 3f, 1f),
            new RbmCanonicalWeaponFactor("ThrowingAxe", 0.3f, 0.2f, 1f, 2.5f, 4f, 1f),
            new RbmCanonicalWeaponFactor("SlingStone", 0.3f, 0.35f, 1f, 6f, 10f, 1f)
        };

    private static volatile bool initialized;

    internal static void Initialize()
    {
        lock (Gate)
        {
            // Readers outside this lock must fail closed while a new patch inventory is being
            // normalized and verified.
            initialized = false;
            try
            {
                // Re-run sanitization: RBM intentionally unpatches/repatches itself in OnGameStart.
                // The operation is idempotent and catches that second patch wave.
                if (CombatModFingerprintCatalog.IsFamilyPresent(
                        CombatModFamily.Rbm434,
                        AppDomain.CurrentDomain.GetAssemblies())
                    && !TryNormalizeRbmGameplayConfiguration(out string normalizationFailure))
                {
                    throw new InvalidOperationException(normalizationFailure);
                }

                SanitizeLoadedPatches();
                initialized = true;
            }
            catch (Exception exception)
            {
                // A failed optional integration may continue only after every known optional patch
                // has been removed and that empty inventory has been verified.
                initialized = false;
                Logger.Error(
                    exception,
                    "[WorkshopCombat] Compatibility guard initialization failed; disabling all known optional combat patches");
                if (!TryDisableAllKnownOptionalPatchesAndVerify(out string cleanupFailure))
                {
                    Logger.Error(
                        "[WorkshopCombat] Optional-patch cleanup could not be verified: {Failure}",
                        cleanupFailure);
                    throw new InvalidOperationException(
                        "Optional combat patches remain after failed guard initialization; aborting Coop startup",
                        exception);
                }
            }
        }
    }

    internal static bool IsInitialized => initialized;

    internal static MethodBase ResolveOptionalMethod(string typeName, string methodName)
    {
        try
        {
            Type type = AccessTools.TypeByName(typeName);
            return type == null ? null : AccessTools.Method(type, methodName);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] Optional target {Type}.{Method} was unavailable; integration remains disabled",
                typeName,
                methodName);
            return null;
        }
    }

    internal static Assembly ResolveOptionalAssembly(string assemblyName)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] Optional assembly {Assembly} was unavailable",
                assemblyName);
            return null;
        }
    }

    /// <summary>
    /// Finds lifecycle overrides on every submodule type in the named optional assembly.  Scanning
    /// by base type, rather than trusting RBM.SubModule, lets an incompatible renamed implementation
    /// be found and denied by the prefix as well.
    /// </summary>
    internal static IEnumerable<MethodBase> ResolveOptionalSubModuleLifecycleMethods(
        string assemblyName,
        string methodName)
    {
        Assembly assembly = ResolveOptionalAssembly(assemblyName);
        foreach (Type type in GetLoadableTypes(assembly))
        {
            if (!typeof(MBSubModuleBase).IsAssignableFrom(type)) continue;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            }
            catch (Exception exception)
            {
                Logger.Error(
                    exception,
                    "[WorkshopCombat] Could not inspect optional lifecycle methods on {Type}; the integration remains untrusted",
                    type.FullName);
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                if (string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    yield return method;
            }
        }
    }

    /// <summary>
    /// Resolves every same-named declaration so an added overload is patched and denied by the
    /// shape gate instead of becoming an unguarded configuration mutation path.
    /// </summary>
    internal static IEnumerable<MethodBase> ResolveOptionalDeclaredMethods(
        string assemblyName,
        string typeName,
        string methodName)
    {
        Assembly assembly = ResolveOptionalAssembly(assemblyName);
        Type type;
        try
        {
            type = assembly?.GetType(typeName, throwOnError: false, ignoreCase: false);
        }
        catch (Exception exception)
        {
            Logger.Error(
                exception,
                "[WorkshopCombat] Could not resolve optional type {Type}; its entry point remains denied",
                typeName);
            yield break;
        }

        if (type == null) yield break;

        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
        }
        catch (Exception exception)
        {
            Logger.Error(
                exception,
                "[WorkshopCombat] Could not inspect optional entry points on {Type}; its entry point remains denied",
                typeName);
            yield break;
        }

        foreach (MethodInfo method in methods)
        {
            if (string.Equals(method.Name, methodName, StringComparison.Ordinal))
                yield return method;
        }
    }

    internal static bool IsExpectedRbmLifecycleMethodShape(
        MethodBase method,
        RbmLifecycleEntryPoint entryPoint)
    {
        try
        {
            if (!(method is MethodInfo methodInfo)
                || methodInfo.IsStatic
                || !methodInfo.IsVirtual
                || methodInfo.ReturnType != typeof(void)
                || methodInfo.DeclaringType == null
                || !typeof(MBSubModuleBase).IsAssignableFrom(methodInfo.DeclaringType)
                || methodInfo.GetBaseDefinition().DeclaringType != typeof(MBSubModuleBase))
            {
                return false;
            }

            Type expectedParameter;
            string expectedName;
            if (entryPoint == RbmLifecycleEntryPoint.MissionBehaviorInitialize)
            {
                expectedName = "OnMissionBehaviorInitialize";
                expectedParameter = typeof(Mission);
            }
            else
            {
                expectedName = "OnGameInitializationFinished";
                expectedParameter = typeof(Game);
            }

            ParameterInfo[] parameters = methodInfo.GetParameters();
            return string.Equals(methodInfo.Name, expectedName, StringComparison.Ordinal)
                && parameters.Length == 1
                && parameters[0].ParameterType == expectedParameter;
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] RBM lifecycle method shape could not be verified; it will be denied");
            return false;
        }
    }

    internal static bool IsExpectedRbmPatchWaveMethodShape(MethodBase method)
    {
        try
        {
            return method is MethodInfo methodInfo
                && methodInfo.IsPublic
                && methodInfo.IsStatic
                && methodInfo.ReturnType == typeof(void)
                && methodInfo.GetParameters().Length == 0
                && string.Equals(methodInfo.Name, "ApplyHarmonyPatches", StringComparison.Ordinal)
                && string.Equals(methodInfo.DeclaringType?.FullName, "RBM.SubModule", StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] RBM patch-wave method shape could not be verified; it will be denied");
            return false;
        }
    }

    internal static bool IsExpectedRbmConfigEntryPointShape(
        MethodBase method,
        RbmConfigEntryPoint entryPoint)
    {
        try
        {
            if (!(method is MethodInfo methodInfo)
                || methodInfo.ReturnType != typeof(void)
                || methodInfo.GetParameters().Length != 0
                || methodInfo.DeclaringType == null)
            {
                return false;
            }

            string expectedType;
            string expectedName;
            bool expectedStatic;
            bool expectedPublic;
            switch (entryPoint)
            {
                case RbmConfigEntryPoint.LoadConfig:
                    expectedType = "RBMConfig.RBMConfig";
                    expectedName = "LoadConfig";
                    expectedStatic = true;
                    expectedPublic = true;
                    break;
                case RbmConfigEntryPoint.SaveXmlConfig:
                    expectedType = "RBMConfig.RBMConfig";
                    expectedName = "saveXmlConfig";
                    expectedStatic = true;
                    expectedPublic = true;
                    break;
                case RbmConfigEntryPoint.ExecuteDone:
                    expectedType = "RBMConfig.RBMConfigViewModel";
                    expectedName = "ExecuteDone";
                    expectedStatic = false;
                    expectedPublic = false;
                    break;
                default:
                    return false;
            }

            return methodInfo.IsStatic == expectedStatic
                && methodInfo.IsPublic == expectedPublic
                && string.Equals(methodInfo.DeclaringType.FullName, expectedType, StringComparison.Ordinal)
                && string.Equals(methodInfo.Name, expectedName, StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] RBM configuration entry-point shape could not be verified; it will be denied");
            return false;
        }
    }

    internal static bool AllowRbmConfigEntryPoint(
        MethodBase method,
        RbmConfigEntryPoint entryPoint,
        bool guardInitialized,
        bool moduleCompatible)
    {
        return guardInitialized
            && moduleCompatible
            && IsExpectedRbmConfigEntryPointShape(method, entryPoint);
    }

    internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        if (assembly == null) return Enumerable.Empty<Type>();
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null);
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] Could not inspect optional assembly {Assembly}",
                assembly.GetName().Name);
            return Enumerable.Empty<Type>();
        }
    }

    internal static bool IsFamilyCompatible(CombatModFamily family)
    {
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            CombatModFingerprint[] expected = CombatModFingerprintCatalog.Entries
                .Where(entry => entry.Family == family)
                .ToArray();

            foreach (CombatModFingerprint fingerprint in expected)
            {
                Assembly assembly = assemblies.FirstOrDefault(candidate => string.Equals(
                    candidate.GetName().Name,
                    fingerprint.AssemblyName,
                    StringComparison.Ordinal));
                if (assembly == null || !IsAssemblyCompatible(assembly))
                    return false;
            }

            return expected.Length > 0;
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "[WorkshopCombat] Optional family {Family} did not pass validation", family);
            return false;
        }
    }

    internal static bool IsAssemblyCompatible(Assembly assembly)
    {
        if (assembly == null) return false;

        try
        {
            AssemblyName assemblyName = assembly.GetName();
            CombatModFingerprint expected = CombatModFingerprintCatalog.Find(assemblyName.Name);
            if (expected == null || string.IsNullOrWhiteSpace(assembly.Location)) return false;

            var file = new FileInfo(assembly.Location);
            string cacheKey = string.Concat(
                assembly.FullName,
                "|",
                file.FullName,
                "|",
                file.Length,
                "|",
                file.LastWriteTimeUtc.Ticks);
            return FingerprintResults.GetOrAdd(cacheKey, _ =>
            {
                string hash = ComputeSha256(file.FullName);
                return CombatModFingerprintCatalog.IsAccepted(
                    assemblyName.Name,
                    assemblyName.Version,
                    hash);
            });
        }
        catch (Exception exception)
        {
            Logger.Warning(
                exception,
                "[WorkshopCombat] Could not fingerprint optional assembly {Assembly}; it will be disabled",
                assembly.GetName().Name);
            return false;
        }
    }

    internal static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            return string.Concat(hash.Select(value => value.ToString("X2")));
        }
    }

    internal static CombatPatchDisposition ClassifyPatch(
        string owner,
        string declaringTypeName,
        string methodName,
        bool isServer,
        string patchAssemblyName = null)
    {
        if (string.Equals(owner, "mod.bannerlord.unblockablethrust", StringComparison.Ordinal))
            return CombatPatchDisposition.Remove; // replaced by the authority-aware postfix in this assembly

        if (string.Equals(owner, "com.rbmt", StringComparison.Ordinal))
            return isServer ? CombatPatchDisposition.Keep : CombatPatchDisposition.ServerOnly;

        if (IsCriticalCoopCollisionTarget(declaringTypeName, methodName))
            return CombatPatchDisposition.Remove;

        // RBMAI mutates formation orders, shield state, tactic weights and Agent.Formation across
        // a broad patch surface.  Client execution is default-deny; only the campaign server may
        // retain an audited RBMAI patch.  Assembly identity closes any owner-id aliasing gap.
        if (string.Equals(owner, "com.rbmai", StringComparison.Ordinal)
            || string.Equals(patchAssemblyName, "RBMAI", StringComparison.Ordinal))
        {
            return isServer ? CombatPatchDisposition.Keep : CombatPatchDisposition.ServerOnly;
        }

        if (string.Equals(declaringTypeName, "TaleWorlds.MountAndBlade.Mission", StringComparison.Ordinal)
            && (string.Equals(methodName, "OnAgentShootMissile", StringComparison.Ordinal)
                || string.Equals(methodName, "CreateMeleeBlow", StringComparison.Ordinal)
                || string.Equals(methodName, "OnAgentHit", StringComparison.Ordinal)))
        {
            return CombatPatchDisposition.LocalMissionAuthorityOnly;
        }

        if (string.Equals(declaringTypeName, "TaleWorlds.MountAndBlade.HumanAIComponent", StringComparison.Ordinal)
            || (declaringTypeName != null
                && declaringTypeName.EndsWith("AgentStatCalculateModel", StringComparison.Ordinal)
                && (methodName == "SetAiRelatedProperties" || methodName == "SetWeaponSkillEffectsOnAgent"))
            || (string.Equals(declaringTypeName, "TaleWorlds.MountAndBlade.Agent", StringComparison.Ordinal)
                && methodName == "SetFiringOrder"))
        {
            return CombatPatchDisposition.LocalMissionAuthorityOnly;
        }

        if (!isServer && IsCampaignMutationTarget(declaringTypeName))
            return CombatPatchDisposition.ServerOnly;

        return CombatPatchDisposition.Keep;
    }

    internal static bool IsCriticalCoopCollisionTarget(string declaringTypeName, string methodName)
    {
        if (string.Equals(declaringTypeName, "TaleWorlds.MountAndBlade.Mission", StringComparison.Ordinal))
        {
            return methodName == "RegisterBlow"
                || methodName == "MeleeHitCallback"
                || methodName == "MissileHitCallback"
                || methodName == "ChargeDamageCallback"
                || methodName == "HandleMissileCollisionReaction";
        }

        if (string.Equals(declaringTypeName, "TaleWorlds.MountAndBlade.Agent", StringComparison.Ordinal))
            return methodName == "RegisterBlow" || methodName == "HandleBlow" || methodName == "OnShieldDamaged";

        return (declaringTypeName == "SandBox.Missions.MissionLogics.BattleAgentLogic"
                || declaringTypeName == "TaleWorlds.MountAndBlade.CustomBattle.CustomBattleAgentLogic")
            && methodName == "OnAgentHit";
    }

    internal static bool IsCampaignMutationTarget(string declaringTypeName)
    {
        if (string.IsNullOrEmpty(declaringTypeName)) return false;
        return declaringTypeName.StartsWith("TaleWorlds.CampaignSystem.", StringComparison.Ordinal)
            || declaringTypeName.StartsWith("StoryMode.", StringComparison.Ordinal);
    }

    internal static bool BeforeRbmPatchApply(MethodBase method)
    {
        lock (Gate)
        {
            bool compatible = IsFamilyCompatible(CombatModFamily.Rbm434);
            bool wasInitialized = initialized;
            string normalizationFailure = null;
            bool expectedShape = IsExpectedRbmPatchWaveMethodShape(method);
            bool normalized = compatible
                && wasInitialized
                && expectedShape
                && TryNormalizeRbmGameplayConfiguration(out normalizationFailure);
            if (normalized) return true;

            if (!expectedShape)
                normalizationFailure = "RBM ApplyHarmonyPatches method shape mismatch";
            else if (!wasInitialized)
                normalizationFailure = "guard was not initialized";
            else if (!compatible)
                normalizationFailure = "RBM 4.3.4 family fingerprint mismatch";

            initialized = false;
            if (!TryDisableFamilyAndVerify(
                    CombatModFamily.Rbm434,
                    RbmOwners,
                    out string cleanupFailure))
            {
                throw new InvalidOperationException(
                    "RBM patch wave was denied, but existing RBM patches could not be purged: "
                    + cleanupFailure);
            }

            Logger.Error(
                "[WorkshopCombat] RBM patch wave denied and purged. compatible={Compatible}, previouslyInitialized={Initialized}, normalizationFailure={Failure}",
                compatible,
                wasInitialized,
                normalizationFailure);
            return false;
        }
    }

    internal static void AfterRbmPatchApply()
    {
        Initialize();
    }

    internal static bool AllowOptionalPatchMethod(MethodBase patchMethod, object[] arguments)
    {
        if (!initialized
            || patchMethod?.DeclaringType?.Assembly == null
            || !IsAssemblyCompatible(patchMethod.DeclaringType.Assembly))
        {
            return false;
        }

        if (!GameInterface.Services.MapEvents.BattleSpawnGate.IsCoopBattleActive)
            return true;

        Agent source = FindAuthorityAgent(patchMethod, arguments);
        bool local = source != null && source.IsLocallyControlled();
        return CombatModAuthorityPolicy.AllowMissionGameplayDecision(
            isCoopBattleActive: true,
            sourceIsLocallyControlled: local);
    }

    private static Agent FindAuthorityAgent(MethodBase patchMethod, object[] arguments)
    {
        if (patchMethod == null || arguments == null) return null;

        int index = FindAuthorityAgentParameterIndex(patchMethod);
        return index >= 0 && index < arguments.Length ? arguments[index] as Agent : null;
    }

    /// <summary>
    /// A workshop Harmony patch's <c>__instance</c> is an ordinary parameter on the static patch
    /// method being guarded, so it is present in that patch method's own parameter list/argument array.
    /// This helper intentionally inspects the optional patch method, not our guard prefix.
    /// </summary>
    internal static int FindAuthorityAgentParameterIndex(MethodBase patchMethod)
    {
        if (patchMethod == null) return -1;

        ParameterInfo[] parameters = patchMethod.GetParameters();
        string[] preferredNames =
        {
            "attacker", "attackerAgent", "affectorAgent", "shooterAgent", "agent", "___Agent", "__instance"
        };

        foreach (string preferredName in preferredNames)
        {
            for (int index = 0; index < parameters.Length; index++)
            {
                if (string.Equals(parameters[index].Name, preferredName, StringComparison.Ordinal)
                    && typeof(Agent).IsAssignableFrom(parameters[index].ParameterType.IsByRef
                        ? parameters[index].ParameterType.GetElementType()
                        : parameters[index].ParameterType))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Sweeps and classifies every known-optional patch, removing or guarding it as
    /// <see cref="ClassifyPatch"/> directs. Retried as a whole via HarmonyPatchInfoStabilizer (see
    /// that type's remarks): HarmonyLib hands back the PatchMethod to unpatch from GetPatchInfo, and
    /// that value has been observed to transiently deserialize to the wrong MethodInfo. Unpatching the
    /// wrong (unrelated) method leaves the real target patched, so only re-scanning and re-attempting
    /// the removal — not a plain single pass — recovers from it.
    /// </summary>
    private static void SanitizeLoadedPatches()
    {
        HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(() =>
        {
            RunSanitizationPass();
            return !HasPendingRemovablePatches();
        });
    }

    private static void RunSanitizationPass()
    {
        foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
        {
            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;

            IEnumerable<Patch> patches = patchInfo.Prefixes
                .Concat(patchInfo.Postfixes)
                .Concat(patchInfo.Transpilers)
                .Concat(patchInfo.Finalizers);

            foreach (Patch patch in patches.ToArray())
            {
                if (!IsKnownOptionalPatch(patch)) continue;

                Assembly patchAssembly = patch.PatchMethod?.DeclaringType?.Assembly;
                if (patchAssembly == null || !IsAssemblyCompatible(patchAssembly))
                {
                    GuardHarmony.Unpatch(original, patch.PatchMethod);
                    Logger.Error(
                        "[WorkshopCombat] Disabled incompatible patch {Patch} on {Target}",
                        patch.PatchMethod,
                        original);
                    continue;
                }

                CombatPatchDisposition disposition = ClassifyPatch(
                    patch.owner,
                    original.DeclaringType?.FullName,
                    original.Name,
                    ModInformation.IsServer,
                    patchAssembly?.GetName().Name);

                if (disposition == CombatPatchDisposition.Remove
                    || disposition == CombatPatchDisposition.ServerOnly)
                {
                    GuardHarmony.Unpatch(original, patch.PatchMethod);
                    Logger.Information(
                        "[WorkshopCombat] Removed {Owner} patch {Patch} from Coop-owned target {Target}",
                        patch.owner,
                        patch.PatchMethod,
                        original);
                }
                else if (disposition == CombatPatchDisposition.LocalMissionAuthorityOnly)
                {
                    if (!InstallAuthorityGuard(patch.PatchMethod))
                    {
                        GuardHarmony.Unpatch(original, patch.PatchMethod);
                        Logger.Warning(
                            "[WorkshopCombat] Removed optional patch with unsupported return signature {Patch}",
                            patch.PatchMethod);
                    }
                }
            }
        }
    }

    /// <summary>True if a known-optional patch that should have been removed is still present.</summary>
    private static bool HasPendingRemovablePatches()
    {
        foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
        {
            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;

            IEnumerable<Patch> patches = patchInfo.Prefixes
                .Concat(patchInfo.Postfixes)
                .Concat(patchInfo.Transpilers)
                .Concat(patchInfo.Finalizers);

            foreach (Patch patch in patches)
            {
                if (!IsKnownOptionalPatch(patch)) continue;

                Assembly patchAssembly = patch.PatchMethod?.DeclaringType?.Assembly;
                if (patchAssembly == null || !IsAssemblyCompatible(patchAssembly)) return true;

                CombatPatchDisposition disposition = ClassifyPatch(
                    patch.owner,
                    original.DeclaringType?.FullName,
                    original.Name,
                    ModInformation.IsServer,
                    patchAssembly?.GetName().Name);

                if (disposition == CombatPatchDisposition.Remove
                    || disposition == CombatPatchDisposition.ServerOnly)
                    return true;
            }
        }

        return false;
    }

    private static bool InstallAuthorityGuard(MethodInfo patchMethod)
    {
        if (patchMethod == null) return false;
        if (AuthorityGuardedPatchMethods.Contains(patchMethod)) return true;

        string prefixName;
        if (patchMethod.ReturnType == typeof(void))
            prefixName = nameof(OptionalVoidPatchAuthorityPrefix);
        else if (patchMethod.ReturnType == typeof(bool))
            prefixName = nameof(OptionalBoolPatchAuthorityPrefix);
        else
            return false;

        MethodInfo prefix = AccessTools.Method(
            typeof(CombatModCompatibilityGuard),
            prefixName);
        GuardHarmony.Patch(patchMethod, prefix: new HarmonyMethod(prefix)
        {
            priority = Priority.First
        });
        AuthorityGuardedPatchMethods.Add(patchMethod);
        return true;
    }

    private static bool OptionalVoidPatchAuthorityPrefix(
        MethodBase __originalMethod,
        object[] __args)
    {
        return AllowOptionalPatchMethod(__originalMethod, __args);
    }

    private static bool OptionalBoolPatchAuthorityPrefix(
        MethodBase __originalMethod,
        object[] __args,
        ref bool __result)
    {
        bool allowed = AllowOptionalPatchMethod(__originalMethod, __args);
        if (!allowed)
        {
            // A skipped bool-returning Harmony prefix must report "continue original".  Leaving
            // default(false) would accidentally suppress the Bannerlord target on non-authority peers.
            __result = true;
        }

        return allowed;
    }

    private static bool IsKnownOptionalPatch(Patch patch)
    {
        string assemblyName = patch?.PatchMethod?.DeclaringType?.Assembly?.GetName().Name;
        return patch != null
            && (RbmOwners.Contains(patch.owner)
                || string.Equals(patch.owner, "mod.bannerlord.unblockablethrust", StringComparison.Ordinal)
                || CombatModFingerprintCatalog.Find(assemblyName) != null);
    }

    private static bool TryDisableAllKnownOptionalPatchesAndVerify(out string failure)
    {
        return TryDisableFamilyAndVerify(
            family: null,
            RbmOwners.Concat(new[] { "mod.bannerlord.unblockablethrust" }),
            out failure);
    }

    private static bool TryDisableFamilyAndVerify(
        CombatModFamily? family,
        IEnumerable<string> owners,
        out string failure)
    {
        string[] ownerArray = owners.Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            DisableFamily(family, ownerArray);
        }
        catch (Exception exception)
        {
            failure = "Harmony cleanup threw: " + exception.Message;
            return false;
        }

        try
        {
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
            {
                HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(original);
                if (patchInfo == null) continue;

                IEnumerable<Patch> patches = patchInfo.Prefixes
                    .Concat(patchInfo.Postfixes)
                    .Concat(patchInfo.Transpilers)
                    .Concat(patchInfo.Finalizers);
                Patch remaining = patches.FirstOrDefault(candidate =>
                    MatchesOptionalFamilyPatch(candidate, family, ownerArray));
                if (remaining != null)
                {
                    failure = string.Concat(
                        "residual owner=",
                        remaining.owner,
                        " patch=",
                        remaining.PatchMethod,
                        " target=",
                        original);
                    return false;
                }
            }

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = "Harmony cleanup verification threw: " + exception.Message;
            return false;
        }
    }

    private static void DisableFamily(
        CombatModFamily? family,
        IEnumerable<string> owners)
    {
        var ownerSet = new HashSet<string>(owners, StringComparer.Ordinal);
        foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToArray())
        {
            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null) continue;

            IEnumerable<Patch> patches = patchInfo.Prefixes
                .Concat(patchInfo.Postfixes)
                .Concat(patchInfo.Transpilers)
                .Concat(patchInfo.Finalizers);
            foreach (Patch patch in patches.Where(candidate =>
                         MatchesOptionalFamilyPatch(candidate, family, ownerSet))
                     .ToArray())
                GuardHarmony.Unpatch(original, patch.PatchMethod);
        }
    }

    private static bool MatchesOptionalFamilyPatch(
        Patch patch,
        CombatModFamily? family,
        IEnumerable<string> owners)
    {
        if (patch == null) return false;
        if (owners.Contains(patch.owner, StringComparer.Ordinal)) return true;

        CombatModFingerprint fingerprint = CombatModFingerprintCatalog.Find(
            patch.PatchMethod?.DeclaringType?.Assembly?.GetName().Name);
        return fingerprint != null
            && (!family.HasValue || fingerprint.Family == family.Value);
    }

    /// <summary>
    /// RBM reads a per-user XML file.  Damage/posture options cannot be allowed to differ between
    /// three collision-authority peers, so Coop pins the audited 4.3.4 gameplay values after every
    /// RBM LoadConfig/patch wave.  Presentation-only UI switches are intentionally left local.
    /// </summary>
    internal static bool ReassertCanonicalRbmGameplayConfiguration()
    {
        lock (Gate)
        {
            string failure = initialized ? null : "guard was not initialized";
            if (initialized && TryNormalizeRbmGameplayConfiguration(out failure))
                return true;

            initialized = false;
            if (!TryDisableFamilyAndVerify(
                    CombatModFamily.Rbm434,
                    RbmOwners,
                    out string cleanupFailure))
            {
                throw new InvalidOperationException(
                    "RBM configuration drift could not be normalized and RBM cleanup failed: "
                    + cleanupFailure);
            }

            Logger.Error(
                "[WorkshopCombat] RBM configuration reassertion failed; RBM was disabled. failure={Failure}",
                failure);
            return false;
        }
    }

    internal static bool TryNormalizeRbmGameplayConfiguration(out string failure)
    {
        failure = null;
        try
        {
            if (!IsFamilyCompatible(CombatModFamily.Rbm434))
            {
                failure = "RBM 4.3.4 family fingerprint mismatch";
                return false;
            }

            Type config = AccessTools.TypeByName("RBMConfig.RBMConfig");
            Type utilities = AccessTools.TypeByName("RBMConfig.Utilities");
            if (config?.FullName != "RBMConfig.RBMConfig"
                || utilities?.FullName != "RBMConfig.Utilities"
                || !IsAssemblyCompatible(config.Assembly)
                || !IsAssemblyCompatible(utilities.Assembly))
            {
                failure = "RBMConfig types did not resolve to the audited assembly";
                return false;
            }

            return TryApplyCanonicalRbmScalarValues(config, out failure)
                && TryApplyCanonicalPriceMultipliers(config, out failure)
                && TryApplyCanonicalWeaponFactors(config, utilities, out failure);
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ": " + exception.Message;
            Logger.Error(
                exception,
                "[WorkshopCombat] Failed to apply canonical RBM gameplay configuration");
            return false;
        }
    }

    internal static bool TryApplyCanonicalRbmScalarValues(Type config, out string failure)
    {
        if (config == null)
        {
            failure = "RBMConfig type was null";
            return false;
        }

        foreach (RbmCanonicalConfigValue expected in CanonicalRbmGameplayValues)
        {
            FieldInfo field = config.GetField(
                expected.Name,
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly);
            if (field == null
                || !field.IsStatic
                || field.IsInitOnly
                || field.FieldType != expected.FieldType)
            {
                failure = string.Concat(
                    "RBMConfig field shape mismatch: ",
                    expected.Name,
                    " expected ",
                    expected.FieldType.FullName);
                return false;
            }

            field.SetValue(null, expected.Value);
            object actual = field.GetValue(null);
            if (!Equals(actual, expected.Value))
            {
                failure = string.Concat(
                    "RBMConfig field verification failed: ",
                    expected.Name,
                    " expected=",
                    expected.Value,
                    " actual=",
                    actual);
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static bool TryApplyCanonicalPriceMultipliers(Type config, out string failure)
    {
        FieldInfo field = config.GetField(
            "priceMultipliers",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        object multipliers = field?.GetValue(null);
        if (field == null
            || field.IsInitOnly
            || field.FieldType.FullName != "RBMConfig.RBMCombatConfigPriceMultipliers"
            || multipliers == null)
        {
            failure = "RBMConfig priceMultipliers field shape mismatch";
            return false;
        }

        var values = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["ArmorPriceModifier"] = 1f,
            ["WeaponPriceModifier"] = 1f,
            ["HorsePriceModifier"] = 0.2f,
            ["TradePriceModifier"] = 1f
        };
        foreach (KeyValuePair<string, float> expected in values)
        {
            FieldInfo multiplier = field.FieldType.GetField(
                expected.Key,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (multiplier == null || multiplier.IsInitOnly || multiplier.FieldType != typeof(float))
            {
                failure = "RBM price-multiplier field shape mismatch: " + expected.Key;
                return false;
            }

            multiplier.SetValue(multipliers, expected.Value);
            if (!Equals(multiplier.GetValue(multipliers), expected.Value))
            {
                failure = "RBM price-multiplier verification failed: " + expected.Key;
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static bool TryApplyCanonicalWeaponFactors(
        Type config,
        Type utilities,
        out string failure)
    {
        FieldInfo factorsField = config.GetField(
            "weaponTypesFactors",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (factorsField == null
            || factorsField.IsInitOnly
            || !factorsField.FieldType.IsGenericType
            || factorsField.FieldType.GetGenericTypeDefinition() != typeof(List<>))
        {
            failure = "RBMConfig weaponTypesFactors field shape mismatch";
            return false;
        }

        Type factorType = factorsField.FieldType.GetGenericArguments()[0];
        if (factorType.FullName != "RBMConfig.RBMCombatConfigWeaponType"
            || !(factorsField.GetValue(null) is IList factors))
        {
            failure = "RBM weapon-factor element type/value mismatch";
            return false;
        }

        MethodInfo[] candidates = utilities.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "createWeaponTypesFactors")
            .ToArray();
        MethodInfo createDefaults = candidates.SingleOrDefault(method =>
        {
            ParameterInfo[] parameters = method.GetParameters();
            return method.ReturnType == typeof(void)
                && parameters.Length == 1
                && parameters[0].ParameterType == factorsField.FieldType.MakeByRefType();
        });
        if (createDefaults == null)
        {
            failure = "RBM Utilities.createWeaponTypesFactors method shape mismatch";
            return false;
        }

        factors.Clear();
        object[] arguments = { factors };
        createDefaults.Invoke(null, arguments);
        if (!(arguments[0] is IList canonicalFactors))
        {
            failure = "RBM weapon-factor defaults returned no list";
            return false;
        }

        if (!ReferenceEquals(canonicalFactors, factors))
            factorsField.SetValue(null, canonicalFactors);
        if (!ReferenceEquals(factorsField.GetValue(null), canonicalFactors)
            || canonicalFactors.Count != CanonicalRbmWeaponFactors.Count)
        {
            failure = "RBM weapon-factor list verification failed";
            return false;
        }

        for (int index = 0; index < CanonicalRbmWeaponFactors.Count; index++)
        {
            object actual = canonicalFactors[index];
            RbmCanonicalWeaponFactor expected = CanonicalRbmWeaponFactors[index];
            if (actual == null
                || !VerifyInstanceField(actual, factorType, "weaponType", typeof(string), expected.WeaponType)
                || !VerifyInstanceField(actual, factorType, "ExtraBluntFactorCut", typeof(float), expected.ExtraBluntFactorCut)
                || !VerifyInstanceField(actual, factorType, "ExtraBluntFactorPierce", typeof(float), expected.ExtraBluntFactorPierce)
                || !VerifyInstanceField(actual, factorType, "ExtraBluntFactorBlunt", typeof(float), expected.ExtraBluntFactorBlunt)
                || !VerifyInstanceField(actual, factorType, "ExtraArmorThresholdFactorPierce", typeof(float), expected.ExtraArmorThresholdFactorPierce)
                || !VerifyInstanceField(actual, factorType, "ExtraArmorThresholdFactorCut", typeof(float), expected.ExtraArmorThresholdFactorCut)
                || !VerifyInstanceField(actual, factorType, "ExtraArmorSkillDamageAbsorb", typeof(float), expected.ExtraArmorSkillDamageAbsorb))
            {
                failure = "RBM weapon-factor verification failed at index " + index;
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static bool VerifyInstanceField(
        object instance,
        Type declaringType,
        string fieldName,
        Type expectedType,
        object expectedValue)
    {
        FieldInfo field = declaringType.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        return field != null
            && field.FieldType == expectedType
            && Equals(field.GetValue(instance), expectedValue);
    }
}
