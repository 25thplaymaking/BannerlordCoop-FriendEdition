using GameInterface.Services.WorkshopMods.Fourberie;
using HarmonyLib;
using System;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieHarmonyIsolationTests
{
    private static void Original()
    {
    }

    private static bool Prefix() => true;

    [Fact]
    public void ApprovedCreatorBinary_DeclaresNoHarmonyOwnersOrPatchMethods()
    {
        Assert.Empty(FourberieHarmonyIsolation.AuditedOwnerIds);
        Assert.Empty(FourberieHarmonyIsolation.AuditedPatchIdentities);
        Assert.False(FourberieCompatibilityManifest.ApprovedBinaryDeclaresHarmonySurface);
        Assert.Equal("Bannerlord.Coop.Workshop.Fourberie", FourberieCompatibilityManifest.AdapterHarmonyId);
    }

    [Fact]
    public void PatchImplementationIdentity_UsesExactAssemblyReference()
    {
        var prefix = typeof(FourberieHarmonyIsolationTests).GetMethod(
            nameof(Prefix),
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.True(FourberieHarmonyIsolation.IsImplementedBy(
            prefix,
            typeof(FourberieHarmonyIsolationTests).Assembly));
        Assert.False(FourberieHarmonyIsolation.IsImplementedBy(
            prefix,
            typeof(FourberieCompatibilityManifest).Assembly));
    }

    [Fact]
    public void AdapterGuardAssertion_RequiresExactOwnerAndMethodInventory()
    {
        var owner = "tests.fourberie.guard." + Guid.NewGuid().ToString("N");
        var harmony = new Harmony(owner);
        var original = typeof(FourberieHarmonyIsolationTests).GetMethod(
            nameof(Original),
            BindingFlags.Static | BindingFlags.NonPublic);
        var prefix = typeof(FourberieHarmonyIsolationTests).GetMethod(
            nameof(Prefix),
            BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            FourberieHarmonyIsolation.AssertOnlyAdapterGuards(
                new[] { (Original: original, Prefix: prefix, Postfix: (MethodInfo)null) },
                owner);
            Assert.Throws<InvalidOperationException>(() =>
                FourberieHarmonyIsolation.AssertOnlyAdapterGuards(
                    new[] { (Original: original, Prefix: prefix, Postfix: (MethodInfo)null) },
                    owner + ".wrong"));
        }
        finally
        {
            harmony.Unpatch(original, prefix);
        }
    }
}
