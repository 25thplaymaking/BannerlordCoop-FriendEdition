using GameInterface.Services.WorkshopMods.Frameworks;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Frameworks;

public sealed class FrameworkCompatibilityTests
{
    [Fact]
    public void Manifest_PinsCanonicalHarmonyAndExactV147FrameworkExecutables()
    {
        Assert.Collection(
            FrameworkCompatibilityManifest.Assemblies,
            item => AssertAssembly(item, "0Harmony", "2.4.2.0", optional: false),
            item => AssertAssembly(item, "Bannerlord.Harmony", "2.4.2.248", optional: false),
            item => AssertAssembly(item, "Bannerlord.ButterLib", "2.11.1.0", optional: true),
            item => AssertAssembly(item, "Bannerlord.UIExtenderEx", "2.13.3.0", optional: true),
            item => AssertAssembly(item, "MCMv5", "5.12.2.0", optional: true),
            item => AssertAssembly(item, "Bannerlord.MBOptionScreen", "1.0.1.50", optional: true),
            item => AssertAssembly(item, "Bannerlord.ButterLib.Implementation.1.4.7", "2.11.1.0", optional: true),
            item => AssertAssembly(item, "MCM.UI.Adapter.MCMv5", "5.12.2.0", optional: true),
            item => AssertAssembly(item, "Bannerlord.MBOptionScreen.v1.4.7", "5.12.2.0", optional: true));

        Assert.Equal(64, FrameworkCompatibilityManifest.Assemblies[0].Sha256.Length);
        Assert.Equal(
            "2edda13a18954b79795bac0d7e0e8fdf0bfa05b96552d6056d90f446cd0a2ab2",
            FrameworkCompatibilityManifest.Assemblies[0].Sha256);
    }

    [Fact]
    public void Activation_AdmitsFullyStagedOrCompleteCohortAndRejectsPartialCohort()
    {
        string[] required = { "Butter", "UIExtender", "MCM" };
        Assert.Equal(
            FrameworkActivationState.StagedInactive,
            FrameworkCompatibilityBootstrap.DetermineActivationState(
                new[] { "0Harmony", "Coop" }, required));
        Assert.Equal(
            FrameworkActivationState.ActiveExactBlocked,
            FrameworkCompatibilityBootstrap.DetermineActivationState(
                new[] { "Butter", "UIExtender", "MCM" }, required));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            FrameworkCompatibilityBootstrap.DetermineActivationState(
                new[] { "Butter", "MCM" }, required));
        Assert.Contains("UIExtender", exception.Message);

