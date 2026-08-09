using E2E.Tests.Environment;
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
}
