using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

/// <summary>
/// Serializes every test that touches the Fourberie runtime double. Its state lives in statics on
/// <c>global::Fourberie.FourberieBehavior</c> — the shape the real mod exposes — and several tests
/// call <c>Reset()</c> or assign those fields directly. xUnit gives each test class its own
/// collection by default and runs collections in parallel, so before this the classes clobbered
/// each other: capture and restore tests failed intermittently with
/// <c>KeyNotFoundException</c> as a neighbour wiped the dictionary mid-assertion, and which test
/// failed changed between runs. The full-suite run happened to pass, so the races were invisible
/// in CI while masking real regressions in snapshot and restore.
///
/// Every test class in this folder must carry <c>[Collection(FourberieRuntimeCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(FourberieRuntimeCollection.Name, DisableParallelization = true)]
public sealed class FourberieRuntimeCollection
{
    public const string Name = nameof(FourberieRuntimeCollection);
}
