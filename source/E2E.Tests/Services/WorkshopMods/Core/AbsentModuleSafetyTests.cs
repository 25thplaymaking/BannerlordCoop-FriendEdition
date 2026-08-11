using E2E.Tests.Environment;
using GameInterface.Services.WorkshopMods.Core;
using Missions;
using System.Reflection;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Core;

public sealed class AbsentModuleSafetyTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }

    public AbsentModuleSafetyTests(ITestOutputHelper output)
    {
        // Constructing the environment runs GameInterface.PatchAll() on every instance.
        // With no Workshop mod installed in the harness, that must not throw.
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void PatchAll_WithNoWorkshopModulesInstalled_DoesNotThrow()
    {
        Assert.NotNull(TestEnvironment.Server);
        Assert.NotEmpty(TestEnvironment.Clients);
    }

    [Fact]
    public void MissionModule_DeclaresOnlyActiveCombatGameplayModules()
    {
        var field = typeof(MissionModule).GetField(
            "DeclaredWorkshopModules",
            BindingFlags.Static | BindingFlags.NonPublic);
        var modules = Assert.IsType<IWorkshopModule[]>(field?.GetValue(null));

        Assert.Equal(
            new[] { "DismembermentPlus", "UnblockableThrust" },
            modules.Select(module => module.ModuleId));
        Assert.DoesNotContain(modules, module => module.ModuleId == "RBM");
    }
}