        InvalidOperationException implementationOnly = Assert.Throws<InvalidOperationException>(() =>
            FrameworkCompatibilityBootstrap.DetermineActivationState(
                new[] { "MCM.UI.Implementation" },
                required,
                required.Append("MCM.UI.Implementation")));
        Assert.Contains("without their audited base cohort", implementationOnly.Message);
    }

    [Fact]
    public void Fingerprint_PostJoinLocalMcmEditsCannotChangeAuthoritativeGameplayState()
    {
        var before = new Dictionary<string, string>
        {
            ["Diplomacy.EnableWarExhaustion"] = "true",
            ["UnblockableThrust.Enabled"] = "true",
        };
        var edited = new Dictionary<string, string>
        {
            ["Diplomacy.EnableWarExhaustion"] = "false",
            ["UnblockableThrust.Enabled"] = "false",
            ["Injected.ClientOnly.Setting"] = "999999",
        };

        Assert.False(FrameworkSettingsAuthority.AllowsLocalMcmMutation);
        Assert.Equal(
            FrameworkSettingsAuthority.AuthoritativeGameplayFingerprint,
            FrameworkSettingsAuthority.FingerprintAfterLocalMcmView(before));
        Assert.Equal(
            FrameworkSettingsAuthority.FingerprintAfterLocalMcmView(before),
            FrameworkSettingsAuthority.FingerprintAfterLocalMcmView(edited));
    }

    [Fact]
    public void AssemblyGate_RequiresExactNameVersionAndFingerprint()
    {
        Assembly assembly = typeof(FrameworkCompatibilityTests).Assembly;
        AssemblyName assemblyName = assembly.GetName();
        string simpleName = assemblyName.Name ??
            throw new InvalidOperationException("Test assembly has no simple name.");
        Version version = assemblyName.Version ??
            throw new InvalidOperationException("Test assembly has no version.");
        var expected = new FrameworkAssemblyExpectation(
            simpleName,
            version.ToString(),
            new string('a', 64),
            requiredBeforeModuleLoad: true,
            optionalFramework: true);

        FrameworkCompatibilityBootstrap.ValidateExpectedAssembly(
            assembly, expected, _ => new string('a', 64));
        Assert.Throws<InvalidOperationException>(() =>
            FrameworkCompatibilityBootstrap.ValidateExpectedAssembly(
                assembly, expected, _ => new string('b', 64)));
    }

    [Fact]
    public void MethodGate_ResolvesOnlyTheAuditedOverload()
    {
        Assembly assembly = typeof(FrameworkCompatibilityTests).Assembly;
        string assemblyName = assembly.GetName().Name ??
            throw new InvalidOperationException("Test assembly has no simple name.");
        string probeTypeName = typeof(ShapeProbe).FullName ??
            throw new InvalidOperationException("Test probe type has no full name.");
        var assemblies = new Dictionary<string, Assembly>
        {
            [assemblyName] = assembly,
        };
        var exact = new FrameworkMethodExpectation(
            assemblyName,
            probeTypeName,
            nameof(ShapeProbe.Mutate),
            "System.Void",
            "System.String");

        MethodInfo resolved = FrameworkCompatibilityBootstrap.ResolveExactMethod(assemblies, exact);
        Assert.Equal(typeof(string), Assert.Single(resolved.GetParameters()).ParameterType);

        var drifted = new FrameworkMethodExpectation(
            assemblyName,
            probeTypeName,
            nameof(ShapeProbe.Mutate),
            "System.Void",
            "System.Boolean");
        Assert.Throws<InvalidOperationException>(() =>
            FrameworkCompatibilityBootstrap.ResolveExactMethod(assemblies, drifted));
    }

    [Fact]
    public void HarmonyIsolation_RemovesFrameworkOwnerAndLeavesAdapterOwner()
    {
        string originalOwner = "Bannerlord.ButterLib.SaveSystem";
        string adapterOwner = FrameworkCompatibilityManifest.AdapterHarmonyId;
        var originalHarmony = new Harmony(originalOwner);
        var adapterHarmony = new Harmony(adapterOwner);
        var cleanupHarmony = new Harmony("coop.tests.framework.cleanup." + Guid.NewGuid().ToString("N"));
        MethodInfo original = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.Target), BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Test target method is missing.");
        MethodInfo prefix = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.Prefix), BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Test framework prefix is missing.");
        MethodInfo adapterPrefix = typeof(ShapeProbe).GetMethod(
            nameof(ShapeProbe.AdapterPrefix), BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Test adapter prefix is missing.");

        try
        {
            originalHarmony.Patch(original, prefix: new HarmonyMethod(prefix));
            adapterHarmony.Patch(original, prefix: new HarmonyMethod(adapterPrefix));
            Assert.NotEmpty(FrameworkHarmonyIsolation.DescribeOriginalFrameworkPatches(null));

            Assert.True(FrameworkHarmonyIsolation.RemoveOriginalFrameworkPatches(
                null, cleanupHarmony) >= 1);
            FrameworkHarmonyIsolation.AssertNoOriginalFrameworkPatches(null);

            Patches remaining = Harmony.GetPatchInfo(original) ??
                throw new InvalidOperationException("Adapter patch inventory unexpectedly disappeared.");
            Assert.Contains(remaining.Prefixes, patch => patch.owner == adapterOwner);
            Assert.DoesNotContain(remaining.Prefixes, patch => patch.owner == originalOwner);
        }
        finally
        {
            originalHarmony.UnpatchAll(originalOwner);
            adapterHarmony.UnpatchAll(adapterOwner);
            cleanupHarmony.UnpatchAll(cleanupHarmony.Id);
        }
    }

    [Theory]
    [InlineData("butterlib.delayedsubmoduleloader.static", true)]
    [InlineData("Bannerlord.ButterLib.ObjectSystem", true)]
    [InlineData("bannerlord.uiextender.ex.viewmodels.MCM.UI", true)]
    [InlineData("Bannerlord.MBOptionScreen", true)]
    [InlineData("MCM.UI.Adapter.MCMv5", true)]
    [InlineData("bannerlord.mcm.ui.optionsswitchpatch", true)]
    [InlineData(FrameworkCompatibilityManifest.AdapterHarmonyId, false)]
    [InlineData("Bannerlord.Harmony", false)]
    [InlineData("Bannerlord.Coop", false)]
    public void HarmonyOwnerInventory_IsExactAndNeverPurgesTheAdapter(string owner, bool expected)
    {
        Assert.Equal(expected, FrameworkHarmonyIsolation.IsOriginalOwner(owner));
    }

    [Fact]
    public void GuardInventory_CoversLifecycleSaveAndEveryPrimitiveMcmEditor()
    {
        string[] identities = FrameworkCompatibilityManifest.GuardedMethods
            .Select(method => method.Identity)
            .ToArray();

        Assert.Contains(identities, identity => identity.Contains("PerSaveCampaignBehavior.SyncData"));
        Assert.Contains(identities, identity => identity.Contains("DefaultSettingsProvider.SaveSettings"));
        Assert.Contains(identities, identity => identity.Contains("SettingsPropertyVM.set_BoolValue"));
        Assert.Contains(identities, identity => identity.Contains("SettingsPropertyVM.set_FloatValue"));
        Assert.Contains(identities, identity => identity.Contains("SettingsPropertyVM.set_IntValue"));
        Assert.Contains(identities, identity => identity.Contains("SettingsPropertyVM.set_StringValue"));
        Assert.Contains(identities, identity => identity.Contains("UIExtender.Enable"));
        Assert.Contains(identities, identity => identity.Contains("ButterLibSubModule.OnApplicationTick"));
        Assert.Contains(identities, identity => identity.Contains("ButterLibSubModule.OnGameStart"));
        Assert.Contains(identities, identity => identity.Contains("Implementation.SubModule.OnGameStart"));
    }

    [Fact]
    public void ActiveFrameworkCohort_IsExplicitlyBlockedAndInventoriesNonDisableableButterSubsystems()
    {
        Assert.False(FrameworkCompatibilityManifest.OptionalFrameworkActivationAllowed);
        Assert.Contains(
            "Bannerlord.ButterLib.DelayedSubModule.DelayedSubModuleSubSystem",
            FrameworkCompatibilityManifest.ButterSubsystemTypes);
        Assert.Contains(
            "Bannerlord.ButterLib.SubModuleWrappers2.SubModuleWrappers2SubSystem",
            FrameworkCompatibilityManifest.ButterSubsystemTypes);
    }

    private static void AssertAssembly(
        FrameworkAssemblyExpectation expectation,
        string name,
        string version,
        bool optional)
    {
        Assert.Equal(name, expectation.AssemblyName);
        Assert.Equal(Version.Parse(version), expectation.Version);
        Assert.Equal(optional, expectation.OptionalFramework);
        Assert.Matches("^[0-9a-f]{64}$", expectation.Sha256);
    }

    private static class ShapeProbe
    {
        internal static void Target()
        {
        }

        internal static bool Prefix() => true;
        internal static bool AdapterPrefix() => true;
        internal static void Mutate(string value) => _ = value;
        internal static void Mutate(int value) => _ = value;
    }
}
