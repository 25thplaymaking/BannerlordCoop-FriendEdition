using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.WorkshopMods.Diplomacy;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy through the shared gates. The whole file is the cost of covering a mod once its
/// <see cref="IWorkshopModule"/> exists — that is the point of <see cref="WorkshopModuleTestBase"/>.
/// </summary>
public sealed class DiplomacyModuleGateTests : WorkshopModuleTestBase
{
    public DiplomacyModuleGateTests(ITestOutputHelper output) : base(output) { }

    protected override IWorkshopModule Module => new DiplomacyModule();
}
