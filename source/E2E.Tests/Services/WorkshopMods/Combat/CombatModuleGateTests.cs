using GameInterface.Services.WorkshopMods.Core;
using Missions.WorkshopMods.Combat;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Combat;

/// <summary>
/// The three combat mods through the same gates as Diplomacy. Each file is the whole cost of
/// covering a mod once its <see cref="IWorkshopModule"/> exists — three derived classes replacing
/// what used to be an assembly-name presence check with no coverage of its own.
/// </summary>
public sealed class RbmModuleGateTests : WorkshopModuleTestBase
{
    public RbmModuleGateTests(ITestOutputHelper output) : base(output) { }

    protected override IWorkshopModule Module => new RbmModule();
}

public sealed class DismembermentPlusModuleGateTests : WorkshopModuleTestBase
{
    public DismembermentPlusModuleGateTests(ITestOutputHelper output) : base(output) { }

    protected override IWorkshopModule Module => new DismembermentPlusModule();
}

/// <summary>
/// UnblockableThrust declares a null patch category on purpose — its adapter patches a native method
/// that always resolves and must keep applying when the mod is absent. Every other gate still binds,
/// which is the point of declaring it at all.
/// </summary>
public sealed class UnblockableThrustModuleGateTests : WorkshopModuleTestBase
{
    public UnblockableThrustModuleGateTests(ITestOutputHelper output) : base(output) { }

    protected override IWorkshopModule Module => new UnblockableThrustModule();
}
