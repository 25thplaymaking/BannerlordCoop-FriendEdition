using HarmonyLib;
using Missions.WorkshopMods.Combat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace E2E.Tests.Services.WorkshopMods.Core;

/// <summary>
/// AbsentModuleSafetyTests covers a Workshop mod that is not installed at all: TargetMethods()
/// resolves to nothing AND the category is never registered (MissionModule/GameInterfaceModule gate
/// registration on presence). This file covers the other half of the binding constraint — "absent,
/// mismatched, or disabled": a same-named-but-internally-different build, where the presence gate is
/// fooled (it only checks assembly NAMES, e.g. CombatModFingerprintCatalog.IsFamilyPresent), the
/// category DOES get registered and applied, and an individual patch class's own TargetMethods()
/// still resolves to nothing because the specific type/method it looks for isn't there. That is
/// exactly the HarmonyException shape this whole task exists to remove, and it is the scenario the
/// [HarmonyPrepare] guards added to every RBM/DismembermentPlus adapter exist to catch as a second,
/// independent line of defense underneath the presence gate.
///
/// A fully faithful reproduction would need a real assembly loaded into the process literally named
/// "RBM"/"RBMAI"/"RBMCombat"/"RBMConfig"/"RBMTournament" (CombatModFingerprintCatalog.IsFamilyPresent
/// and CombatModCompatibilityGuard's ResolveOptionalAssembly/ResolveOptionalDeclaredMethods/etc. all
/// resolve by exact AppDomain-wide assembly name, not an injectable list), containing types that
/// deliberately don't match what the adapters look for. That is not practical to do safely in this
/// harness: a dynamically emitted assembly registers into the real, shared
/// AppDomain.CurrentDomain.GetAssemblies() the instant it is created, and .NET gives no deterministic
/// way to unload it again within a test's lifetime (no rollback like Harmony.Unpatch) — it would
/// permanently change what every other test in the same xunit process, including
/// AbsentModuleSafetyTests and every E2E environment constructed afterward, sees as "installed" for
/// the rest of the run. The existing suite already deliberately avoids this:
/// CombatModCompatibilityTests.CreateVoidEntryPoint names its synthetic assemblies
/// "SyntheticRbmConfigShape_*" rather than the real "RBM"/"RBMConfig", for the same reason.
///
/// Instead, this proves the general mechanism directly and safely with real Harmony, against
/// synthetic types/categories private to this test that are never registered anywhere else, then
/// confirms by reflection that every actual RBM/DismembermentPlus adapter patch class carries the
/// same guard.
/// </summary>
public sealed class MismatchedModuleSafetyTests
{
    private const string UnguardedCategory = "E2E.Tests.Synthetic.UnguardedEmptyTargetMethods";
    private const string GuardedCategory = "E2E.Tests.Synthetic.GuardedEmptyTargetMethods";

    [Fact]
    public void PatchCategory_WithUnguardedEmptyTargetMethods_ThrowsUndefinedTargetMethod()
    {
        // Reproduces the exact defect: a category is registered/applied (as it would be for a
        // mismatched build that fools the presence gate) but the individual patch class's own
        // TargetMethods() resolves to nothing and it has no HarmonyPrepare guard.
        var harmony = new Harmony("E2E.Tests.MismatchedModuleSafetyTests.Unguarded." + Guid.NewGuid());

        var exception = Record.Exception(
            () => harmony.PatchCategory(typeof(UnguardedEmptyTargetPatch).Assembly, UnguardedCategory));

        Assert.NotNull(exception);
        Assert.Contains("Undefined target method", exception.ToString());
    }

    [Fact]
    public void PatchCategory_WithHarmonyPrepareGuardedEmptyTargetMethods_DoesNotThrow()
    {
        // The fix: identical shape to the test above (empty TargetMethods(), category applied), but
        // with the same [HarmonyPrepare] pattern this task added to every RBM/DismembermentPlus
        // adapter. Harmony must skip the class instead of throwing.
        var harmony = new Harmony("E2E.Tests.MismatchedModuleSafetyTests.Guarded." + Guid.NewGuid());

        var exception = Record.Exception(
            () => harmony.PatchCategory(typeof(GuardedEmptyTargetPatch).Assembly, GuardedCategory));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(typeof(RbmPatchWaveCompatibilityPatch))]
    [InlineData(typeof(RbmConfigLoadPatch))]
    [InlineData(typeof(RbmConfigSavePatch))]
    [InlineData(typeof(RbmConfigUiDonePatch))]
    [InlineData(typeof(RbmMissionBehaviorInitializationPatch))]
    [InlineData(typeof(RbmGameInitializationFinishedPatch))]
    [InlineData(typeof(DismembermentMissionInitializerPatch))]
    [InlineData(typeof(DismembermentRegisterBlowPatch))]
    [InlineData(typeof(DismembermentSlowMotionPatch))]
    public void OptionalCombatAdapterPatch_HasHarmonyPrepareGuard(Type patchType)
    {
        // Ties the mechanism proved above back to the real shipped classes: every RBM/
        // DismembermentPlus adapter must carry the guard, so a mismatched build that fools
        // MissionModule's IsFamilyPresent gate into registering the category still cannot make this
        // specific class abort Coop's patching.
        bool hasPrepare = patchType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Any(method => method.GetCustomAttributes(typeof(HarmonyPrepare), inherit: false).Length > 0);

        Assert.True(hasPrepare, $"{patchType.FullName} is missing a [HarmonyPrepare] guard.");
    }

    [HarmonyPatch]
    [HarmonyPatchCategory(UnguardedCategory)]
    private static class UnguardedEmptyTargetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Enumerable.Empty<MethodBase>();

        [HarmonyPrefix]
        private static bool Prefix() => true;
    }

    [HarmonyPatch]
    [HarmonyPatchCategory(GuardedCategory)]
    private static class GuardedEmptyTargetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Enumerable.Empty<MethodBase>();

        [HarmonyPrepare]
        private static bool Prepare() => TargetMethods().Any();

        [HarmonyPrefix]
        private static bool Prefix() => true;
    }
}
